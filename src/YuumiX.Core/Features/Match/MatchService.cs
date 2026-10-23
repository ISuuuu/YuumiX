using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Common;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Lcu;
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
    private readonly ILogger<MatchService>? _logger;
    private readonly EventStream<MatchServiceStatus> _status;

    /// <summary>详情缓存：已结束的对局不会变，可以长期复用，直到 <see cref="InvalidateCache"/>。</summary>
    private readonly ConcurrentDictionary<long, Lazy<Task<MatchDetail?>>> _details = new();

    /// <summary>飞行中的分页请求：同参数并发共享一次 HTTP，结束后立即移除。</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<MatchHistoryPage>>> _pages = new(StringComparer.Ordinal);

    /// <param name="transportProvider">当前传输层（来自 <c>LcuConnection.Transport</c>）；未连接时返回 <c>null</c>。</param>
    /// <param name="logger">可选日志。</param>
    public MatchService(Func<ILcuTransport?> transportProvider, ILogger<MatchService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);

        _transportProvider = transportProvider;
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
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            _status.Publish(MatchServiceStatus.Failed("客户端未连接，无法读取战绩"));
            return MatchHistoryPage.Empty;
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
            return MatchHistoryPage.Empty;
        }

        if (!response.IsSuccess)
        {
            _status.Publish(MatchServiceStatus.Failed($"读取战绩失败（HTTP {response.StatusCode}）"));
            return MatchHistoryPage.Empty;
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

            MatchHistoryPage page = new()
            {
                Games = games,
                BeginIndex = beginIndex,
                EndIndex = endIndex,
                TotalCount = totalCount,
                HasMore = HasMore(totalCount, beginIndex, endIndex, games.Count),
            };

            _status.Publish(MatchServiceStatus.Loaded($"已加载 {games.Count} 场对局"));
            return page;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "战绩载荷解析失败（{Bytes} 字节）", response.Body.Length);
            _status.Publish(MatchServiceStatus.Failed("战绩载荷解析失败，可能是客户端版本变更"));
            return MatchHistoryPage.Empty;
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
