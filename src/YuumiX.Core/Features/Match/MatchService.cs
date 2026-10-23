using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Common;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Lcu.Sgp;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Features.Match;

/// <summary>
/// <see cref="IMatchService"/> 的 LCU 实现：取数、分页、去重缓存、状态推送。
///
/// 四条实现纪律（对应 <see cref="IMatchService"/> 的契约）：
/// <list type="number">
///   <item><b>复用 <c>LcuConnection.Transport</c></b>，不自建 <c>HttpClient</c>：
///         并发限流（<c>ApiConcurrencyNumber</c>）与证书忽略都在传输层，
///         绕开它等于把"客户端被自己打满"这个老问题重新引入。用提供器是因为重连会换传输层。</item>
///   <item><b>失败不抛异常。</b>界面不该为战绩写 try/catch：列表失败给空页、详情失败给 <c>null</c>，
///         原因经 <see cref="Status"/> 推送。唯一的例外是取消（<see cref="OperationCanceledException"/> 照常抛）。</item>
///   <item><b>详情按 gameId 缓存，但"查询者胜负"不缓存。</b>同一局对 A 是胜、对 B 是负，
///         缓存整份结果会把 A 的结论发给 B。故缓存与查询者无关的版本，胜负按 puuid 现场补算。</item>
///   <item><b>同参数并发只打一次 HTTP。</b>两个组件同时要第 1 页时共享一次请求；
///         请求结束即移出表，下一次调用重新取——缓存只在"飞行中"存在，不会端出过期数据。</item>
/// </list>
/// </summary>
public sealed class MatchService : IMatchService
{
    /// <summary>列表路径模板（参数：puuid、begIndex、endIndex）。</summary>
    internal const string HistoryPathTemplate =
        "/lol-match-history/v1/products/lol/{0}/matches?begIndex={1}&endIndex={2}";

    /// <summary>详情路径模板（参数：gameId）。</summary>
    internal const string DetailPathTemplate = "/lol-match-history/v1/games/{0}";

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly ISgpService? _sgpService;
    private readonly Func<LcuCredentials?>? _credentialsProvider;
    private readonly ILogger<MatchService>? _logger;
    private readonly EventStream<MatchServiceStatus> _status;

    /// <summary>详情缓存：已结束的对局不会变，可以长期复用，直到 <see cref="InvalidateCache"/>。</summary>
    private readonly ConcurrentDictionary<long, Lazy<Task<MatchDetail?>>> _details = new();

    /// <summary>飞行中的分页请求：同参数并发共享一次 HTTP，结束后立即移除。</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<MatchHistoryPage>>> _pages = new(StringComparer.Ordinal);

    /// <summary>
    /// 玩家战绩池缓存：按 puuid 存储已拉取并去重的战绩流。
    /// 支撑前 2 页由 LCU 预拉 20 场、第 2 页触发 SGP 拉取第 21 场起的增量加载机制。
    /// </summary>
    private sealed class PlayerMatchState
    {
        public List<MatchSummary> Games { get; } = [];
        public HashSet<long> SeenGameIds { get; } = [];
        public int LoadedGameIndex { get; set; }
        public int? TotalCount { get; set; }
        public bool HasReachedEnd { get; set; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, PlayerMatchState> _playerStates = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="transportProvider">当前传输层（来自 <c>LcuConnection.Transport</c>）；未连接时返回 <c>null</c>。</param>
    /// <param name="sgpService">可选 SGP 腾讯国服外网服务，用于超出 20 条战绩后的增量分页拉取。</param>
    /// <param name="credentialsProvider">可选连接凭证提供器（获取当前大区标识）。</param>
    /// <param name="logger">可选日志。</param>
    public MatchService(
        Func<ILcuTransport?> transportProvider,
        ISgpService? sgpService = null,
        Func<LcuCredentials?>? credentialsProvider = null,
        ILogger<MatchService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);

        _transportProvider = transportProvider;
        _sgpService = sgpService;
        _credentialsProvider = credentialsProvider;
        _logger = logger;
        _status = new EventStream<MatchServiceStatus>(
            ex => _logger?.LogWarning(ex, "战绩状态观察者抛出异常，已隔离"));
    }

    public EventStream<MatchServiceStatus> Status => _status;

    /// <summary>当前缓存的详情条数（诊断与测试用）。</summary>
    internal int CachedDetailCount => _details.Count;

    /// <summary>当前飞行中的分页请求数（诊断与测试用）。</summary>
    internal int InFlightPageCount => _pages.Count;

    public async Task<MatchHistoryPage> GetMatchHistoryAsync(
        string puuid,
        int beginIndex,
        int endIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(puuid);
        ArgumentOutOfRangeException.ThrowIfNegative(beginIndex);

        if (endIndex < beginIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(endIndex), endIndex, "结束下标不能小于起始下标");
        }

        string key = string.Create(CultureInfo.InvariantCulture, $"{puuid}|{beginIndex}|{endIndex}");

        Lazy<Task<MatchHistoryPage>> entry = _pages.GetOrAdd(
            key,
            _ => new Lazy<Task<MatchHistoryPage>>(
                () => FetchPageAsync(puuid, beginIndex, endIndex, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await entry.Value.ConfigureAwait(false);
        }
        finally
        {
            // 只移除自己那一项：并发场景下后来者可能已经放进新的 Lazy
            _pages.TryRemove(new KeyValuePair<string, Lazy<Task<MatchHistoryPage>>>(key, entry));
        }
    }

    public Task<MatchDetail?> GetMatchDetailAsync(
        long gameId,
        string? queriedPuuid = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gameId);

        Lazy<Task<MatchDetail?>> entry = _details.GetOrAdd(
            gameId,
            id => new Lazy<Task<MatchDetail?>>(
                () => FetchDetailAsync(id, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return ApplyQueriedPlayerAsync(entry, queriedPuuid);
    }

    public void InvalidateCache()
    {
        int details = _details.Count;
        _details.Clear();
        _pages.Clear();
        _playerStates.Clear();
        _logger?.LogDebug("战绩缓存已清空（详情 {Count} 条）", details);
    }

    /// <summary>
    /// 分页结果的 <c>HasMore</c>：有 <c>gameCount</c> 时按总数判断；
    /// 没有时退回"本页是否填满"，宁可多翻一页也不要漏掉最后一页。
    /// </summary>
    internal static bool HasMore(int? totalCount, int beginIndex, int endIndex, int returnedCount)
    {
        if (totalCount is { } total)
        {
            return endIndex + 1 < total;
        }

        int requested = endIndex - beginIndex + 1;
        return requested > 0 && returnedCount >= requested;
    }

    private async Task<MatchHistoryPage> FetchPageAsync(
        string puuid,
        int beginIndex,
        int endIndex,
        CancellationToken cancellationToken)
    {
        PlayerMatchState state = _playerStates.GetOrAdd(puuid, _ => new PlayerMatchState());
        await state.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 1. 若当前池子为空且未标记到达尽头：首次拉取前 20 条（LCU 0~19）
            if (state.Games.Count == 0 && !state.HasReachedEnd)
            {
                int initialBeg = (beginIndex == 0) ? 0 : beginIndex;
                int initialEnd = (beginIndex == 0) ? Math.Max(19, endIndex) : endIndex;

                var (lcuGames, totalCount) = await FetchLcuGamesAsync(puuid, initialBeg, initialEnd, cancellationToken).ConfigureAwait(false);
                if (totalCount.HasValue)
                {
                    state.TotalCount = totalCount;
                }

                foreach (MatchSummary summary in lcuGames)
                {
                    if (state.SeenGameIds.Add(summary.GameId))
                    {
                        state.Games.Add(summary);
                    }
                }

                state.LoadedGameIndex = Math.Max(initialEnd + 1, state.Games.Count);
                // 首次请求 0..19 若返回不满 20 条（例如只有 1 场），说明该账号全部战绩就只有这几场，直接标记到底
                if (lcuGames.Count < (initialEnd - initialBeg + 1))
                {
                    state.HasReachedEnd = true;
                }
            }

            // 2. 检查大区与 SGP 状态
            string? server = _credentialsProvider?.Invoke()?.Server;
            bool sgpAvailable = _sgpService is not null && _sgpService.IsSupported(server);

            // 当用户点到第 2 页（beginIndex >= 10）时，触发从第 21 条（LoadedGameIndex = 20）开始拉取 30 条！
            // 对应原项目 LOAD_MORE_COUNT = 30。仅在请求第 2 页或更后页时触发
            const int loadMoreBatchSize = 30;
            while (beginIndex >= 10 && !state.HasReachedEnd &&
                   (state.LoadedGameIndex <= 20 || beginIndex >= state.Games.Count))
            {
                int fetchBeg = Math.Max(20, state.LoadedGameIndex);
                int fetchCount = loadMoreBatchSize;

                IReadOnlyList<MatchSummary> newBatch = [];

                if (sgpAvailable)
                {
                    _status.Publish(MatchServiceStatus.Loading($"正在通过腾讯 SGP 接口查询第 {fetchBeg + 1} 场起的数据"));
                    newBatch = await _sgpService!.GetMatchHistoryAsync(
                        server,
                        puuid,
                        fetchBeg,
                        fetchCount,
                        cancellationToken).ConfigureAwait(false);
                }

                // 若非腾讯服，或 SGP 未命中任何数据，则降级尝试 LCU
                if (newBatch.Count == 0)
                {
                    var (lcuBatch, _) = await FetchLcuGamesAsync(
                        puuid,
                        fetchBeg,
                        fetchBeg + fetchCount - 1,
                        cancellationToken).ConfigureAwait(false);
                    newBatch = lcuBatch;
                }

                int addedCount = 0;
                foreach (MatchSummary summary in newBatch)
                {
                    if (state.SeenGameIds.Add(summary.GameId))
                    {
                        state.Games.Add(summary);
                        addedCount++;
                    }
                }

                state.LoadedGameIndex = fetchBeg + fetchCount;

                // 若两方都拉不到任何新对局，或返回不满预期条数，说明已触达服务端尽头
                if (addedCount == 0 || newBatch.Count < fetchCount)
                {
                    state.HasReachedEnd = true;
                    break;
                }
            }

            // 3. 切片返回
            if (beginIndex >= state.Games.Count)
            {
                return new MatchHistoryPage
                {
                    Games = [],
                    BeginIndex = beginIndex,
                    EndIndex = endIndex,
                    TotalCount = state.TotalCount,
                    HasMore = false,
                };
            }

            int countToTake = Math.Min(endIndex - beginIndex + 1, state.Games.Count - beginIndex);
            var slice = state.Games.GetRange(beginIndex, countToTake);

            // 核心修复：只要池子里还有后续数据，或者尚未确认到达远端尽头，就允许向后翻页！
            bool hasMore = (state.Games.Count > endIndex + 1) || (!state.HasReachedEnd);

            _status.Publish(MatchServiceStatus.Loaded($"已加载 {slice.Count} 场对局"));

            return new MatchHistoryPage
            {
                Games = slice,
                BeginIndex = beginIndex,
                EndIndex = endIndex,
                TotalCount = state.TotalCount,
                HasMore = hasMore,
            };
        }
        finally
        {
            state.Lock.Release();
        }
    }

    private async Task<(IReadOnlyList<MatchSummary> Games, int? TotalCount)> FetchLcuGamesAsync(
        string puuid,
        int beginIndex,
        int endIndex,
        CancellationToken cancellationToken)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            _status.Publish(MatchServiceStatus.Failed("客户端未连接，无法读取战绩"));
            return ([], null);
        }

        _status.Publish(MatchServiceStatus.Loading($"正在拉取第 {beginIndex}–{endIndex} 场"));

        string path = string.Format(
            CultureInfo.InvariantCulture,
            HistoryPathTemplate,
            Uri.EscapeDataString(puuid),
            beginIndex,
            endIndex);

        LcuResponse response;
        try
        {
            response = await transport.SendAsync(LcuRequest.Get(path), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _status.Publish(MatchServiceStatus.Idle);
            throw;
        }

        if (response.IsNotFound)
        {
            _status.Publish(MatchServiceStatus.Failed("玩家不存在，或该账号的战绩不可见"));
            return ([], null);
        }

        if (!response.IsSuccess)
        {
            _status.Publish(MatchServiceStatus.Failed($"读取战绩失败（HTTP {response.StatusCode}）"));
            return ([], null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);

            IReadOnlyList<MatchSummary> parsed = MatchParser.ParseHistory(document.RootElement);
            int? totalCount = MatchParser.ParseGameCount(document.RootElement);

            // 翻页边界上 LCU 偶发重复下发同一局：按 gameId 去重并保持原顺序
            var seen = new HashSet<long>();
            var games = new List<MatchSummary>(parsed.Count);
            foreach (MatchSummary summary in parsed)
            {
                if (seen.Add(summary.GameId))
                {
                    games.Add(summary);
                }
            }

            return (games, totalCount);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "战绩载荷解析失败（{Bytes} 字节）", response.Body.Length);
            _status.Publish(MatchServiceStatus.Failed("战绩载荷解析失败，可能是客户端版本变更"));
            return ([], null);
        }
    }

    private async Task<MatchDetail?> FetchDetailAsync(long gameId, CancellationToken cancellationToken)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            _status.Publish(MatchServiceStatus.Failed("客户端未连接，无法读取对局详情"));
            return null;
        }

        _status.Publish(MatchServiceStatus.Loading($"正在读取对局 {gameId} 的详情"));

        string path = string.Format(CultureInfo.InvariantCulture, DetailPathTemplate, gameId);

        LcuResponse response;
        try
        {
            response = await transport.SendAsync(LcuRequest.Get(path), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _status.Publish(MatchServiceStatus.Idle);
            throw;
        }

        if (response.IsNotFound)
        {
            // 对局被清理或 gameId 归属其它大区，属常态：不能让详情失败污染列表状态
            _logger?.LogInformation("对局 {GameId} 详情不存在（HTTP 404）", gameId);
            _status.Publish(MatchServiceStatus.Failed($"未找到对局 {gameId}，可能已被客户端清理"));
            return null;
        }

        if (!response.IsSuccess)
        {
            _status.Publish(MatchServiceStatus.Failed($"读取对局详情失败（HTTP {response.StatusCode}）"));
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);

            // 刻意不传 queriedPuuid：缓存里存的是与"谁在查"无关的版本
            MatchDetail? detail = MatchParser.ParseDetail(document.RootElement);
            if (detail is null)
            {
                _status.Publish(MatchServiceStatus.Failed($"对局 {gameId} 没有参与者数据"));
                return null;
            }

            _status.Publish(MatchServiceStatus.Loaded($"对局 {gameId} 详情已加载"));
            return detail;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "对局详情载荷解析失败: {GameId}", gameId);
            _status.Publish(MatchServiceStatus.Failed("对局详情解析失败，可能是客户端版本变更"));
            return null;
        }
    }

    /// <summary>
    /// 等待详情并按查询者补算胜负。
    ///
    /// 详情缓存以 gameId 为键，而"这场对我是胜是负"取决于查询者，因此不能连结论一起缓存——
    /// 否则 A 先查过、B 再查同一局会看到 A 的胜负。
    /// </summary>
    private static async Task<MatchDetail?> ApplyQueriedPlayerAsync(
        Lazy<Task<MatchDetail?>> entry,
        string? queriedPuuid)
    {
        MatchDetail? detail = await entry.Value.ConfigureAwait(false);

        if (detail is null || string.IsNullOrEmpty(queriedPuuid))
        {
            return detail;
        }

        return detail with { QueriedPlayerWin = FindQueriedPlayerWin(detail, queriedPuuid) };
    }

    private static bool? FindQueriedPlayerWin(MatchDetail detail, string puuid)
    {
        foreach (MatchDetailTeam team in new[] { detail.Blue, detail.Red })
        {
            foreach (MatchDetailPlayer player in team.Players)
            {
                if (string.Equals(player.Puuid, puuid, StringComparison.Ordinal))
                {
                    return player.Win;
                }
            }
        }

        return null;
    }
}
