using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Common;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.SavedPlayers;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Lcu;
using YuumiX.Core.Lcu.Sgp;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 对局信息编排服务实现（对应旧版 <c>useGamePlayerData.ts</c>）。
/// 核心职责：阶段状态机驱动、LCU 事件与会话编排，并将状态存储/解析/快照/玩家加载/战绩扩充/LiveClient 补全委托给独立协作者。
/// </summary>
public sealed class GameInfoService : IGameInfoService, IDisposable
{
    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly ISummonerService _summonerService;
    private readonly ILiveClientService? _liveClientService;
    private readonly AppConfig _config;
    private readonly ILogger<GameInfoService>? _logger;

    private readonly Lock _sessionGate = new();
    private readonly IDisposable? _connStateSub;
    private readonly IDisposable? _connEventSub;

    private readonly GameInfoStateStore _stateStore;
    private readonly GameInfoSnapshotCoordinator _snapshotCoordinator;
    private readonly GameInfoMatchExpander _matchExpander;
    private readonly GameInfoPlayerLoader _playerLoader;
    private readonly GameInfoLiveClientEnricher _liveClientEnricher;

    /// <summary>是否经历过本局选人阶段（未经历时进入对局需清残留防上一局污染）。</summary>
    private bool _champSelectSeenForGame;

    /// <summary>
    /// 选人阶段我方队员快照（对应旧版 <c>champSelectTeamSnapshot</c>）。
    /// 关键用途：脱敏对局（排位）中 gameflow session 无法匹配本机玩家时，
    /// 用它反查我方属于 teamOne 还是 teamTwo，避免双方队伍整体错位。
    /// </summary>
    private List<PremadePlayerLike> _champSelectTeamSnapshot = [];

    /// <summary>选人阶段敌方队员快照（对应旧版 <c>champSelectTheirTeamSnapshot</c>）。</summary>
    private List<PremadePlayerLike> _champSelectTheirTeamSnapshot = [];

    /// <summary>gameflow session 推断出的友方队伍（组队 ID 回填用）。</summary>
    private List<PremadePlayerLike> _sessionAllyTeam = [];

    /// <summary>gameflow session 推断出的敌方队伍（组队 ID 回填用）。</summary>
    private List<PremadePlayerLike> _sessionEnemyTeam = [];

    private GamePhase _observedPhase = GamePhase.None;
    private long _currentSessionRequestId;
    private bool _disposed;

    /// <summary>对局结束链式轮询间隔（毫秒），测试可注入缩短。</summary>
    internal int[] GameEndedChainDelaysMs
    {
        get => _matchExpander.GameEndedChainDelaysMs;
        set => _matchExpander.GameEndedChainDelaysMs = value;
    }

    /// <summary>Live Client 轮询重试间隔（毫秒），测试可注入缩短。</summary>
    internal int LiveClientPollDelayMs
    {
        get => _liveClientEnricher.PollDelayMs;
        set => _liveClientEnricher.PollDelayMs = value;
    }

    /// <summary>Live Client 最大轮询重试次数。</summary>
    internal int LiveClientMaxRetries
    {
        get => _liveClientEnricher.MaxRetries;
        set => _liveClientEnricher.MaxRetries = value;
    }

    /// <summary>gameflow session 双方队伍为空时的重试次数（旧版 10 次 × 1s）。</summary>
    internal int EmptyTeamRetryCount { get; set; } = 10;

    /// <summary>gameflow session 仅有单边队伍时的重试次数（旧版 3 次 × 1s）。</summary>
    internal int SingleTeamRetryCount { get; set; } = 3;

    /// <summary>gameflow session 队伍重试间隔（毫秒，旧版 1000ms）。</summary>
    internal int EmptyTeamRetryDelayMs { get; set; } = 1000;

    /// <summary>落盘防抖间隔（毫秒），测试可注入缩短。</summary>
    internal int SnapshotFlushDelayMs
    {
        get => _snapshotCoordinator.SnapshotFlushDelayMs;
        set => _snapshotCoordinator.SnapshotFlushDelayMs = value;
    }

    public GameInfoService(
        Func<ILcuTransport?> transportProvider,
        ISummonerService summonerService,
        IMatchService matchService,
        ICareerService careerService,
        IMasteryService masteryService,
        IFateService fateService,
        AppConfig config,
        LcuConnection? connection = null,
        ILogger<GameInfoService>? logger = null,
        ISgpService? sgpService = null,
        SavedPlayersStore? savedPlayersStore = null,
        ILiveClientService? liveClientService = null,
        Func<GameDataCatalog?>? catalogProvider = null,
        GameInfoSnapshotStore? snapshotStore = null)
    {
        _transportProvider = transportProvider;
        _summonerService = summonerService;
        _liveClientService = liveClientService;
        _config = config;
        _logger = logger;

        _stateStore = new GameInfoStateStore(ex =>
            _logger?.LogWarning(ex, "GameInfo 状态通知发生异常，已隔离"));

        _snapshotCoordinator = new GameInfoSnapshotCoordinator(snapshotStore, logger);

        _matchExpander = new GameInfoMatchExpander(
            matchService,
            sgpService,
            summonerService,
            config,
            logger,
            _stateStore,
            isDisposed: () => Volatile.Read(ref _disposed));

        _playerLoader = new GameInfoPlayerLoader(
            transportProvider,
            matchService,
            careerService,
            masteryService,
            fateService,
            savedPlayersStore,
            config,
            logger,
            _matchExpander,
            _stateStore,
            isDisposed: () => Volatile.Read(ref _disposed),
            writeReservedSnapshot: WriteReservedSnapshot);

        _liveClientEnricher = new GameInfoLiveClientEnricher(
            liveClientService,
            catalogProvider,
            summonerService,
            logger,
            _stateStore,
            isActiveGamePhase: IsGameActivePhaseObserved,
            loadPlayerDataAsync: LoadPlayerDataAsync,
            writeReservedSnapshot: WriteReservedSnapshot);

        if (connection is not null)
        {
            _connStateSub = connection.States.Subscribe(OnConnectionStateChanged);
            _connEventSub = connection.Events.Subscribe(OnLcuEvent);
        }
    }

    public EventStream<GameInfoState> StateStream => _stateStore.StateStream;

    public GameInfoState CurrentState => _stateStore.CurrentState;

    // ── 测试诊断钩子（InternalsVisibleTo=YuumiX.Core.Tests）─────────────────
    internal bool HasReservedSnapshot => _snapshotCoordinator.HasReservedSnapshot;
    internal long? ReservedSnapshotGameId => _snapshotCoordinator.ReservedSnapshotGameId;
    internal int ReservedSnapshotTeamCount => _snapshotCoordinator.ReservedSnapshotTeamCount;
    internal int ReservedSnapshotLoadedCount => _snapshotCoordinator.ReservedSnapshotLoadedCount;
    internal IReadOnlyDictionary<string, GameInfoPlayer>? ReservedSnapshotPlayerData => _snapshotCoordinator.ReservedSnapshotPlayerData;
    internal bool IsGameEndedChainActive => _matchExpander.IsGameEndedChainActive;
    internal bool ExtraMatchesIdle => _matchExpander.ExtraMatchesIdle;
    internal int ExtraMatchesProcessedCount => _matchExpander.ExtraMatchesProcessedCount;

    internal Func<string, CancellationToken, Task<IReadOnlyDictionary<string, SavedPlayerMarker>>>? SavedPlayersMapQueryOverride
    {
        get => _playerLoader.SavedPlayersMapQueryOverride;
        set => _playerLoader.SavedPlayersMapQueryOverride = value;
    }

    internal Task<IReadOnlyDictionary<string, SavedPlayerMarker>> QuerySavedPlayersMapAsync(
        string selfPuuid,
        CancellationToken cancellationToken = default)
        => _playerLoader.QuerySavedPlayersMapAsync(selfPuuid, cancellationToken);

    internal Task<bool> PollLiveClientChampionsOnceForTestAsync(CancellationToken cancellationToken = default)
        => _liveClientEnricher.PollOnceAsync(cancellationToken);

    internal void SetStateForTest(Func<GameInfoState, GameInfoState> updater)
        => _stateStore.SetStateUnconditional(updater);

    public void SetActiveTab(string tab)
        => _stateStore.SetStateUnconditional(s => s with { ActiveTab = tab });

    public void SetViewColumns(int columns)
        => _stateStore.SetStateUnconditional(s => s with { ViewColumns = columns });

    public void LoadDemoState()
        => _stateStore.SetStateUnconditional(_ => GameInfoSampleFactory.CreateSample() with { IsDemo = true, IsDemoData = true });

    public async Task RefreshStateAsync(CancellationToken cancellationToken = default)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            _stateStore.UpdateState(s => s with { Phase = GamePhase.None, ShouldShowContent = false, Error = null });
            return;
        }

        try
        {
            await EnsureCurrentSummonerAsync(cancellationToken).ConfigureAwait(false);

            // 1. 获取当前阶段
            LcuResponse phaseResp = await transport.SendAsync(
                LcuRequest.Get("/lol-gameflow/v1/gameflow-phase"),
                cancellationToken).ConfigureAwait(false);

            GamePhase phase = GamePhase.None;
            if (phaseResp.IsSuccess && !string.IsNullOrWhiteSpace(phaseResp.Body))
            {
                string rawPhase = phaseResp.Body.Trim().Trim('"');
                phase = GamePhaseExtensions.ParseGamePhase(rawPhase);
            }

            // 2. 检查会话与 TFT
            await UpdateQueueAndTftAsync(transport, cancellationToken).ConfigureAwait(false);

            _stateStore.UpdateState(s => s with { Phase = phase });
            HandlePhaseObserved(phase);

            if (phase == GamePhase.ChampSelect)
            {
                await LoadFromChampSelectAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (phase is GamePhase.GameStart or GamePhase.InProgress)
            {
                await LoadFromGameflowSessionAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                HandleOutOfGamePhase(phase);
                // 非活跃阶段刷新时，主动强制同步当前玩家最新战绩
                _ = SyncCurrentPlayerMatchesAsync(forceNetwork: true);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "RefreshStateAsync 发生异常");
            if (_transportProvider() is null)
            {
                _stateStore.UpdateState(s => s with { Phase = GamePhase.None, ShouldShowContent = false, Error = null });
            }
            else
            {
                _stateStore.UpdateState(s => s with { Error = ex.Message });
            }
        }
    }

    public async Task LoadFromGameflowSessionAsync(CancellationToken cancellationToken = default)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return;
        }

        long reqId = Interlocked.Increment(ref _currentSessionRequestId);

        try
        {
            // WebSocket 事件直连入口不经过 RefreshStateAsync（旧版 processTeamData 同款守卫）：
            // 本机召唤师身份缺失时，teamOne/teamTwo 的成员匹配会整体失效，直接导致双方队伍错位
            await EnsureCurrentSummonerAsync(cancellationToken).ConfigureAwait(false);

            LcuResponse resp = await transport.SendAsync(
                LcuRequest.Get("/lol-gameflow/v1/session"),
                cancellationToken).ConfigureAwait(false);

            if (!resp.IsSuccess || string.IsNullOrWhiteSpace(resp.Body))
            {
                return;
            }

            if (reqId != Interlocked.Read(ref _currentSessionRequestId))
            {
                return;
            }

            using var doc = JsonDocument.Parse(resp.Body);
            if (!doc.RootElement.TryGetProperty("gameData", out var gameData))
            {
                return;
            }

            var fetched = GameInfoSessionParser.ParseGameflowGameData(gameData);
            var teamOne = fetched.TeamOne;
            var teamTwo = fetched.TeamTwo;
            long? liveGameId = fetched.GameId;
            bool isCustomGame = fetched.IsCustomGame;

            // 当前对局 gameId 检测：未经历本局选人（直连进游戏/大乱斗/中途启动）且 gameId 变化时，
            // 彻底清空上一局残留（含保留快照），防止旧快照污染新对局
            bool isSameLiveGame = HandleLiveGameIdTransition(liveGameId);

            // InProgress 阶段 LCU 会把 gameflow 队伍精简（甚至只剩自己）：
            // 落盘有同 gameId 的完整快照且当前队伍更少时，先从快照恢复整局，后续合并保留更全的数据
            if (liveGameId is not null)
            {
                GameInfoState cur = _stateStore.CurrentState;
                if (_snapshotCoordinator.ShouldRestoreForActiveSession(
                        liveGameId.Value,
                        isCustomGame,
                        cur.MyTeam.Count + cur.TheirTeam.Count,
                        cur.TheirTeam.Count,
                        teamOne.Count + teamTwo.Count))
                {
                    TryRestoreReservedSnapshot();
                }
            }

            // 同局完整态短路复用：liveGameId 与 currentGameId 一致、队伍人数均已齐备（>= 会话队伍人数）
            // 且所有非 Bot 玩家均已不在 Loading 状态时，跳过冗余全量队伍重建与 LoadAllPlayersAsync，仅对个别缺失项做补填
            if (isSameLiveGame && _stateStore.TryGetSameGameMissingPlayers(teamOne.Count, teamTwo.Count, out var missingPlayers))
            {
                _logger?.LogDebug("同局完整态短路复用 gameId={GameId}, missing={MissingCount}", liveGameId, missingPlayers.Count);
                foreach (var p in missingPlayers)
                {
                    _ = LoadPlayerDataAsync(p.CellId, p.SummonerId, p.Puuid, p, cancellationToken);
                }

                if (IsGameActivePhaseObserved())
                {
                    _liveClientEnricher.StartPolling();
                }

                return;
            }

            // ── 队伍空/单边空重试（对应旧版 loadFromGameflowSession 的重试分支）──
            var retried = await RetryIncompleteTeamsAsync(
                transport,
                reqId,
                teamOne,
                teamTwo,
                liveGameId,
                isCustomGame,
                cancellationToken).ConfigureAwait(false);
            if (retried is null)
            {
                return;
            }

            teamOne = retried.Value.TeamOne;
            teamTwo = retried.Value.TeamTwo;

            // 组队 ID 回填与 session 阵营缓存（对应旧版 fetchPremadeColors 的 teamParticipantId 回填）
            UpdateSessionTeams(teamOne, teamTwo);

            GameInfoState snapshotState = _stateStore.CurrentState;
            string? mapSide = snapshotState.MapSide;

            // 阵营未知（脱敏对局）时异步探测：pin-drop → 选人 session cellId → gameflow 队伍 → LiveClientData
            if (string.IsNullOrEmpty(mapSide))
            {
                mapSide = await GameInfoSessionParser.ResolveMapSideAsync(
                    transport,
                    _liveClientService,
                    teamOne,
                    teamTwo,
                    snapshotState.CurrentSummonerId,
                    snapshotState.CurrentSummonerPuuid,
                    cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(mapSide))
                {
                    _logger?.LogInformation("脱敏对局阵营探测结果: {Side}", mapSide);
                }
            }

            GameInfoState stateBeforeBuild = _stateStore.CurrentState;
            TeamBuildContext ctx;
            lock (_sessionGate)
            {
                ctx = new TeamBuildContext
                {
                    CurrentSummonerId = stateBeforeBuild.CurrentSummonerId,
                    CurrentSummonerPuuid = stateBeforeBuild.CurrentSummonerPuuid,
                    MapSide = mapSide,
                    ChampSelectTeamSnapshot = _champSelectTeamSnapshot,
                    ChampSelectTheirTeamSnapshot = _champSelectTheirTeamSnapshot,
                    SessionAllyTeam = _sessionAllyTeam,
                    SessionEnemyTeam = _sessionEnemyTeam,
                    GameflowMyTeam = stateBeforeBuild.MyTeam,
                    GameflowTheirTeam = stateBeforeBuild.TheirTeam,
                    PlayerData = stateBeforeBuild.PlayerData,
                };
            }

            var (allyTeam, enemyTeam, side) = TeamBuilder.ResolveTeamSide(teamOne, teamTwo, ctx);
            _logger?.LogInformation(
                "对局队伍解析: teamOne={TeamOne} 人, teamTwo={TeamTwo} 人, 阵营={Side} → 我方 {My} 人 / 敌方 {Their} 人",
                teamOne.Count, teamTwo.Count, side ?? "未知", allyTeam.Count, enemyTeam.Count);
            var (mappedMy, mappedTheir) = TeamBuilder.BuildGameflowTeams(allyTeam, enemyTeam, side, ctx);

            // LCU 会话偶发残缺（如 InProgress 精简到只剩自己）：新队伍更短时保留已有更全的，
            // 不让整列消失；人数补齐后由后续 session 事件重处理
            GameInfoState latestState = _stateStore.CurrentState;
            IReadOnlyList<PremadePlayerLike> finalMy = mappedMy.Count >= latestState.MyTeam.Count ? mappedMy : latestState.MyTeam;
            IReadOnlyList<PremadePlayerLike> finalTheir = mappedTheir.Count >= latestState.TheirTeam.Count ? mappedTheir : latestState.TheirTeam;

            var newPlayerData = new Dictionary<string, GameInfoPlayer>(ctx.PlayerData);
            TeamBuilder.SeedInitialPlayerData(finalMy.Concat(finalTheir).ToList(), newPlayerData);

            var colorsMy = TeamBuilder.ResolvePremadeColors(finalMy, latestState.PremadeColorsMy);
            var colorsTheir = TeamBuilder.ResolvePremadeColors(finalTheir, latestState.PremadeColorsTheir);

            _stateStore.UpdateState(s => s with
            {
                MyTeam = finalMy,
                TheirTeam = finalTheir,
                // 阵营仅在解析成功时写入（对应旧版 if (side) setMapSide(side)），未知时不覆盖既有值
                MapSide = side ?? s.MapSide,
                PlayerData = newPlayerData,
                PremadeColorsMy = colorsMy,
                PremadeColorsTheir = colorsTheir,
                ShouldShowContent = true,
                IsLoadingTeams = false,
                // 装配出真实队伍即脱离演示态：演示数据的 puuid 是占位值，不能用于跳转查询
                IsDemo = false,
                IsDemoData = false,
            });

            _ = LoadAllPlayersAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LoadFromGameflowSessionAsync 失败");
        }
    }

    public async Task LoadAllPlayersAsync(CancellationToken cancellationToken = default)
    {
        var (capturedGameId, capturedEpoch) = _stateStore.CaptureGameContext();

        GameInfoState state = _stateStore.CurrentState;
        List<PremadePlayerLike> allPlayers = state.MyTeam.Concat(state.TheirTeam).ToList();
        if (allPlayers.Count == 0)
        {
            return;
        }

        var tasks = allPlayers.Select(p =>
            LoadPlayerDataAsync(p.CellId, p.SummonerId, p.Puuid, p, cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (!_stateStore.IsSameGameContext(capturedGameId, capturedEpoch))
        {
            return;
        }

        // 加载全部玩家后，如果在对局中，尝试启动 Live Client 补全（§4.11）
        if (IsGameActivePhaseObserved())
        {
            _liveClientEnricher.StartPolling();
        }
    }

    public Task<GameInfoPlayer?> LoadPlayerDataAsync(
        int cellId,
        long summonerId,
        string? puuid,
        PremadePlayerLike? fallback = null,
        CancellationToken cancellationToken = default)
        => _playerLoader.LoadPlayerDataAsync(cellId, summonerId, puuid, fallback, cancellationToken);

    public Task SyncCurrentPlayerMatchesAsync(bool forceNetwork = false, CancellationToken cancellationToken = default)
        => _matchExpander.SyncCurrentPlayerMatchesAsync(forceNetwork, cancellationToken);

    public void NotifyGameEnded()
        => _matchExpander.NotifyGameEnded();

    private bool HandleLiveGameIdTransition(long? liveGameId)
    {
        if (liveGameId is null)
        {
            return false;
        }

        long? savedGameId = _snapshotCoordinator.ReservedSnapshotGameId;
        bool champSelectSeen;
        lock (_sessionGate)
        {
            champSelectSeen = _champSelectSeenForGame;
        }

        var (isNewGame, isSameLiveGame) = _stateStore.HandleLiveGameIdTransition(
            liveGameId.Value,
            savedGameId,
            champSelectSeen);

        if (isNewGame)
        {
            _logger?.LogDebug("检测到全新对局 gameId={GameId}（上一局 savedId={SavedId}），彻底清空上一局数据", liveGameId, savedGameId);
            _snapshotCoordinator.Clear();
        }

        return isSameLiveGame;
    }

    private async Task<GameInfoSessionParser.GameflowTeamsFetch?> RetryIncompleteTeamsAsync(
        ILcuTransport transport,
        long reqId,
        List<PremadePlayerLike> teamOne,
        List<PremadePlayerLike> teamTwo,
        long? liveGameId,
        bool isCustomGame,
        CancellationToken cancellationToken)
    {
        bool snapshotHasBots;
        lock (_sessionGate)
        {
            snapshotHasBots = _champSelectTeamSnapshot.Any(p => p.Bot || p.IsBot) ||
                              _champSelectTheirTeamSnapshot.Any(p => p.Bot || p.IsBot);
        }

        if (teamOne.Count == 0 && teamTwo.Count == 0)
        {
            for (int attempt = 0; attempt < EmptyTeamRetryCount; attempt++)
            {
                await Task.Delay(EmptyTeamRetryDelayMs, cancellationToken).ConfigureAwait(false);
                if (reqId != Interlocked.Read(ref _currentSessionRequestId) || !IsGameActivePhaseObserved())
                {
                    return null;
                }

                var retry = await GameInfoSessionParser.FetchGameflowTeamsAsync(transport, cancellationToken).ConfigureAwait(false);
                if (retry.GameId is not null) liveGameId = retry.GameId;
                isCustomGame = retry.IsCustomGame;
                if (retry.TeamOne.Count > 0 || retry.TeamTwo.Count > 0)
                {
                    teamOne = retry.TeamOne;
                    teamTwo = retry.TeamTwo;
                    break;
                }
            }

            if (teamOne.Count == 0 && teamTwo.Count == 0)
            {
                _logger?.LogInformation("gameflow session 双方队伍仍为空（已重试 {Count} 次），暂不渲染对局", EmptyTeamRetryCount);
                return null;
            }
        }
        else if (teamOne.Count == 0 || teamTwo.Count == 0)
        {
            // 自定义对局（人机）teamTwo 可能永远为空，无需重试
            if (!isCustomGame && !snapshotHasBots)
            {
                for (int attempt = 0; attempt < SingleTeamRetryCount && (teamOne.Count == 0 || teamTwo.Count == 0); attempt++)
                {
                    await Task.Delay(EmptyTeamRetryDelayMs, cancellationToken).ConfigureAwait(false);
                    if (reqId != Interlocked.Read(ref _currentSessionRequestId) || !IsGameActivePhaseObserved())
                    {
                        return null;
                    }

                    var retry = await GameInfoSessionParser.FetchGameflowTeamsAsync(transport, cancellationToken).ConfigureAwait(false);
                    if (retry.GameId is not null) liveGameId = retry.GameId;
                    isCustomGame = retry.IsCustomGame;
                    if (retry.TeamOne.Count > 0) teamOne = retry.TeamOne;
                    if (retry.TeamTwo.Count > 0) teamTwo = retry.TeamTwo;
                }
            }
        }

        return new GameInfoSessionParser.GameflowTeamsFetch(teamOne, teamTwo, liveGameId, isCustomGame);
    }

    private void UpdateSessionTeams(
        IReadOnlyList<PremadePlayerLike> teamOne,
        IReadOnlyList<PremadePlayerLike> teamTwo)
    {
        GameInfoState state = _stateStore.CurrentState;
        List<PremadePlayerLike> snapshot;
        lock (_sessionGate)
        {
            snapshot = _champSelectTeamSnapshot;
        }

        var (ally, enemy) = GameInfoSessionParser.SplitSessionTeams(
            teamOne,
            teamTwo,
            state.CurrentSummonerId,
            state.CurrentSummonerPuuid,
            snapshot);

        lock (_sessionGate)
        {
            _sessionAllyTeam = ally;
            _sessionEnemyTeam = enemy;
            GameInfoSessionParser.BackfillTeamParticipantIds(_champSelectTeamSnapshot, ally);
        }
    }

    private async Task LoadFromChampSelectAsync(CancellationToken cancellationToken)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return;
        }

        try
        {
            LcuResponse resp = await transport.SendAsync(
                LcuRequest.Get("/lol-champ-select/v1/session"),
                cancellationToken).ConfigureAwait(false);

            if (!resp.IsSuccess || string.IsNullOrWhiteSpace(resp.Body))
            {
                return;
            }

            using var doc = JsonDocument.Parse(resp.Body);
            var parsed = GameInfoSessionParser.BuildChampSelectTeams(doc.RootElement);
            var myTeam = parsed.MyTeam;
            var theirTeam = parsed.TheirTeam;

            // 增量维护选人阶段双方快照：新帧未识别出英雄时沿用上一帧的 championId / 组队 ID，
            // 避免过渡帧把已锁定的英雄冲成 0（对应旧版 mergeSnapshot）
            lock (_sessionGate)
            {
                _champSelectTeamSnapshot = GameInfoSessionParser.MergeChampSelectSnapshot(myTeam, _champSelectTeamSnapshot);
                _champSelectTheirTeamSnapshot = GameInfoSessionParser.MergeChampSelectSnapshot(theirTeam, _champSelectTheirTeamSnapshot);
                myTeam = _champSelectTeamSnapshot;
                theirTeam = _champSelectTheirTeamSnapshot;
            }

            var newPlayerData = new Dictionary<string, GameInfoPlayer>(_stateStore.CurrentState.PlayerData);
            TeamBuilder.SeedInitialPlayerData(myTeam.Concat(theirTeam).ToList(), newPlayerData);

            var colorsMy = PremadeAnalyzer.ComputePremadeColors(myTeam);
            var colorsTheir = PremadeAnalyzer.ComputePremadeColors(theirTeam);

            _stateStore.UpdateState(s => s with
            {
                MyTeam = myTeam,
                TheirTeam = theirTeam,
                PlayerData = newPlayerData,
                PremadeColorsMy = colorsMy,
                PremadeColorsTheir = colorsTheir,
                ShouldShowContent = true,
                IsLoadingTeams = false,
                // 装配出真实队伍即脱离演示态：演示数据的 puuid 是占位值，不能用于跳转查询
                IsDemo = false,
                IsDemoData = false,
            });

            if (myTeam.Count > 0)
            {
                lock (_sessionGate)
                {
                    _champSelectSeenForGame = true;
                }
            }

            // 选人阶段即判定阵营（对应旧版 ChampSelect 时 getMapSide 追加标题红蓝方）：
            // 已有值不重复探测；探测失败（选人初期 pin-drop 未就绪）靠后续 session 事件帧自然重试
            GameInfoState currentState = _stateStore.CurrentState;
            if (string.IsNullOrEmpty(currentState.MapSide))
            {
                string? resolved = await GameInfoSessionParser.ResolveMapSideAsync(
                    transport,
                    _liveClientService,
                    [],
                    [],
                    currentState.CurrentSummonerId,
                    currentState.CurrentSummonerPuuid,
                    cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(resolved))
                {
                    _logger?.LogInformation("选人阶段阵营探测结果: {Side}", resolved);
                    _stateStore.UpdateState(s => s with { MapSide = resolved });
                }
            }

            _ = LoadAllPlayersAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LoadFromChampSelectAsync 失败");
        }
    }

    private async Task UpdateQueueAndTftAsync(ILcuTransport transport, CancellationToken cancellationToken)
    {
        try
        {
            LcuResponse resp = await transport.SendAsync(
                LcuRequest.Get("/lol-gameflow/v1/session"),
                cancellationToken).ConfigureAwait(false);

            if (resp.IsSuccess && !string.IsNullOrWhiteSpace(resp.Body))
            {
                using var doc = JsonDocument.Parse(resp.Body);
                if (GameInfoSessionParser.TryParseQueueInfo(doc.RootElement, out var queueInfo))
                {
                    if (queueInfo.IsTft)
                    {
                        // TFT 清全部（含保留快照），对局信息页显示云顶占位
                        _stateStore.ApplyTftReset(queueInfo.QueueId);
                        _snapshotCoordinator.Clear();
                        return;
                    }

                    _stateStore.UpdateState(s => s with
                    {
                        CurrentQueueId = queueInfo.QueueId,
                        IsTftMode = queueInfo.IsTft,
                    });
                }
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

    private void HandleOutOfGamePhase(GamePhase phase)
    {
        GameInfoState state = _stateStore.CurrentState;
        bool hasMemoryData = state.MyTeam.Count > 0 && state.PlayerData.Count > 0;

        // 内存无数据（如中途启动/选人秒退后）：仅配置开启时恢复上一局保留快照
        if (!hasMemoryData && _config.Functions.EnableReserveGameinfo)
        {
            TryRestoreReservedSnapshot();
        }

        // 旧版语义：非活跃阶段展示保留数据必须 EnableReserveGameinfo=true（§4.9）
        _stateStore.SetStateUnconditional(s => s with
        {
            Phase = phase,
            ShouldShowContent = _config.Functions.EnableReserveGameinfo && s.MyTeam.Count > 0 && s.PlayerData.Count > 0,
        });
    }

    /// <summary>
    /// 阶段观测：等价旧版 gamePhase watch + isGameActive watch + gameEndedTrigger watch 的状态清理与触发链。
    /// </summary>
    private void HandlePhaseObserved(GamePhase phase)
    {
        GamePhase prev;
        lock (_sessionGate)
        {
            if (phase == _observedPhase)
            {
                return;
            }

            prev = _observedPhase;
            _observedPhase = phase;
        }

        bool wasActive = prev.IsGameActive();
        bool isActive = phase.IsGameActive();

        if (phase is not (GamePhase.GameStart or GamePhase.InProgress))
        {
            _liveClientEnricher.CancelPolling();
        }

        if (isActive)
        {
            // 重新进入活跃对局：立即停止对局结束链式轮询
            _matchExpander.StopGameEndedChain();

            if (phase == GamePhase.ChampSelect)
            {
                // 进入选人：清内存视图与上一局数据（保留快照，防秒退丢上一局完整对局）
                lock (_sessionGate)
                {
                    _champSelectSeenForGame = false;
                    _champSelectTeamSnapshot = [];
                    _champSelectTheirTeamSnapshot = [];
                    _sessionAllyTeam = [];
                    _sessionEnemyTeam = [];
                }

                _stateStore.CurrentGameId = null;
                _stateStore.ClearInGameMemory();

                // 进入选人：加载路人集映射（§4.8）
                _playerLoader.LoadSavedPlayerMap();
            }
            else if (phase is GamePhase.GameStart or GamePhase.InProgress && !wasActive)
            {
                bool champSelectSeen;
                lock (_sessionGate)
                {
                    champSelectSeen = _champSelectSeenForGame;
                }

                if (!champSelectSeen)
                {
                    // 未经历本局选人快照（直连进游戏/大乱斗/中途启动）：清残留防上一局污染
                    _stateStore.ClearInGameMemory();
                }
                else
                {
                    // 清理选人阶段以数字 0..9 临时缓存的旧键（红方槽位与敌方槽位碰撞防护）
                    _stateStore.RemoveCellIdKeysFromPlayerData();
                }
            }
        }
        else if (wasActive)
        {
            // 离开游戏活跃状态（回到 Lobby / EndOfGame 等）
            _matchExpander.StopGameEndedChain();
            lock (_sessionGate)
            {
                _champSelectSeenForGame = false;
            }

            GameInfoState state = _stateStore.CurrentState;
            bool hasData = state.MyTeam.Count > 0 && state.PlayerData.Count > 0;

            if (hasData)
            {
                // 内存中已有刚打完的对局：写入保留快照
                WriteReservedSnapshot();
            }
            else if (_config.Functions.EnableReserveGameinfo)
            {
                // 否则（如选人秒退离开、中途启动）：仅配置开启时恢复上一局快照
                TryRestoreReservedSnapshot();
            }

            if (!_config.Functions.EnableReserveGameinfo)
            {
                _stateStore.CurrentGameId = null;
                _stateStore.ClearInGameMemory();
            }

            if (phase is GamePhase.WaitingForStats or GamePhase.PreEndOfGame or GamePhase.EndOfGame or GamePhase.Lobby or GamePhase.None)
            {
                // 对局结束：链式梯度轮询拉取刚结束对局的最新战绩
                NotifyGameEnded();
            }
        }
        else if (phase == GamePhase.None)
        {
            // None 阶段：仅未开启保留时清空内存态（开启保留时保留上一局直到下一次对局开始）
            if (!_config.Functions.EnableReserveGameinfo)
            {
                _stateStore.CurrentGameId = null;
                _stateStore.ClearInGameMemory();
            }
        }
    }

    private void WriteReservedSnapshot()
    {
        var (state, currentGameId) = _stateStore.CaptureSnapshotInputs();
        _snapshotCoordinator.WriteSnapshot(state, currentGameId);
    }

    private bool TryRestoreReservedSnapshot()
    {
        var (state, currentGameId) = _stateStore.CaptureSnapshotInputs();
        if (!_snapshotCoordinator.TryPrepareRestore(
                state.Phase,
                currentGameId,
                _config.Functions.EnableReserveGameinfo,
                out var restored))
        {
            return false;
        }

        _stateStore.ApplyRestoredSnapshot(restored);

        // 异步补齐缺少熟练度的玩家 + 同步当前玩家最新战绩 + 恢复后加载路人集映射（§4.8）
        _ = _playerLoader.BackfillMissingMasteriesAsync(restored.PlayerData.Values);
        _ = _matchExpander.SyncCurrentPlayerMatchesAsync();
        _playerLoader.LoadSavedPlayerMap();
        return true;
    }

    private bool IsGameActivePhaseObserved()
    {
        lock (_sessionGate)
        {
            return _observedPhase is GamePhase.GameStart or GamePhase.InProgress;
        }
    }

    private async Task EnsureCurrentSummonerAsync(CancellationToken cancellationToken)
    {
        GameInfoState state = _stateStore.CurrentState;
        if (state.CurrentSummonerId != 0 && !string.IsNullOrEmpty(state.CurrentSummonerPuuid))
        {
            return;
        }

        var current = await _summonerService.GetCurrentSummonerAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            string? previousPuuid = _stateStore.CurrentState.CurrentSummonerPuuid;

            _stateStore.UpdateState(s => s with
            {
                CurrentSummonerId = current.SummonerId,
                CurrentSummonerPuuid = current.Puuid,
            });

            // 当前召唤师变化（含首次加载）：加载路人集映射（§4.8）
            if (!string.IsNullOrEmpty(current.Puuid) &&
                !string.Equals(previousPuuid, current.Puuid, StringComparison.Ordinal))
            {
                _playerLoader.LoadSavedPlayerMap();
            }
        }
    }

    private void OnConnectionStateChanged(LcuConnectionState state)
    {
        if (state.Status == LcuConnectionStatus.Connected)
        {
            _ = RefreshStateAsync();
            // 连接变化：加载路人集映射（§4.8；首次连接时召唤师尚未就绪，由召唤师变化触发兜底）
            _playerLoader.LoadSavedPlayerMap();
        }
        else if (state.Status == LcuConnectionStatus.Disconnected)
        {
            _stateStore.UpdateState(s => s with { Phase = GamePhase.None, ShouldShowContent = false, Error = null });
        }
    }

    private void OnLcuEvent(LcuEvent ev)
    {
        if (ev.Uri.StartsWith("/lol-gameflow/v1/gameflow-phase", StringComparison.OrdinalIgnoreCase))
        {
            if (ev.Data.ValueKind == JsonValueKind.String)
            {
                string raw = ev.Data.GetString() ?? string.Empty;
                GamePhase phase = GamePhaseExtensions.ParseGamePhase(raw);
                _stateStore.UpdateState(s => s with { Phase = phase });
                HandlePhaseObserved(phase);
                _ = RefreshStateAsync();
            }
        }
        else if (ev.Uri.StartsWith("/lol-champ-select/v1/session", StringComparison.OrdinalIgnoreCase))
        {
            if (_stateStore.CurrentState.Phase == GamePhase.ChampSelect)
            {
                _ = LoadFromChampSelectAsync(CancellationToken.None);
            }
        }
        else if (ev.Uri.StartsWith("/lol-gameflow/v1/session", StringComparison.OrdinalIgnoreCase))
        {
            if (_stateStore.CurrentState.Phase is GamePhase.GameStart or GamePhase.InProgress)
            {
                _ = LoadFromGameflowSessionAsync(CancellationToken.None);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true))
        {
            return;
        }

        _matchExpander.Dispose();
        _liveClientEnricher.Dispose();
        _snapshotCoordinator.Dispose();

        _connStateSub?.Dispose();
        _connEventSub?.Dispose();
        _playerLoader.Dispose();
        _stateStore.CompleteStream();
    }
}
