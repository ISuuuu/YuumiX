using Microsoft.Extensions.Logging;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Lcu.Sgp;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 玩家战绩后台扩充（11~50 局低优先级队列）、当前玩家战绩同步（10s 节流 + 单飞）
/// 与对局结束链式轮询（2s/3.5s/5s）协作者（§4.1 / §4.10）。
/// </summary>
internal sealed class GameInfoMatchExpander : IDisposable
{
    private readonly Lock _gate = new();
    private readonly IMatchService _matchService;
    private readonly ISgpService? _sgpService;
    private readonly ISummonerService _summonerService;
    private readonly AppConfig _config;
    private readonly ILogger? _logger;
    private readonly GameInfoStateStore _stateStore;
    private readonly Func<bool> _isDisposed;

    /// <summary>低优先级战绩扩充顺序队列（单任务串行，排在首屏加载之后）。</summary>
    private readonly SemaphoreSlim _extraMatchesGate = new(1, 1);

    private int _extraMatchesProcessed;

    // 当前玩家战绩同步状态（10s 节流 + 单飞）
    private bool _isSyncingCurrentPlayerMatches;
    private long _lastCurrentPlayerSyncTick;

    // 对局结束链式轮询（2s/3.5s/5s，可安全取消）
    private CancellationTokenSource? _gameEndedChainCts;

    /// <summary>对局结束链式轮询间隔（毫秒），测试可注入缩短。</summary>
    internal int[] GameEndedChainDelaysMs { get; set; } = [2000, 3500, 5000];

    /// <summary>对局结束链式轮询是否仍在运行（测试用）。</summary>
    internal bool IsGameEndedChainActive
    {
        get { lock (_gate) { return _gameEndedChainCts is not null; } }
    }

    /// <summary>战绩扩充顺序队列是否空闲（测试用：等待后台扩充完成）。</summary>
    internal bool ExtraMatchesIdle => _extraMatchesGate.CurrentCount > 0;

    /// <summary>已完成的战绩扩充任务数（测试用：等待后台扩充处理完毕）。</summary>
    internal int ExtraMatchesProcessedCount => Volatile.Read(ref _extraMatchesProcessed);

    public GameInfoMatchExpander(
        IMatchService matchService,
        ISgpService? sgpService,
        ISummonerService summonerService,
        AppConfig config,
        ILogger? logger,
        GameInfoStateStore stateStore,
        Func<bool> isDisposed)
    {
        _matchService = matchService;
        _sgpService = sgpService;
        _summonerService = summonerService;
        _config = config;
        _logger = logger;
        _stateStore = stateStore;
        _isDisposed = isDisposed;
    }

    /// <summary>GameInfoFilter 开启时按 CurrentQueueId 过滤战绩；CurrentQueueId 无效时不过滤。</summary>
    internal List<MatchSummary> ApplyQueueFilter(IReadOnlyList<MatchSummary> matches)
    {
        if (!_config.Functions.GameInfoFilter)
        {
            return matches.ToList();
        }

        int? queueId = _stateStore.CurrentState.CurrentQueueId is int q && q > 0 ? q : null;
        return queueId is null
            ? matches.ToList()
            : matches.Where(m => m.QueueId == queueId.Value).ToList();
    }

    /// <summary>低优先级战绩扩充任务入队（单任务顺序执行，排在首屏之后）。</summary>
    internal async Task EnqueueExtraMatchesAsync(GameInfoPlayer player, IReadOnlyList<MatchSummary> initialMatches)
    {
        await _extraMatchesGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ExpandPlayerMatchesAsync(player, initialMatches).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 后台扩充失败静默降级
        }
        finally
        {
            _extraMatchesGate.Release();
            Interlocked.Increment(ref _extraMatchesProcessed);
        }
    }

    /// <summary>
    /// 战绩样本 11~50 局后台扩充（§4.1 第二阶段）：先 IMatchService 拉 10..49；
    /// 结果不足 20 条或失败时用 ISgpService 补拉同区间并按 gameId 并集合并（SGP 覆盖重合局）；
    /// 与已有前 10 局合并去重、时间降序；总数增长才重算统计字段——不改变 Matches 列表内容。
    /// </summary>
    private async Task ExpandPlayerMatchesAsync(GameInfoPlayer player, IReadOnlyList<MatchSummary> initialMatches)
    {
        string puuid = player.Info?.Puuid ?? string.Empty;
        if (string.IsNullOrEmpty(puuid))
        {
            return;
        }

        var (capturedGameId, capturedEpoch) = _stateStore.CaptureGameContext();

        // 归属校验：该 puuid 当前条目仍是本次加载的对象且未跨局切换，防补填期间被重载后误写
        if (!_stateStore.IsPlayerEntryCurrent(puuid, player, capturedGameId, capturedEpoch))
        {
            return;
        }

        IReadOnlyList<MatchSummary> extra = [];
        try
        {
            MatchHistoryPage page = await _matchService.GetMatchHistoryAsync(puuid, 10, 49).ConfigureAwait(false);
            extra = page.Games;
        }
        catch (Exception)
        {
            // ignore，走 SGP 补拉
        }

        if (extra.Count < 20 && _sgpService is not null)
        {
            try
            {
                IReadOnlyList<MatchSummary> sgpMatches = await _sgpService
                    .GetMatchHistoryAsync(null, puuid, 10, 40)
                    .ConfigureAwait(false);
                if (sgpMatches.Count > 0)
                {
                    // SGP 与 LCU 结果按 gameId 并集（SGP 覆盖重合局以提供权威 teamPosition）
                    var unionMap = new Dictionary<long, MatchSummary>();
                    foreach (var m in extra)
                    {
                        if (m.GameId != 0)
                        {
                            unionMap[m.GameId] = m;
                        }
                    }

                    foreach (var m in sgpMatches)
                    {
                        if (m.GameId != 0)
                        {
                            unionMap[m.GameId] = m;
                        }
                    }

                    extra = unionMap.Values.ToList();
                }
            }
            catch (Exception)
            {
                // ignore SGP fallback failure
            }
        }

        if (extra.Count == 0)
        {
            return;
        }

        List<MatchSummary> validExtra = ApplyQueueFilter(extra);

        // 前 10 局与扩充样本按 gameId 合并去重，时间降序
        var combined = new Dictionary<long, MatchSummary>();
        foreach (var m in initialMatches)
        {
            combined[m.GameId] = m;
        }

        foreach (var m in validExtra)
        {
            if (!combined.ContainsKey(m.GameId))
            {
                combined[m.GameId] = m;
            }
        }

        var allMatches = combined.Values.OrderByDescending(m => m.GameCreation).ToList();
        if (allMatches.Count <= initialMatches.Count)
        {
            return;
        }

        var stats = MatchStatsCalculator.ComputeMatchStats(allMatches);

        // 写入前归属与局号校验 + 只更新统计字段，不改变 Matches 列表内容（展示仍取前 10）
        _stateStore.TryUpdatePlayerEntryByReference(
            puuid,
            player,
            capturedGameId,
            capturedEpoch,
            current => current with
            {
                WinCount = stats.WinCount,
                LossesCount = stats.LossesCount,
                WinRate = stats.WinRate,
                AvgKda = stats.AvgKda,
                Streak = stats.Streak,
                Positions = stats.Positions,
                TopChampions = stats.TopChampions,
            });
    }

    /// <summary>
    /// 同步当前玩家最新战绩（§4.10）：10s 节流（force 可跳过）；SGP 强制拉 0..49，
    /// 失败回退 IMatchService；GameInfoFilter 开启时按当前队列过滤；
    /// 命中自己的所有条目（puuid/sid/cellId 多键）统一更新前 10 局与全部统计字段并清隐藏标记。
    /// </summary>
    internal async Task SyncCurrentPlayerMatchesAsync(bool forceNetwork = false, CancellationToken cancellationToken = default)
    {
        if (_isDisposed())
        {
            return;
        }

        lock (_gate)
        {
            if (_isSyncingCurrentPlayerMatches)
            {
                return;
            }

            if (!forceNetwork && Environment.TickCount64 - _lastCurrentPlayerSyncTick < 10_000)
            {
                return;
            }

            _isSyncingCurrentPlayerMatches = true;
            _lastCurrentPlayerSyncTick = Environment.TickCount64;
        }

        try
        {
            GameInfoState state = _stateStore.CurrentState;
            string puuid = state.CurrentSummonerPuuid ?? string.Empty;
            long sid = state.CurrentSummonerId;

            if (string.IsNullOrEmpty(puuid) || sid == 0)
            {
                try
                {
                    var current = await _summonerService.GetCurrentSummonerAsync(cancellationToken).ConfigureAwait(false);
                    if (current is not null)
                    {
                        puuid = current.Puuid;
                        sid = current.SummonerId;
                        _stateStore.UpdateState(s => s with
                        {
                            CurrentSummonerId = sid,
                            CurrentSummonerPuuid = puuid,
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // ignore
                }
            }

            if (string.IsNullOrEmpty(puuid))
            {
                return;
            }

            // 命中自己的所有条目（多键索引，唯一对象去重）
            state = _stateStore.CurrentState;
            var myCellKey = state.MyTeam
                .FirstOrDefault(p => (!string.IsNullOrEmpty(p.Puuid) && p.Puuid == puuid) || (sid != 0 && p.SummonerId == sid))
                ?.CellId.ToString();

            var targets = new List<GameInfoPlayer>();
            foreach (var (key, entry) in state.PlayerData)
            {
                if (entry is null || entry.Loading)
                {
                    continue;
                }

                bool hit = (!string.IsNullOrEmpty(entry.Info?.Puuid) && entry.Info.Puuid == puuid) ||
                           (sid != 0 && entry.Info is not null && entry.Info.SummonerId == sid) ||
                           key == puuid ||
                           (sid != 0 && key == sid.ToString()) ||
                           (myCellKey is not null && key == myCellKey);
                if (hit && !targets.Any(t => ReferenceEquals(t, entry)))
                {
                    targets.Add(entry);
                }
            }

            if (targets.Count == 0)
            {
                return;
            }

            // SGP 强制拉 0..49；失败或非腾讯服回退 IMatchService 拉满 50
            IReadOnlyList<MatchSummary> raw = [];
            if (_sgpService is not null)
            {
                try
                {
                    raw = await _sgpService.GetMatchHistoryAsync(null, puuid, 0, 50, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // ignore，走回退
                }
            }

            if (raw.Count == 0)
            {
                try
                {
                    // 回退前先让列表池失效：SGP 不可用（实测 503）时若直接读池，
                    // 「上一场战绩」会停在池子建立那一刻。这条路径只在降级时多一次请求，正常路径不受影响。
                    _matchService.InvalidateHistoryCache();
                    MatchHistoryPage page = await _matchService.GetMatchHistoryAsync(puuid, 0, 49, cancellationToken).ConfigureAwait(false);
                    raw = page.Games;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // ignore
                }
            }

            if (raw.Count == 0)
            {
                return;
            }

            List<MatchSummary> filtered = ApplyQueueFilter(raw);
            if (filtered.Count == 0)
            {
                return;
            }

            var stats = MatchStatsCalculator.ComputeMatchStats(filtered);
            var topMatches = filtered.Take(10).ToList();

            _stateStore.UpdatePlayerEntriesByReferences(
                targets,
                existing => existing with
                {
                    Matches = topMatches,
                    MatchHistoryHidden = false,
                    WinCount = stats.WinCount,
                    LossesCount = stats.LossesCount,
                    WinRate = stats.WinRate,
                    AvgKda = stats.AvgKda,
                    Streak = stats.Streak,
                    Positions = stats.Positions,
                    TopChampions = stats.TopChampions,
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "同步当前玩家最新战绩失败");
        }
        finally
        {
            lock (_gate)
            {
                _isSyncingCurrentPlayerMatches = false;
            }
        }
    }

    /// <summary>
    /// 对局结束通知：启动 2s/3.5s/5s 链式轮询当前玩家战绩。
    /// 阶段重新进入活跃或再次结束时立即取消旧链（先 resolve 再 clear 语义，避免 await 永挂）。
    /// </summary>
    internal void NotifyGameEnded()
    {
        if (_isDisposed())
        {
            return;
        }

        StopGameEndedChain();

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _gameEndedChainCts = cts;
        }

        _ = RunGameEndedChainAsync(cts);
    }

    private async Task RunGameEndedChainAsync(CancellationTokenSource cts)
    {
        try
        {
            foreach (int delay in GameEndedChainDelaysMs)
            {
                try
                {
                    await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (_isDisposed() || cts.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await SyncCurrentPlayerMatchesAsync(forceNetwork: true).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // ignore
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_gameEndedChainCts, cts))
                {
                    _gameEndedChainCts = null;
                }
            }

            try
            {
                cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // 与 StopGameEndedChain 并发取消，忽略
            }
        }
    }

    internal void StopGameEndedChain()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _gameEndedChainCts;
            _gameEndedChainCts = null;
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 链已自然结束并释放，忽略
            }
        }
    }

    public void Dispose()
    {
        StopGameEndedChain();
        _extraMatchesGate.Dispose();
    }
}
