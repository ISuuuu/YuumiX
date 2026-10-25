using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Common;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.SavedPlayers;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Lcu.Sgp;
using YuumiX.Core.Parsers;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 对局信息编排服务实现（对应旧版 <c>useGamePlayerData.ts</c>）。
/// 核心职责：阶段状态机驱动、队伍解析、10人并发加载三路、预组队与宿命检测。
/// </summary>
public sealed class GameInfoService : IGameInfoService, IDisposable
{
    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly ISummonerService _summonerService;
    private readonly IMatchService _matchService;
    private readonly ICareerService _careerService;
    private readonly IMasteryService _masteryService;
    private readonly IFateService _fateService;
    private readonly ISgpService? _sgpService;
    private readonly SavedPlayersStore? _savedPlayersStore;
    private readonly AppConfig _config;
    private readonly ILogger<GameInfoService>? _logger;
    private readonly EventStream<GameInfoState> _stateStream;
    private readonly SemaphoreSlim _semaphore;

    /// <summary>低优先级战绩扩充顺序队列（单任务串行，排在首屏加载之后）。</summary>
    private readonly SemaphoreSlim _extraMatchesGate = new(1, 1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<GameInfoPlayer?>> _inFlightLoads = new();

    /// <summary>路人集映射 in-flight 去重条目（同 puuid 并发只发一次，对应旧版 querySavedPlayersMapDedup）。</summary>
    private (string Puuid, Task<IReadOnlyDictionary<string, SavedPlayerMarker>> Task)? _savedPlayersMapInFlight;

    private readonly IDisposable? _connStateSub;
    private readonly IDisposable? _connEventSub;

    private GameInfoState _state = GameInfoState.Empty;

    /// <summary>保留对局快照（内存结构，对应旧版 localStorage 保留数据语义，见 §4.9）。</summary>
    private ReservedSnapshot? _reservedSnapshot;

    /// <summary>当前对局 gameId（InProgress 精简 session 恢复时校验快照归属）。</summary>
    private long? _currentGameId;

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

    // 当前玩家战绩同步状态（10s 节流 + 单飞）
    private bool _isSyncingCurrentPlayerMatches;
    private long _lastCurrentPlayerSyncTick;

    // 对局结束链式轮询（2s/3.5s/5s，可安全取消）
    private CancellationTokenSource? _gameEndedChainCts;

    /// <summary>对局结束链式轮询间隔（毫秒），测试可注入缩短。</summary>
    internal int[] GameEndedChainDelaysMs { get; set; } = [2000, 3500, 5000];

    // Live Client 补全（2999 端口，敌方脱敏补全）
    private readonly ILiveClientService? _liveClientService;
    private readonly Func<GameDataCatalog?>? _catalogProvider;
    private CancellationTokenSource? _liveClientPollCts;
    private readonly HashSet<string> _queriedLiveClientNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Live Client 轮询重试间隔（毫秒），测试可注入缩短。</summary>
    internal int LiveClientPollDelayMs { get; set; } = 2000;

    /// <summary>Live Client 最大轮询重试次数。</summary>
    internal int LiveClientMaxRetries { get; set; } = 15;

    /// <summary>gameflow session 双方队伍为空时的重试次数（旧版 10 次 × 1s）。</summary>
    internal int EmptyTeamRetryCount { get; set; } = 10;

    /// <summary>gameflow session 仅有单边队伍时的重试次数（旧版 3 次 × 1s）。</summary>
    internal int SingleTeamRetryCount { get; set; } = 3;

    /// <summary>gameflow session 队伍重试间隔（毫秒，旧版 1000ms）。</summary>
    internal int EmptyTeamRetryDelayMs { get; set; } = 1000;

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
        Func<GameDataCatalog?>? catalogProvider = null)
    {
        _transportProvider = transportProvider;
        _summonerService = summonerService;
        _matchService = matchService;
        _careerService = careerService;
        _masteryService = masteryService;
        _fateService = fateService;
        _sgpService = sgpService;
        _savedPlayersStore = savedPlayersStore;
        _liveClientService = liveClientService;
        _catalogProvider = catalogProvider;
        _config = config;
        _logger = logger;

        int concurrency = Math.Clamp(config.Functions.ApiConcurrencyNumber, 1, 10);
        _semaphore = new SemaphoreSlim(concurrency, concurrency);

        _stateStream = new EventStream<GameInfoState>(ex =>
            _logger?.LogWarning(ex, "GameInfo 状态通知发生异常，已隔离"));

        if (connection is not null)
        {
            _connStateSub = connection.States.Subscribe(OnConnectionStateChanged);
            _connEventSub = connection.Events.Subscribe(OnLcuEvent);
        }
    }

    /// <summary>
    /// 保留对局快照：双方队伍、玩家数据、组队色与归属信息
    /// （对应旧版 localStorage 的 yuumi_last_gameflow_* / yuumi_last_game_* 键组）。
    /// </summary>
    private sealed record ReservedSnapshot(
        IReadOnlyList<PremadePlayerLike> MyTeam,
        IReadOnlyList<PremadePlayerLike> TheirTeam,
        IReadOnlyDictionary<string, GameInfoPlayer> PlayerData,
        IReadOnlyDictionary<string, int> PremadeColorsMy,
        IReadOnlyDictionary<string, int> PremadeColorsTheir,
        long? GameId,
        int TeamCount,
        int LoadedCount);

    public EventStream<GameInfoState> StateStream => _stateStream;

    public GameInfoState CurrentState
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    // ── 测试诊断钩子（InternalsVisibleTo=YuumiX.Core.Tests）─────────────────
    internal bool HasReservedSnapshot
    {
        get { lock (_gate) { return _reservedSnapshot is not null; } }
    }

    internal long? ReservedSnapshotGameId
    {
        get { lock (_gate) { return _reservedSnapshot?.GameId; } }
    }

    internal int ReservedSnapshotTeamCount
    {
        get { lock (_gate) { return _reservedSnapshot?.TeamCount ?? 0; } }
    }

    internal int ReservedSnapshotLoadedCount
    {
        get { lock (_gate) { return _reservedSnapshot?.LoadedCount ?? 0; } }
    }

    internal IReadOnlyDictionary<string, GameInfoPlayer>? ReservedSnapshotPlayerData
    {
        get { lock (_gate) { return _reservedSnapshot?.PlayerData; } }
    }

    /// <summary>对局结束链式轮询是否仍在运行（测试用）。</summary>
    internal bool IsGameEndedChainActive
    {
        get { lock (_gate) { return _gameEndedChainCts is not null; } }
    }

    /// <summary>战绩扩充顺序队列是否空闲（测试用：等待后台扩充完成）。</summary>
    internal bool ExtraMatchesIdle => _extraMatchesGate.CurrentCount > 0;

    /// <summary>已完成的战绩扩充任务数（测试用：等待后台扩充处理完毕）。</summary>
    internal int ExtraMatchesProcessedCount => Volatile.Read(ref _extraMatchesProcessed);

    private int _extraMatchesProcessed;

    /// <summary>路人集映射查询注入点（测试用；null 时走 SavedPlayersStore）。</summary>
    internal Func<string, CancellationToken, Task<IReadOnlyDictionary<string, SavedPlayerMarker>>>? SavedPlayersMapQueryOverride { get; set; }

    /// <summary>是否具备路人集映射查询能力（store 或测试注入点存在）。</summary>
    private bool HasSavedPlayersMapSource => _savedPlayersStore is not null || SavedPlayersMapQueryOverride is not null;

    /// <summary>
    /// 路人集映射共享查询（对应旧版 <c>querySavedPlayersMapDedup</c>）：
    /// 同 puuid 并发只发一次，与宿命检测候选共用同一次调用；不同 puuid 立即替换 in-flight 条目。
    /// </summary>
    internal Task<IReadOnlyDictionary<string, SavedPlayerMarker>> QuerySavedPlayersMapAsync(
        string selfPuuid,
        CancellationToken cancellationToken = default)
    {
        if (!HasSavedPlayersMapSource || string.IsNullOrWhiteSpace(selfPuuid))
        {
            return Task.FromResult<IReadOnlyDictionary<string, SavedPlayerMarker>>(new Dictionary<string, SavedPlayerMarker>());
        }

        TaskCompletionSource<IReadOnlyDictionary<string, SavedPlayerMarker>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_savedPlayersMapInFlight is { } inflight && inflight.Puuid == selfPuuid)
            {
                return inflight.Task;
            }

            _savedPlayersMapInFlight = (selfPuuid, completion.Task);
        }

        _ = QuerySavedPlayersMapCoreAsync(selfPuuid, cancellationToken, completion);
        return completion.Task;
    }

    private async Task QuerySavedPlayersMapCoreAsync(
        string selfPuuid,
        CancellationToken cancellationToken,
        TaskCompletionSource<IReadOnlyDictionary<string, SavedPlayerMarker>> completion)
    {
        try
        {
            IReadOnlyDictionary<string, SavedPlayerMarker> map;
            if (SavedPlayersMapQueryOverride is { } overrideQuery)
            {
                map = await overrideQuery(selfPuuid, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                map = await _savedPlayersStore!.GetSavedPlayersMapAsync(selfPuuid, cancellationToken).ConfigureAwait(false);
            }

            lock (_gate)
            {
                if (_savedPlayersMapInFlight is { } inflight &&
                    ReferenceEquals(inflight.Task, completion.Task))
                {
                    _savedPlayersMapInFlight = null;
                }
            }

            completion.TrySetResult(map);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (_savedPlayersMapInFlight is { } inflight &&
                    ReferenceEquals(inflight.Task, completion.Task))
                {
                    _savedPlayersMapInFlight = null;
                }
            }
            completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_savedPlayersMapInFlight is { } inflight &&
                    ReferenceEquals(inflight.Task, completion.Task))
                {
                    _savedPlayersMapInFlight = null;
                }
            }
            completion.TrySetException(ex);
        }
    }

    /// <summary>
    /// 触发路人集映射加载并发布到状态流（§4.8 触发时机：进入选人 / 连接变化 / 当前召唤师变化 / 恢复快照后）。
    /// </summary>
    private void LoadSavedPlayerMap()
    {
        if (!HasSavedPlayersMapSource)
        {
            return;
        }

        string? puuid;
        lock (_gate)
        {
            puuid = _state.CurrentSummonerPuuid;
        }

        if (string.IsNullOrWhiteSpace(puuid))
        {
            return;
        }

        _ = PublishSavedPlayerMapAsync(puuid);
    }

    private async Task PublishSavedPlayerMapAsync(string selfPuuid)
    {
        try
        {
            IReadOnlyDictionary<string, SavedPlayerMarker> map = await QuerySavedPlayersMapAsync(selfPuuid)
                .ConfigureAwait(false);
            UpdateState(s => s with { SavedPlayerMap = map });
        }
        catch (OperationCanceledException)
        {
            // 取消不发布
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "路人集映射加载失败");
        }
    }

    public void SetActiveTab(string tab)
    {
        lock (_gate)
        {
            _state = _state with { ActiveTab = tab };
            _stateStream.Publish(_state);
        }
    }

    public void SetViewColumns(int columns)
    {
        lock (_gate)
        {
            _state = _state with { ViewColumns = columns };
            _stateStream.Publish(_state);
        }
    }

    public void LoadDemoState()
    {
        lock (_gate)
        {
            _state = GameInfoSampleFactory.CreateSample() with { IsDemoData = true };
            _stateStream.Publish(_state);
        }
    }

    internal void SetStateForTest(Func<GameInfoState, GameInfoState> updater)
    {
        lock (_gate)
        {
            _state = updater(_state);
            _stateStream.Publish(_state);
        }
    }

    public async Task RefreshStateAsync(CancellationToken cancellationToken = default)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            UpdateState(s => s with { Phase = GamePhase.None, ShouldShowContent = false });
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

            UpdateState(s => s with { Phase = phase });
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
            UpdateState(s => s with { Error = ex.Message });
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
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("gameData", out var gameData))
            {
                return;
            }

            var teamOne = ParseParticipants(gameData, "teamOne");
            var teamTwo = ParseParticipants(gameData, "teamTwo");

            // 当前对局 gameId 检测：未经历本局选人（直连进游戏/大乱斗/中途启动）且 gameId 变化时，
            // 彻底清空上一局残留（含保留快照），防止旧快照污染新对局
            long? liveGameId = null;
            if (gameData.TryGetProperty("gameId", out var gameIdEl) && gameIdEl.ValueKind == JsonValueKind.Number)
            {
                long gid = gameIdEl.GetInt64();
                if (gid > 0)
                {
                    liveGameId = gid;
                }
            }

            bool isCustomGame = false;
            if (gameData.TryGetProperty("queue", out var queueEl) && queueEl.ValueKind == JsonValueKind.Object)
            {
                isCustomGame = JsonRead.Bool(queueEl, "isCustom", false);
            }

            if (liveGameId is not null)
            {
                lock (_gate)
                {
                    long? savedGameId = _reservedSnapshot?.GameId;
                    bool isNewGame = !_champSelectSeenForGame &&
                        ((savedGameId is not null && savedGameId != liveGameId) ||
                         (_currentGameId is not null && _currentGameId != liveGameId));
                    if (isNewGame)
                    {
                        _logger?.LogDebug("检测到全新对局 gameId={GameId}（上一局 savedId={SavedId}），彻底清空上一局数据", liveGameId, savedGameId);
                        _reservedSnapshot = null;
                        _state = _state with
                        {
                            MyTeam = [],
                            TheirTeam = [],
                            PlayerData = new Dictionary<string, GameInfoPlayer>(),
                            PremadeColorsMy = new Dictionary<string, int>(),
                            PremadeColorsTheir = new Dictionary<string, int>(),
                            ShouldShowContent = true,
                            IsLoadingTeams = false,
                        };
                        _stateStream.Publish(_state);
                    }

                    _currentGameId = liveGameId;
                }
            }

            // InProgress 阶段 LCU 会把 gameflow 队伍精简（甚至只剩自己）：
            // 落盘有同 gameId 的完整快照且当前队伍更少时，先从快照恢复整局，后续合并保留更全的数据
            if (liveGameId is not null && !isCustomGame)
            {
                ReservedSnapshot? saved;
                int currentTotal;
                int currentTheirCount;
                lock (_gate)
                {
                    saved = _reservedSnapshot;
                    currentTotal = _state.MyTeam.Count + _state.TheirTeam.Count;
                    currentTheirCount = _state.TheirTeam.Count;
                }

                int sessionTotal = teamOne.Count + teamTwo.Count;
                if (saved is { GameId: { } savedId } &&
                    savedId == liveGameId &&
                    (currentTotal == 0 ||
                     currentTheirCount == 0 ||
                     (saved.TeamCount > 0 && (sessionTotal < saved.TeamCount || currentTotal < saved.TeamCount))))
                {
                    TryRestoreReservedSnapshot();
                }
            }

            // ── 队伍空/单边空重试（对应旧版 loadFromGameflowSession 的重试分支）──
            // LCU 在载入瞬间偶发返回空队伍或只返回单边，直接放弃会让敌方整列消失
            bool snapshotHasBots;
            lock (_gate)
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
                        return;
                    }

                    var retry = await FetchGameflowTeamsAsync(transport, cancellationToken).ConfigureAwait(false);
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
                    return;
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
                            return;
                        }

                        var retry = await FetchGameflowTeamsAsync(transport, cancellationToken).ConfigureAwait(false);
                        if (retry.GameId is not null) liveGameId = retry.GameId;
                        isCustomGame = retry.IsCustomGame;
                        if (retry.TeamOne.Count > 0) teamOne = retry.TeamOne;
                        if (retry.TeamTwo.Count > 0) teamTwo = retry.TeamTwo;
                    }
                }
            }

            // 组队 ID 回填与 session 阵营缓存（对应旧版 fetchPremadeColors 的 teamParticipantId 回填）
            UpdateSessionTeams(teamOne, teamTwo);

            string? mapSide;
            lock (_gate)
            {
                mapSide = _state.MapSide;
            }

            // 阵营未知（脱敏对局）时异步探测：pin-drop → 选人 session cellId → gameflow 队伍 → LiveClientData
            if (string.IsNullOrEmpty(mapSide))
            {
                mapSide = await ResolveMapSideAsync(transport, teamOne, teamTwo, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(mapSide))
                {
                    _logger?.LogInformation("脱敏对局阵营探测结果: {Side}", mapSide);
                }
            }

            TeamBuildContext ctx;
            lock (_gate)
            {
                ctx = new TeamBuildContext
                {
                    CurrentSummonerId = _state.CurrentSummonerId,
                    CurrentSummonerPuuid = _state.CurrentSummonerPuuid,
                    MapSide = mapSide,
                    ChampSelectTeamSnapshot = _champSelectTeamSnapshot,
                    ChampSelectTheirTeamSnapshot = _champSelectTheirTeamSnapshot,
                    SessionAllyTeam = _sessionAllyTeam,
                    SessionEnemyTeam = _sessionEnemyTeam,
                    GameflowMyTeam = _state.MyTeam,
                    GameflowTheirTeam = _state.TheirTeam,
                    PlayerData = _state.PlayerData,
                };
            }

            var (allyTeam, enemyTeam, side) = TeamBuilder.ResolveTeamSide(teamOne, teamTwo, ctx);
            _logger?.LogInformation(
                "对局队伍解析: teamOne={TeamOne} 人, teamTwo={TeamTwo} 人, 阵营={Side} → 我方 {My} 人 / 敌方 {Their} 人",
                teamOne.Count, teamTwo.Count, side ?? "未知", allyTeam.Count, enemyTeam.Count);
            var (mappedMy, mappedTheir) = TeamBuilder.BuildGameflowTeams(allyTeam, enemyTeam, side, ctx);

            // LCU 会话偶发残缺（如 InProgress 精简到只剩自己）：新队伍更短时保留已有更全的，
            // 不让整列消失；人数补齐后由后续 session 事件重处理
            IReadOnlyList<PremadePlayerLike> finalMy;
            IReadOnlyList<PremadePlayerLike> finalTheir;
            lock (_gate)
            {
                finalMy = mappedMy.Count >= _state.MyTeam.Count ? mappedMy : _state.MyTeam;
                finalTheir = mappedTheir.Count >= _state.TheirTeam.Count ? mappedTheir : _state.TheirTeam;
            }

            var newPlayerData = new Dictionary<string, GameInfoPlayer>(ctx.PlayerData);
            TeamBuilder.SeedInitialPlayerData(finalMy.Concat(finalTheir).ToList(), newPlayerData);

            var colorsMy = TeamBuilder.ResolvePremadeColors(finalMy, _state.PremadeColorsMy);
            var colorsTheir = TeamBuilder.ResolvePremadeColors(finalTheir, _state.PremadeColorsTheir);

            UpdateState(s => s with
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

    /// <summary>gameflow session 拉取结果（重试分支复用）。</summary>
    private readonly record struct GameflowTeamsFetch(
        List<PremadePlayerLike> TeamOne,
        List<PremadePlayerLike> TeamTwo,
        long? GameId,
        bool IsCustomGame);

    /// <summary>重新拉取 gameflow session 的双方队伍（用于队伍缺失时重试）。</summary>
    private static async Task<GameflowTeamsFetch> FetchGameflowTeamsAsync(
        ILcuTransport transport,
        CancellationToken cancellationToken)
    {
        try
        {
            LcuResponse resp = await transport.SendAsync(
                LcuRequest.Get("/lol-gameflow/v1/session"),
                cancellationToken).ConfigureAwait(false);

            if (!resp.IsSuccess || string.IsNullOrWhiteSpace(resp.Body))
            {
                return new GameflowTeamsFetch([], [], null, false);
            }

            using var doc = JsonDocument.Parse(resp.Body);
            if (!doc.RootElement.TryGetProperty("gameData", out var gameData))
            {
                return new GameflowTeamsFetch([], [], null, false);
            }

            long? gameId = null;
            if (gameData.TryGetProperty("gameId", out var gameIdEl) && gameIdEl.ValueKind == JsonValueKind.Number)
            {
                long gid = gameIdEl.GetInt64();
                if (gid > 0)
                {
                    gameId = gid;
                }
            }

            bool isCustom = false;
            if (gameData.TryGetProperty("queue", out var queueEl) && queueEl.ValueKind == JsonValueKind.Object)
            {
                isCustom = JsonRead.Bool(queueEl, "isCustom", false);
            }

            return new GameflowTeamsFetch(
                ParseParticipants(gameData, "teamOne"),
                ParseParticipants(gameData, "teamTwo"),
                gameId,
                isCustom);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new GameflowTeamsFetch([], [], null, false);
        }
    }

    /// <summary>当前观测阶段是否仍在活跃对局（GameStart / InProgress）。</summary>
    private bool IsGameActivePhaseObserved()
    {
        lock (_gate)
        {
            return _observedPhase is GamePhase.GameStart or GamePhase.InProgress;
        }
    }

    /// <summary>
    /// 缓存 gameflow session 推断出的双方队伍，并把 session 中的组队 ID 回填进选人快照
    /// （对应旧版 <c>fetchPremadeColors</c> 的 teamParticipantId 回填）。
    /// </summary>
    private void UpdateSessionTeams(
        IReadOnlyList<PremadePlayerLike> teamOne,
        IReadOnlyList<PremadePlayerLike> teamTwo)
    {
        long summonerId;
        string? puuid;
        List<PremadePlayerLike> snapshot;
        lock (_gate)
        {
            summonerId = _state.CurrentSummonerId;
            puuid = _state.CurrentSummonerPuuid;
            snapshot = _champSelectTeamSnapshot;
        }

        bool MatchesLocal(PremadePlayerLike p) =>
            (summonerId != 0 && p.SummonerId == summonerId) ||
            (!string.IsNullOrEmpty(puuid) && !string.IsNullOrEmpty(p.Puuid) &&
             string.Equals(p.Puuid, puuid, StringComparison.OrdinalIgnoreCase)) ||
            snapshot.Any(cs =>
                (!string.IsNullOrEmpty(p.Puuid) && !string.IsNullOrEmpty(cs.Puuid) &&
                 string.Equals(p.Puuid, cs.Puuid, StringComparison.OrdinalIgnoreCase)) ||
                (p.SummonerId != 0 && cs.SummonerId != 0 && p.SummonerId == cs.SummonerId) ||
                (!string.IsNullOrEmpty(p.GameName) && !string.IsNullOrEmpty(cs.GameName) &&
                 string.Equals(p.GameName, cs.GameName, StringComparison.OrdinalIgnoreCase)));

        bool isTeamOne = teamOne.Any(MatchesLocal);
        var ally = isTeamOne || teamTwo.Count == 0 ? teamOne : teamTwo;
        var enemy = isTeamOne || teamTwo.Count == 0 ? teamTwo : teamOne;

        lock (_gate)
        {
            _sessionAllyTeam = ally.ToList();
            _sessionEnemyTeam = enemy.ToList();

            // 选人快照缺失组队 ID 时，用同身份的 session 队员补齐（供预组队着色与继承）
            for (int i = 0; i < _champSelectTeamSnapshot.Count; i++)
            {
                var cs = _champSelectTeamSnapshot[i];
                if (!string.IsNullOrEmpty(PlayerIdentity.GetValidTpid(cs)))
                {
                    continue;
                }

                var matched = ally.FirstOrDefault(ap =>
                    (!string.IsNullOrEmpty(cs.Puuid) && !string.IsNullOrEmpty(ap.Puuid) && cs.Puuid == ap.Puuid) ||
                    (cs.SummonerId != 0 && ap.SummonerId != 0 && cs.SummonerId == ap.SummonerId) ||
                    (!string.IsNullOrEmpty(cs.GameName) && !string.IsNullOrEmpty(ap.GameName) && cs.GameName == ap.GameName));

                string? tpid = PlayerIdentity.GetValidTpid(matched);
                if (tpid is null)
                {
                    continue;
                }

                _champSelectTeamSnapshot[i] = cs with { TeamParticipantId = tpid, PartyId = tpid };
            }
        }
    }

    /// <summary>
    /// 脱敏对局阵营探测（对应旧版 Rust <c>get_map_side</c>）：
    /// ① pin-drop-notification.mapSide；② 选人 session myTeam 的 cellId 区间；
    /// ③ gameflow session 中本机玩家所在队伍；④ LiveClientData 本机操作玩家的 team。
    /// </summary>
    private async Task<string?> ResolveMapSideAsync(
        ILcuTransport transport,
        IReadOnlyList<PremadePlayerLike> teamOne,
        IReadOnlyList<PremadePlayerLike> teamTwo,
        CancellationToken cancellationToken)
    {
        // ① pin-drop-notification（选人会话初始化可能延迟，短重试）
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                LcuResponse pinResp = await transport.SendAsync(
                    LcuRequest.Get("/lol-champ-select/v1/pin-drop-notification"),
                    cancellationToken).ConfigureAwait(false);

                if (!pinResp.IsSuccess || string.IsNullOrWhiteSpace(pinResp.Body))
                {
                    continue;
                }

                using var pinDoc = JsonDocument.Parse(pinResp.Body);
                if (pinDoc.RootElement.TryGetProperty("mapSide", out var mapSideEl) &&
                    mapSideEl.ValueKind == JsonValueKind.String)
                {
                    string? value = mapSideEl.GetString()?.ToLowerInvariant();
                    if (value is "blue" or "red")
                    {
                        return value;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 落入下一种探测方式
            }
        }

        // ② 选人 session：myTeam 最小/最大 cellId 判定（蓝方 0..4，红方 5..9）
        try
        {
            LcuResponse sessionResp = await transport.SendAsync(
                LcuRequest.Get("/lol-champ-select/v1/session"),
                cancellationToken).ConfigureAwait(false);

            if (sessionResp.IsSuccess && !string.IsNullOrWhiteSpace(sessionResp.Body))
            {
                using var sessionDoc = JsonDocument.Parse(sessionResp.Body);
                if (sessionDoc.RootElement.TryGetProperty("myTeam", out var myTeamEl) &&
                    myTeamEl.ValueKind == JsonValueKind.Array)
                {
                    int minCell = int.MaxValue;
                    int maxCell = -1;
                    foreach (var m in myTeamEl.EnumerateArray())
                    {
                        int cellId = JsonRead.Int(m, "cellId", -1);
                        if (cellId < 0)
                        {
                            continue;
                        }

                        minCell = Math.Min(minCell, cellId);
                        maxCell = Math.Max(maxCell, cellId);
                    }

                    if (maxCell >= 0)
                    {
                        if (minCell >= 5)
                        {
                            return "red";
                        }

                        if (maxCell < 5)
                        {
                            return "blue";
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 落入下一种探测方式
        }

        // ③ gameflow session：本机玩家落在 teamOne（蓝）还是 teamTwo（红）
        long summonerId;
        string? puuid;
        lock (_gate)
        {
            summonerId = _state.CurrentSummonerId;
            puuid = _state.CurrentSummonerPuuid;
        }

        if (summonerId == 0 && string.IsNullOrEmpty(puuid))
        {
            try
            {
                LcuResponse meResp = await transport.SendAsync(
                    LcuRequest.Get("/lol-summoner/v1/current-summoner"),
                    cancellationToken).ConfigureAwait(false);

                if (meResp.IsSuccess && !string.IsNullOrWhiteSpace(meResp.Body))
                {
                    using var meDoc = JsonDocument.Parse(meResp.Body);
                    summonerId = JsonRead.Long(meDoc.RootElement, "summonerId", 0);
                    puuid = JsonRead.String(meDoc.RootElement, "puuid");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 忽略，继续下一种探测
            }
        }

        bool InTeam(IReadOnlyList<PremadePlayerLike> team) => team.Any(p =>
            (!string.IsNullOrEmpty(puuid) && !string.IsNullOrEmpty(p.Puuid) &&
             string.Equals(p.Puuid, puuid, StringComparison.OrdinalIgnoreCase)) ||
            (summonerId != 0 && p.SummonerId == summonerId));

        if (InTeam(teamOne))
        {
            return "blue";
        }

        if (InTeam(teamTwo))
        {
            return "red";
        }

        // ④ LiveClientData：本机操作玩家所属 team（ORDER=蓝 / CHAOS=红）
        if (_liveClientService is null)
        {
            return null;
        }

        try
        {
            string? activeName = await _liveClientService.GetActivePlayerNameAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(activeName))
            {
                return null;
            }

            var players = await _liveClientService.GetPlayerListAsync(cancellationToken).ConfigureAwait(false);
            var matched = players
                .Where(p => string.Equals(p.RiotIdGameName, activeName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.SummonerName, activeName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.RiotId, activeName, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Team)
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 命中多个不同阵营（如盲选同名英雄）时不可采信
            if (matched.Count == 1)
            {
                return string.Equals(matched[0], "ORDER", StringComparison.OrdinalIgnoreCase) ? "blue" : "red";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 探测失败
        }

        return null;
    }

    public async Task LoadAllPlayersAsync(CancellationToken cancellationToken = default)
    {
        List<PremadePlayerLike> allPlayers;
        lock (_gate)
        {
            allPlayers = _state.MyTeam.Concat(_state.TheirTeam).ToList();
        }

        if (allPlayers.Count == 0)
        {
            return;
        }

        var tasks = allPlayers.Select(p =>
            LoadPlayerDataAsync(p.CellId, p.SummonerId, p.Puuid, p, cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // 加载全部玩家后，如果在对局中，尝试启动 Live Client 补全（§4.11）
        if (_observedPhase is GamePhase.GameStart or GamePhase.InProgress)
        {
            StartLiveClientPolling();
        }
    }

    public async Task<GameInfoPlayer?> LoadPlayerDataAsync(
        int cellId,
        long summonerId,
        string? puuid,
        PremadePlayerLike? fallback = null,
        CancellationToken cancellationToken = default)
    {
        string dedupeKey = !string.IsNullOrEmpty(puuid)
            ? puuid
            : (summonerId != 0 ? summonerId.ToString() : cellId.ToString());

        Task<GameInfoPlayer?> task;
        lock (_gate)
        {
            if (_inFlightLoads.TryGetValue(dedupeKey, out var existingTask))
            {
                task = existingTask;
            }
            else
            {
                task = DoLoadPlayerDataAsync(cellId, summonerId, puuid, fallback, cancellationToken);
                _inFlightLoads[dedupeKey] = task;
            }
        }

        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _inFlightLoads.Remove(dedupeKey);
            }
        }
    }

    private async Task<GameInfoPlayer?> DoLoadPlayerDataAsync(
        int cellId,
        long summonerId,
        string? puuid,
        PremadePlayerLike? fallback,
        CancellationToken cancellationToken)
    {
        // 1. 尝试复用已加载的玩家数据
        GameInfoPlayer? reusedPlayer = null;
        bool reuseNeedsMastery = false;
        string? reusePuuid = null;
        long reuseSid = 0;
        bool reuseIsMe = false;
        lock (_gate)
        {
            GameInfoPlayer? existing = null;
            if (!string.IsNullOrEmpty(puuid)) _state.PlayerData.TryGetValue(puuid, out existing);
            if (existing is null && summonerId != 0) _state.PlayerData.TryGetValue(summonerId.ToString(), out existing);
            if (existing is null) _state.PlayerData.TryGetValue(cellId.ToString(), out existing);

            if (existing is not null && !existing.Loading &&
                PlayerIdentity.IsIdentityCompatible(existing, puuid, summonerId, cellId))
            {
                if (fallback is { ChampionId: > 0 } && existing.ChampionId != fallback.ChampionId)
                {
                    existing = existing with { ChampionId = fallback.ChampionId };
                    SavePlayerToDictionary(cellId, summonerId, puuid, existing);
                }

                reusedPlayer = existing;
                // 复用的条目缺少熟练度（如部分加载异常）时，后台静默补填
                reuseNeedsMastery = (existing.Masteries is not { Count: > 0 }) &&
                    (!string.IsNullOrEmpty(puuid) || summonerId != 0);
                reusePuuid = puuid ?? existing.Info?.Puuid;
                reuseSid = summonerId != 0 ? summonerId : (existing.Info?.SummonerId ?? 0);
                reuseIsMe = (reuseSid != 0 && reuseSid == _state.CurrentSummonerId) ||
                    (!string.IsNullOrEmpty(reusePuuid) && reusePuuid == _state.CurrentSummonerPuuid);
            }
        }

        if (reusedPlayer is not null)
        {
            if (reuseNeedsMastery && (!string.IsNullOrEmpty(reusePuuid) || reuseSid != 0))
            {
                _ = BackfillMasteryAsync(reusedPlayer, reusePuuid, reuseSid, reuseIsMe, CancellationToken.None);
            }

            // 复用的是当前玩家且非对局中：后台同步其最新战绩
            if (reuseIsMe && _state.Phase is not (GamePhase.GameStart or GamePhase.InProgress))
            {
                _ = SyncCurrentPlayerMatchesAsync();
            }

            return reusedPlayer;
        }

        // 2. 电脑玩家判定（严格靠官方标记：bot / isBot / isHumanoid / botChampionId）
        bool isBot = fallback is not null && (fallback.Bot || fallback.IsBot || fallback.BotChampionId > 0 || fallback.IsHumanoid);
        if (isBot)
        {
            int botChamp = fallback!.ChampionId > 0 ? fallback.ChampionId : fallback.BotChampionId;
            var botPlayer = new GameInfoPlayer
            {
                Info = new SummonerProfile
                {
                    AccountId = 0,
                    SummonerId = summonerId,
                    SummonerLevel = 0,
                    PercentCompleteForNextLevel = 0,
                    XpSinceLastLevel = 0,
                    XpUntilNextLevel = 0,
                    Puuid = puuid ?? string.Empty,
                    DisplayName = fallback.DisplayName ?? fallback.BotName ?? "电脑",
                    GameName = fallback.GameName ?? "电脑",
                    TagLine = string.Empty,
                    ProfileIconId = fallback.ProfileIconId,
                },
                Loading = false,
                MatchHistoryHidden = true,
                ChampionId = botChamp,
            };

            SavePlayerToDictionary(cellId, summonerId, puuid, botPlayer);
            return botPlayer;
        }

        // 3. 真人玩家加载
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SummonerProfile? profile = await FetchSummonerProfileAsync(
                puuid,
                summonerId,
                fallback?.DisplayName,
                cancellationToken).ConfigureAwait(false);

            if (profile is null)
            {
                return null;
            }

            bool isPrivate = profile.Privacy is not null &&
                             !profile.Privacy.Equals("PUBLIC", StringComparison.OrdinalIgnoreCase);
            bool isHidden = isPrivate;

            bool isMe = profile.SummonerId == _state.CurrentSummonerId ||
                        (!string.IsNullOrEmpty(_state.CurrentSummonerPuuid) && profile.Puuid == _state.CurrentSummonerPuuid);

            // 并行拉取战绩、排位、熟练度
            var matchTask = _matchService.GetMatchHistoryAsync(profile.Puuid, 0, 9, cancellationToken);
            var rankTask = _careerService.GetRankedStatsAsync(profile.Puuid, cancellationToken: cancellationToken);
            var masteryTask = _masteryService.GetPlayerMasteriesAsync(profile.Puuid, profile.SummonerId, isMe, cancellationToken);

            await Task.WhenAll(matchTask, rankTask, masteryTask).ConfigureAwait(false);

            MatchHistoryPage history = await matchTask.ConfigureAwait(false);
            RankedStats ranked = await rankTask.ConfigureAwait(false);
            IReadOnlyList<PlayerChampionMastery> masteries = await masteryTask.ConfigureAwait(false);

            // 旧版口径：战绩返回空（或拉取被拒）即标隐藏；等级条件不参与判定（§0-18 / §11.0-8）
            IReadOnlyList<MatchSummary> rawMatches = history.Games;
            if (rawMatches.Count == 0)
            {
                isHidden = true;
            }

            // GameInfoFilter 开启时按当前队列过滤首屏战绩
            List<MatchSummary> matches = ApplyQueueFilter(rawMatches);

            var (winCount, lossesCount, winRate, avgKda, streak, positions, topChampions) =
                MatchStatsCalculator.ComputeMatchStats(matches);

            int champId = fallback?.ChampionId ?? 0;

            var player = new GameInfoPlayer
            {
                Info = profile,
                Matches = matches,
                Solo = ranked.SoloDuo,
                Flex = ranked.Flex,
                Loading = false,
                MatchHistoryHidden = isHidden,
                IsProfilePrivate = isPrivate,
                ChampionId = champId,
                WinCount = winCount,
                LossesCount = lossesCount,
                WinRate = winRate,
                AvgKda = avgKda,
                Streak = streak,
                Positions = positions,
                TopChampions = topChampions,
                Masteries = masteries,
                MasteryFetched = masteries.Count > 0,
            };

            SavePlayerToDictionary(cellId, summonerId, profile.Puuid, player);

            // 异步触发宿命检测
            _ = TriggerFateDetectionAsync(player, matches, cancellationToken);

            // 第二阶段：非隐藏非私密时后台静默扩充第 11~50 局样本（仅刷新统计字段）
            if (!player.MatchHistoryHidden && !player.IsProfilePrivate)
            {
                _ = EnqueueExtraMatchesAsync(player, matches);
            }

            return player;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "加载玩家数据失败 cellId={CellId}", cellId);
            return null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task TriggerFateDetectionAsync(
        GameInfoPlayer player,
        IReadOnlyList<MatchSummary> matches,
        CancellationToken cancellationToken)
    {
        if (player.Info is null || string.IsNullOrEmpty(player.Info.Puuid))
        {
            return;
        }

        long currentSummonerId = _state.CurrentSummonerId;
        if (currentSummonerId == 0 || player.Info.SummonerId == currentSummonerId)
        {
            return;
        }

        // 候选对局：目标玩家近 5 局 + 路人集相遇记录合并去重、保序（近 5 局在前，§4.7/§4.8）。
        // 注：不可仅依据当前玩家首屏战绩做交集剪枝——共同局可能在 10 局之前或跨模式
        var candidateGameIds = matches.Take(5)
            .Select(m => m.GameId)
            .Where(g => g > 0)
            .Distinct()
            .ToList();

        SavedPlayerMarker? savedMarker = null;
        if (HasSavedPlayersMapSource && !string.IsNullOrEmpty(_state.CurrentSummonerPuuid))
        {
            try
            {
                IReadOnlyDictionary<string, SavedPlayerMarker> savedMap =
                    await QuerySavedPlayersMapAsync(_state.CurrentSummonerPuuid, cancellationToken).ConfigureAwait(false);
                savedMap.TryGetValue(player.Info.Puuid, out savedMarker);
            }
            catch
            {
                // ignore saved map lookup failure
            }
        }

        if (savedMarker is not null)
        {
            IEnumerable<long> encounteredIds = savedMarker.EncounteredGameIds is { Count: > 0 } ids
                ? ids
                : (savedMarker.LastEncounteredGameId is { } lastId ? [lastId] : []);
            foreach (long gid in encounteredIds)
            {
                if (gid > 0 && !candidateGameIds.Contains(gid))
                {
                    candidateGameIds.Add(gid);
                }
            }
        }

        if (candidateGameIds.Count == 0)
        {
            return;
        }

        try
        {
            PlayerFateInfo fate = await _fateService.GetPlayerFateInfoAsync(
                candidateGameIds,
                player.Info.Puuid,
                currentSummonerId,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(fate.FateFlag))
            {
                // fateGameCreation 回退链（旧版 backfillFateInfo）：详情时间戳 → 战绩命中局时间戳 → 路人集 lastMetAt
                long? gameCreation = fate.GameCreation;
                if ((gameCreation is null || gameCreation == 0) && fate.GameId is { } hitGameId)
                {
                    MatchSummary? matched = matches.FirstOrDefault(m => m.GameId == hitGameId);
                    if (matched is not null)
                    {
                        gameCreation = matched.GameCreation;
                    }
                }

                if ((gameCreation is null || gameCreation == 0) && savedMarker?.LastMetAt is { } lastMet)
                {
                    gameCreation = lastMet;
                }

                // 命中局非该玩家战绩第 1 局时置 false（区分「上一局」与「近期」文案）
                bool isLastGame = !(matches.Count > 0 && fate.GameId is { } gid && gid != matches[0].GameId);

                lock (_gate)
                {
                    if (_state.PlayerData.TryGetValue(player.Info.Puuid, out var latest))
                    {
                        var updated = latest with
                        {
                            FateFlag = fate.FateFlag,
                            RecentlyChampionName = fate.RecentlyChampionName,
                            FateGameCreation = gameCreation,
                            FateAllyCount = fate.AllyCount,
                            FateEnemyCount = fate.EnemyCount,
                            FateIsLastGame = isLastGame,
                        };
                        SavePlayerToDictionary(0, player.Info.SummonerId, player.Info.Puuid, updated);
                    }
                }
            }
        }
        catch
        {
            // 宿命检测异常静默降级，不阻断主流程
        }
    }

    private async Task<SummonerProfile?> FetchSummonerProfileAsync(
        string? puuid,
        long summonerId,
        string? displayName,
        CancellationToken cancellationToken)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(puuid))
        {
            try
            {
                var res = await transport.SendAsync(
                    LcuRequest.Get($"/lol-summoner/v2/summoners/puuid/{puuid}"),
                    cancellationToken).ConfigureAwait(false);
                if (res.IsSuccess && !string.IsNullOrWhiteSpace(res.Body))
                {
                    using var doc = JsonDocument.Parse(res.Body);
                    var profile = SummonerParser.ParseSummoner(doc.RootElement);
                    if (profile is not null) return profile;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* ignore */ }
        }

        if (summonerId != 0)
        {
            try
            {
                var res = await transport.SendAsync(
                    LcuRequest.Get($"/lol-summoner/v1/summoners/{summonerId}"),
                    cancellationToken).ConfigureAwait(false);
                if (res.IsSuccess && !string.IsNullOrWhiteSpace(res.Body))
                {
                    using var doc = JsonDocument.Parse(res.Body);
                    var profile = SummonerParser.ParseSummoner(doc.RootElement);
                    if (profile is not null) return profile;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* ignore */ }
        }

        if (!string.IsNullOrEmpty(displayName) && PlayerIdentity.IsRealSummonerName(displayName))
        {
            try
            {
                var res = await transport.SendAsync(
                    LcuRequest.Get($"/lol-summoner/v1/summoners?name={Uri.EscapeDataString(displayName)}"),
                    cancellationToken).ConfigureAwait(false);
                if (res.IsSuccess && !string.IsNullOrWhiteSpace(res.Body))
                {
                    using var doc = JsonDocument.Parse(res.Body);
                    var profile = SummonerParser.ParseSummoner(doc.RootElement);
                    if (profile is not null) return profile;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* ignore */ }
        }

        return null;
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
            JsonElement root = doc.RootElement;

            var session = ParseChampSelectSession(root);

            // 队伍归属以 LCU 原生语义为准：myTeam（含自定义人机 isHumanoid）= 我方，theirTeam = 敌方。
            // isHumanoid 成员补标 bot（下游走本地占位，不请求战绩接口），不改变归属
            bool IsBotMember(ChampSelectMemberLike m) => m.IsHumanoid;

            var myMembers = session.MyTeam ?? [];
            var theirMembers = (session.TheirTeam ?? [])
                .Where(m => m.IsHumanoid || !string.IsNullOrEmpty(m.Puuid) || m.SummonerId != 0 || m.ChampionId > 0)
                .ToList();

            // 自定义会话（敌方 cell 可能与我方共用编号空间）：敌方需重映射到 5+ 稳定区间隔离
            bool isCustomSession = session.IsCustomGame ||
                myMembers.Any(m => m.IsHumanoid) ||
                (session.TheirTeam ?? []).Any(m => m.IsHumanoid);

            var myTeam = myMembers.Select(m => new PremadePlayerLike
            {
                CellId = m.CellId,
                ChampionId = ChampionResolver.ResolvePlayerChampionId(
                    new PremadePlayerLike { CellId = m.CellId, Puuid = m.Puuid, SummonerId = m.SummonerId },
                    session),
                ChampionPickIntent = m.ChampionPickIntent,
                Puuid = m.Puuid,
                SummonerId = m.SummonerId,
                IsEnemy = false,
                Bot = IsBotMember(m),
                IsBot = IsBotMember(m),
                IsHumanoid = m.IsHumanoid,
            }).ToList();

            var theirTeam = theirMembers.Select((m, idx) => new PremadePlayerLike
            {
                CellId = isCustomSession ? 5 + idx : m.CellId,
                ChampionId = ChampionResolver.ResolvePlayerChampionId(
                    new PremadePlayerLike { CellId = m.CellId, Puuid = m.Puuid, SummonerId = m.SummonerId, IsEnemy = true },
                    session),
                ChampionPickIntent = m.ChampionPickIntent,
                Puuid = m.Puuid,
                SummonerId = m.SummonerId,
                IsEnemy = true,
                Bot = IsBotMember(m),
                IsBot = IsBotMember(m),
                IsHumanoid = m.IsHumanoid,
            }).ToList();

            // 增量维护选人阶段双方快照：新帧未识别出英雄时沿用上一帧的 championId / 组队 ID，
            // 避免过渡帧把已锁定的英雄冲成 0（对应旧版 mergeSnapshot）
            lock (_gate)
            {
                _champSelectTeamSnapshot = MergeChampSelectSnapshot(myTeam, _champSelectTeamSnapshot);
                _champSelectTheirTeamSnapshot = MergeChampSelectSnapshot(theirTeam, _champSelectTheirTeamSnapshot);
                myTeam = _champSelectTeamSnapshot;
                theirTeam = _champSelectTheirTeamSnapshot;
            }

            var newPlayerData = new Dictionary<string, GameInfoPlayer>(_state.PlayerData);
            TeamBuilder.SeedInitialPlayerData(myTeam.Concat(theirTeam).ToList(), newPlayerData);

            var colorsMy = PremadeAnalyzer.ComputePremadeColors(myTeam);
            var colorsTheir = PremadeAnalyzer.ComputePremadeColors(theirTeam);

            UpdateState(s => s with
            {
                MyTeam = myTeam,
                TheirTeam = theirTeam,
                PlayerData = newPlayerData,
                PremadeColorsMy = colorsMy,
                PremadeColorsTheir = colorsTheir,
                ShouldShowContent = true,
                IsLoadingTeams = false,
                // 装配出真实队伍即脱离演示态：演示数据的 puuid 是占位值，不能用于跳转查询
                IsDemoData = false,
            });

            if (myTeam.Count > 0)
            {
                lock (_gate)
                {
                    _champSelectSeenForGame = true;
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

    /// <summary>
    /// 选人阶段快照增量合并（对应旧版 <c>mergeSnapshot</c>）：
    /// 按 puuid / summonerId / cellId 对齐上一帧，英雄 ID 与组队 ID 只增不减。
    /// </summary>
    private static List<PremadePlayerLike> MergeChampSelectSnapshot(
        List<PremadePlayerLike> incoming,
        List<PremadePlayerLike> previous)
    {
        if (previous.Count == 0)
        {
            return incoming;
        }

        var merged = new List<PremadePlayerLike>(incoming.Count);
        foreach (var p in incoming)
        {
            var prev = previous.FirstOrDefault(old =>
                (!string.IsNullOrEmpty(p.Puuid) && !string.IsNullOrEmpty(old.Puuid) && p.Puuid == old.Puuid) ||
                (p.SummonerId != 0 && old.SummonerId != 0 && p.SummonerId == old.SummonerId) ||
                old.CellId == p.CellId);

            int champId = p.ChampionId > 0
                ? p.ChampionId
                : (prev is { ChampionId: > 0 } ? prev.ChampionId : 0);

            string? tpid = PlayerIdentity.GetValidTpid(p) ?? PlayerIdentity.GetValidTpid(prev);

            merged.Add(p with
            {
                ChampionId = champId,
                TeamParticipantId = tpid ?? p.TeamParticipantId,
                PartyId = tpid ?? p.PartyId,
                SummonerId = p.SummonerId != 0 ? p.SummonerId : (prev?.SummonerId ?? 0),
                Puuid = !string.IsNullOrEmpty(p.Puuid) ? p.Puuid : prev?.Puuid,
            });
        }

        return merged;
    }

    private static ChampSelectSessionLike ParseChampSelectSession(JsonElement root)
    {
        var actionsList = new List<List<ChampSelectActionLike>>();
        if (root.TryGetProperty("actions", out var actionsProp) && actionsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in actionsProp.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Array) continue;
                var groupList = new List<ChampSelectActionLike>();
                foreach (var act in group.EnumerateArray())
                {
                    int actorCellId = JsonRead.Int(act, "actorCellId", -1);
                    int champId = JsonRead.Int(act, "championId", 0);
                    bool completed = JsonRead.Bool(act, "completed", false);
                    bool inProgress = JsonRead.Bool(act, "isInProgress", false);
                    string type = JsonRead.String(act, "type") ?? string.Empty;
                    groupList.Add(new ChampSelectActionLike(actorCellId, champId, completed, inProgress, type));
                }
                actionsList.Add(groupList);
            }
        }

        var myTeamList = ParseChampSelectMembers(root, "myTeam");
        var theirTeamList = ParseChampSelectMembers(root, "theirTeam");
        bool isCustomGame = JsonRead.Bool(root, "isCustomGame", false);

        return new ChampSelectSessionLike(actionsList, myTeamList, theirTeamList, isCustomGame);
    }

    private static List<ChampSelectMemberLike> ParseChampSelectMembers(JsonElement root, string propName)
    {
        var list = new List<ChampSelectMemberLike>();
        if (root.TryGetProperty(propName, out var teamProp) && teamProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in teamProp.EnumerateArray())
            {
                int cellId = JsonRead.Int(m, "cellId", -1);
                int champId = JsonRead.Int(m, "championId", 0);
                int pickIntent = JsonRead.Int(m, "championPickIntent", 0);
                string? puuid = JsonRead.String(m, "puuid");
                long sid = JsonRead.Long(m, "summonerId", 0);
                bool isHumanoid = JsonRead.Bool(m, "isHumanoid", false);
                list.Add(new ChampSelectMemberLike(cellId, champId, pickIntent, puuid, sid, isHumanoid));
            }
        }
        return list;
    }

    private static List<PremadePlayerLike> ParseParticipants(JsonElement gameData, string teamName)
    {
        var list = new List<PremadePlayerLike>();
        if (gameData.TryGetProperty(teamName, out var team) && team.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in team.EnumerateArray())
            {
                long sid = JsonRead.Long(p, "summonerId", 0);
                string? puuid = JsonRead.String(p, "puuid");
                string? displayName = JsonRead.String(p, "displayName");
                string? summonerName = JsonRead.String(p, "summonerName");
                string? gameName = JsonRead.String(p, "gameName");
                string? tagLine = JsonRead.String(p, "tagLine");
                int profileIconId = JsonRead.Int(p, "profileIconId", 0);
                int championId = JsonRead.Int(p, "championId", 0);
                int cellId = JsonRead.Int(p, "cellId", 0);
                string? tpid = JsonRead.String(p, "teamParticipantId") ?? JsonRead.Long(p, "teamParticipantId", 0).ToString();
                string? partyId = JsonRead.String(p, "partyId") ?? JsonRead.Long(p, "partyId", 0).ToString();
                bool isHumanoid = JsonRead.Bool(p, "isHumanoid", false);
                bool bot = JsonRead.Bool(p, "bot", false) || JsonRead.Bool(p, "isBot", false) || isHumanoid;
                int botChampId = JsonRead.Int(p, "botChampionId", 0);
                string? botName = JsonRead.String(p, "botName");

                list.Add(new PremadePlayerLike
                {
                    SummonerId = sid,
                    Puuid = puuid,
                    DisplayName = displayName,
                    SummonerName = summonerName,
                    GameName = gameName,
                    TagLine = tagLine,
                    ProfileIconId = profileIconId,
                    ChampionId = championId,
                    CellId = cellId,
                    TeamParticipantId = tpid,
                    PartyId = partyId,
                    Bot = bot,
                    IsBot = bot,
                    IsHumanoid = isHumanoid,
                    BotChampionId = botChampId,
                    BotName = botName,
                });
            }
        }
        return list;
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
                if (doc.RootElement.TryGetProperty("gameData", out var gd) &&
                    gd.TryGetProperty("queue", out var queue))
                {
                    int qId = JsonRead.Int(queue, "id", 0);
                    string? gameMode = JsonRead.String(queue, "gameMode");
                    bool isTft = string.Equals(gameMode, "TFT", StringComparison.OrdinalIgnoreCase) ||
                                (qId >= 1090 && qId <= 1200);

                    if (isTft)
                    {
                        // TFT 清全部（含保留快照），对局信息页显示云顶占位
                        lock (_gate)
                        {
                            _reservedSnapshot = null;
                            _currentGameId = null;
                            _state = _state with
                            {
                                MyTeam = [],
                                TheirTeam = [],
                                PlayerData = new Dictionary<string, GameInfoPlayer>(),
                                PremadeColorsMy = new Dictionary<string, int>(),
                                PremadeColorsTheir = new Dictionary<string, int>(),
                                IsLoadingTeams = false,
                                ShouldShowContent = false,
                                CurrentQueueId = qId,
                                IsTftMode = true,
                            };
                            _stateStream.Publish(_state);
                        }
                        return;
                    }

                    UpdateState(s => s with
                    {
                        CurrentQueueId = qId,
                        IsTftMode = isTft,
                    });
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private void HandleOutOfGamePhase(GamePhase phase)
    {
        bool hasMemoryData;
        lock (_gate)
        {
            hasMemoryData = _state.MyTeam.Count > 0 && _state.PlayerData.Count > 0;
        }

        // 内存无数据（如中途启动/选人秒退后）：仅配置开启时恢复上一局保留快照
        if (!hasMemoryData && _config.Functions.EnableReserveGameinfo && TryRestoreReservedSnapshot())
        {
            hasMemoryData = true;
        }

        lock (_gate)
        {
            // 旧版语义：非活跃阶段展示保留数据必须 EnableReserveGameinfo=true（§4.9）
            bool show = _config.Functions.EnableReserveGameinfo && _state.MyTeam.Count > 0 && _state.PlayerData.Count > 0;
            _state = _state with { Phase = phase, ShouldShowContent = show };
            _stateStream.Publish(_state);
        }
    }

    /// <summary>
    /// 阶段观测：等价旧版 gamePhase watch + isGameActive watch + gameEndedTrigger watch 的状态清理与触发链。
    /// </summary>
    private void HandlePhaseObserved(GamePhase phase)
    {
        GamePhase prev;
        lock (_gate)
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
            CancelLiveClientPolling();
        }

        if (isActive)
        {
            // 重新进入活跃对局：立即停止对局结束链式轮询
            StopGameEndedChain();

            if (phase == GamePhase.ChampSelect)
            {
                // 进入选人：清内存视图与上一局数据（保留快照，防秒退丢上一局完整对局）
                lock (_gate)
                {
                    _currentGameId = null;
                    _champSelectSeenForGame = false;
                    _champSelectTeamSnapshot = [];
                    _champSelectTheirTeamSnapshot = [];
                    _sessionAllyTeam = [];
                    _sessionEnemyTeam = [];
                }

                ClearInGameMemory();

                // 进入选人：加载路人集映射（§4.8）
                LoadSavedPlayerMap();
            }
            else if (phase is GamePhase.GameStart or GamePhase.InProgress && !wasActive)
            {
                if (!_champSelectSeenForGame)
                {
                    // 未经历本局选人快照（直连进游戏/大乱斗/中途启动）：清残留防上一局污染
                    ClearInGameMemory();
                }
                else
                {
                    // 清理选人阶段以数字 0..9 临时缓存的旧键（红方槽位与敌方槽位碰撞防护）
                    RemoveCellIdKeysFromPlayerData();
                }
            }
        }
        else if (wasActive)
        {
            // 离开游戏活跃状态（回到 Lobby / EndOfGame 等）
            StopGameEndedChain();
            _champSelectSeenForGame = false;

            bool hasData;
            lock (_gate)
            {
                hasData = _state.MyTeam.Count > 0 && _state.PlayerData.Count > 0;
            }

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

            if (phase is GamePhase.WaitingForStats or GamePhase.PreEndOfGame or GamePhase.EndOfGame or GamePhase.Lobby or GamePhase.None)
            {
                // 对局结束：链式梯度轮询拉取刚结束对局的最新战绩
                NotifyGameEnded();
            }
        }
        else if (phase == GamePhase.None)
        {
            // None：清内存态但保留保留快照
            lock (_gate)
            {
                _currentGameId = null;
            }

            ClearInGameMemory();
        }
    }

    /// <summary>清内存态（队伍/玩家数据/组队色），但保留保留快照与 gameId 归属以外的内存标记。</summary>
    private void ClearInGameMemory()
    {
        lock (_gate)
        {
            _state = _state with
            {
                MyTeam = [],
                TheirTeam = [],
                PlayerData = new Dictionary<string, GameInfoPlayer>(),
                PremadeColorsMy = new Dictionary<string, int>(),
                PremadeColorsTheir = new Dictionary<string, int>(),
                // 阵营随每局重新判定，残留值会在下一局脱敏场景下造成双方整体错位
                MapSide = null,
                IsLoadingTeams = false,
                ShouldShowContent = false,
                IsDemoData = false,
            };
            _stateStream.Publish(_state);
        }
    }

    /// <summary>移除 PlayerData 中 0..9 的 cellId 数字键（保留 summonerId 大数字键）。</summary>
    private void RemoveCellIdKeysFromPlayerData()
    {
        lock (_gate)
        {
            if (_state.PlayerData.Count == 0)
            {
                return;
            }

            var dict = new Dictionary<string, GameInfoPlayer>(_state.PlayerData);
            bool changed = false;
            for (int i = 0; i <= 9; i++)
            {
                changed |= dict.Remove(i.ToString());
            }

            if (!changed)
            {
                return;
            }

            _state = _state with { PlayerData = dict };
            _stateStream.Publish(_state);
        }
    }

    /// <summary>
    /// 写入保留快照（<see cref="System.Threading.Lock"/> 可重入，允许在外层持锁时调用）。
    /// 规则：有我方队伍且至少 1 人已加载才写；loading 占位不入快照；
    /// 非 GameStart/InProgress 阶段新快照已加载数缩水不覆盖；
    /// 同一 gameId 下当前队伍总人数缩水不覆盖（防 InProgress 精简 session 毒化完整快照）。
    /// </summary>
    private void WriteReservedSnapshot()
    {
        GameInfoState state;
        ReservedSnapshot? saved;
        long? currentGameId;
        lock (_gate)
        {
            state = _state;
            saved = _reservedSnapshot;
            currentGameId = _currentGameId;
        }

        if (state.MyTeam.Count == 0)
        {
            return;
        }

        // loading 占位不入快照：恢复后没有重拉逻辑，会永久停留在转圈态
        var persistable = new Dictionary<string, GameInfoPlayer>();
        var loadedIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entry) in state.PlayerData)
        {
            if (entry is not { Loading: false, Info: not null })
            {
                continue;
            }

            persistable[key] = entry;
            loadedIdentities.Add(!string.IsNullOrEmpty(entry.Info.Puuid)
                ? entry.Info.Puuid
                : entry.Info.SummonerId.ToString());
        }

        int loadedCount = loadedIdentities.Count;
        if (loadedCount == 0)
        {
            return;
        }

        // 防缩水覆盖：真实对局阶段始终写入保证时效性；其余阶段仅在新快照数据规模不缩水时覆盖
        bool inRealGame = state.Phase is GamePhase.GameStart or GamePhase.InProgress;
        if (!inRealGame && saved is not null && loadedCount < saved.LoadedCount)
        {
            return;
        }

        // 同一局内禁止用更瘦的快照覆盖：InProgress 阶段 LCU 会把 gameflow 队伍精简到仅剩自己
        int currentTotal = state.MyTeam.Count + state.TheirTeam.Count;
        if (currentGameId is not null &&
            saved is { GameId: { } savedGameId } &&
            savedGameId == currentGameId &&
            saved.TeamCount > 0 &&
            currentTotal < saved.TeamCount)
        {
            return;
        }

        lock (_gate)
        {
            _reservedSnapshot = new ReservedSnapshot(
                state.MyTeam,
                state.TheirTeam,
                persistable,
                state.PremadeColorsMy,
                state.PremadeColorsTheir,
                currentGameId,
                currentTotal,
                loadedCount);
        }
    }

    /// <summary>
    /// 恢复保留快照。活跃阶段仅当快照 gameId 与当前 live gameId 一致才恢复（同局精简修复场景）；
    /// 非活跃阶段必须 EnableReserveGameinfo=true。恢复后剔除 loading 占位、后台补缺失熟练度、
    /// 触发当前玩家战绩同步。
    /// </summary>
    private bool TryRestoreReservedSnapshot()
    {
        ReservedSnapshot? snapshot;
        lock (_gate)
        {
            snapshot = _reservedSnapshot;
            if (snapshot is null || snapshot.PlayerData.Count == 0)
            {
                return false;
            }

            if (_state.Phase.IsGameActive())
            {
                // 活跃阶段仅同局精简修复场景允许恢复；跨局严禁
                if (snapshot.GameId is not { } savedId || _currentGameId is not { } liveId || savedId != liveId)
                {
                    return false;
                }
            }
            else if (!_config.Functions.EnableReserveGameinfo)
            {
                return false;
            }
        }

        // 剔除 loading 占位（写入时已过滤，双保险）
        var restored = new Dictionary<string, GameInfoPlayer>();
        foreach (var (key, entry) in snapshot.PlayerData)
        {
            if (entry is not { Loading: true })
            {
                restored[key] = entry;
            }
        }

        if (restored.Count == 0)
        {
            return false;
        }

        // 组队色缺失时按队伍重算
        var colorsMy = snapshot.PremadeColorsMy.Count > 0
            ? snapshot.PremadeColorsMy
            : PremadeAnalyzer.ComputePremadeColors(snapshot.MyTeam);
        var colorsTheir = snapshot.PremadeColorsTheir.Count > 0
            ? snapshot.PremadeColorsTheir
            : PremadeAnalyzer.ComputePremadeColors(snapshot.TheirTeam);

        lock (_gate)
        {
            _state = _state with
            {
                MyTeam = snapshot.MyTeam,
                TheirTeam = snapshot.TheirTeam,
                PlayerData = restored,
                PremadeColorsMy = colorsMy,
                PremadeColorsTheir = colorsTheir,
                IsLoadingTeams = false,
                ShouldShowContent = true,
            };
            _stateStream.Publish(_state);
        }

        // 异步补齐缺少熟练度的玩家 + 同步当前玩家最新战绩 + 恢复后加载路人集映射（§4.8）
        _ = BackfillMissingMasteriesAsync(restored.Values);
        _ = SyncCurrentPlayerMatchesAsync();
        LoadSavedPlayerMap();
        return true;
    }

    /// <summary>对快照恢复后的玩家批量后台补拉缺失熟练度（每人间隔 100ms 防突发并发）。</summary>
    private async Task BackfillMissingMasteriesAsync(IEnumerable<GameInfoPlayer> players)
    {
        List<GameInfoPlayer> needBackfill = players
            .Where(e => e is { Loading: false, Info: not null } &&
                        e.Masteries is not { Count: > 0 } &&
                        (!string.IsNullOrEmpty(e.Info.Puuid) || e.Info.SummonerId != 0))
            .GroupBy(e => !string.IsNullOrEmpty(e.Info!.Puuid) ? e.Info.Puuid : e.Info.SummonerId.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (needBackfill.Count == 0)
        {
            return;
        }

        foreach (var player in needBackfill)
        {
            await Task.Delay(100).ConfigureAwait(false);
            if (_disposed)
            {
                return;
            }

            await BackfillMasteryAsync(
                player,
                player.Info!.Puuid,
                player.Info.SummonerId,
                isMe: player.Info.SummonerId == CurrentState.CurrentSummonerId ||
                      player.Info.Puuid == CurrentState.CurrentSummonerPuuid,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>后台补拉单个玩家的熟练度并写入（写入前做条目归属校验）。</summary>
    private async Task BackfillMasteryAsync(
        GameInfoPlayer player,
        string? puuid,
        long summonerId,
        bool isMe,
        CancellationToken cancellationToken)
    {
        try
        {
            var masteries = await _masteryService.GetPlayerMasteriesAsync(puuid, summonerId, isMe, cancellationToken)
                .ConfigureAwait(false);
            if (masteries.Count == 0)
            {
                return;
            }

            lock (_gate)
            {
                // 归属校验：该身份当前条目仍是本次加载的对象，防补填期间被重载后误写
                string key = !string.IsNullOrEmpty(puuid) ? puuid : (summonerId != 0 ? summonerId.ToString() : string.Empty);
                if (key.Length == 0 ||
                    !_state.PlayerData.TryGetValue(key, out var current) ||
                    !ReferenceEquals(current, player))
                {
                    return;
                }

                var updated = current with { Masteries = masteries, MasteryFetched = true };
                var dict = new Dictionary<string, GameInfoPlayer>(_state.PlayerData);
                foreach (var k in dict.Keys.ToList())
                {
                    if (ReferenceEquals(dict[k], player))
                    {
                        dict[k] = updated;
                    }
                }

                _state = _state with { PlayerData = dict };
                _stateStream.Publish(_state);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 静默降级，保持 MasteryFetched=false 以便后续重试
        }
    }

    /// <summary>
    /// 同步当前玩家最新战绩（§4.10）：10s 节流（force 可跳过）；SGP 强制拉 0..49，
    /// 失败回退 IMatchService；GameInfoFilter 开启时按当前队列过滤；
    /// 命中自己的所有条目（puuid/sid/cellId 多键）统一更新前 10 局与全部统计字段并清隐藏标记。
    /// </summary>
    public async Task SyncCurrentPlayerMatchesAsync(bool forceNetwork = false, CancellationToken cancellationToken = default)
    {
        if (_disposed)
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
            string puuid;
            long sid;
            lock (_gate)
            {
                puuid = _state.CurrentSummonerPuuid ?? string.Empty;
                sid = _state.CurrentSummonerId;
            }

            if (string.IsNullOrEmpty(puuid) || sid == 0)
            {
                try
                {
                    var current = await _summonerService.GetCurrentSummonerAsync(cancellationToken).ConfigureAwait(false);
                    if (current is not null)
                    {
                        puuid = current.Puuid;
                        sid = current.SummonerId;
                        UpdateState(s => s with
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
                catch
                {
                    // ignore
                }
            }

            if (string.IsNullOrEmpty(puuid))
            {
                return;
            }

            // 命中自己的所有条目（多键索引，唯一对象去重）
            List<GameInfoPlayer> targets;
            lock (_gate)
            {
                var myCellKey = _state.MyTeam
                    .FirstOrDefault(p => (!string.IsNullOrEmpty(p.Puuid) && p.Puuid == puuid) || (sid != 0 && p.SummonerId == sid))
                    ?.CellId.ToString();

                targets = [];
                foreach (var (key, entry) in _state.PlayerData)
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
                catch
                {
                    // ignore，走回退
                }
            }

            if (raw.Count == 0)
            {
                try
                {
                    MatchHistoryPage page = await _matchService.GetMatchHistoryAsync(puuid, 0, 49, cancellationToken).ConfigureAwait(false);
                    raw = page.Games;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
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

            lock (_gate)
            {
                var dict = new Dictionary<string, GameInfoPlayer>(_state.PlayerData);
                foreach (var key in dict.Keys.ToList())
                {
                    if (!targets.Any(t => ReferenceEquals(t, dict[key])))
                    {
                        continue;
                    }

                    dict[key] = dict[key] with
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
                    };
                }

                _state = _state with { PlayerData = dict };
                _stateStream.Publish(_state);
            }
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

    /// <summary>GameInfoFilter 开启时按 CurrentQueueId 过滤战绩；CurrentQueueId 无效时不过滤。</summary>
    private List<MatchSummary> ApplyQueueFilter(IReadOnlyList<MatchSummary> matches)
    {
        if (!_config.Functions.GameInfoFilter)
        {
            return matches.ToList();
        }

        int? queueId;
        lock (_gate)
        {
            queueId = _state.CurrentQueueId is int q && q > 0 ? q : null;
        }

        return queueId is null
            ? matches.ToList()
            : matches.Where(m => m.QueueId == queueId.Value).ToList();
    }

    /// <summary>低优先级战绩扩充任务入队（单任务顺序执行，排在首屏之后）。</summary>
    private async Task EnqueueExtraMatchesAsync(GameInfoPlayer player, IReadOnlyList<MatchSummary> initialMatches)
    {
        await _extraMatchesGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ExpandPlayerMatchesAsync(player, initialMatches).ConfigureAwait(false);
        }
        catch
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

        // 归属校验：该 puuid 当前条目仍是本次加载的对象，防补填期间被重载后误写
        lock (_gate)
        {
            if (!_state.PlayerData.TryGetValue(puuid, out var current) || !ReferenceEquals(current, player))
            {
                return;
            }
        }

        IReadOnlyList<MatchSummary> extra = [];
        try
        {
            MatchHistoryPage page = await _matchService.GetMatchHistoryAsync(puuid, 10, 49).ConfigureAwait(false);
            extra = page.Games;
        }
        catch
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
            catch
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

        lock (_gate)
        {
            // 写入前归属校验 + 只更新统计字段，不改变 Matches 列表内容（展示仍取前 10）
            if (!_state.PlayerData.TryGetValue(puuid, out var current) || !ReferenceEquals(current, player))
            {
                return;
            }

            var updated = current with
            {
                WinCount = stats.WinCount,
                LossesCount = stats.LossesCount,
                WinRate = stats.WinRate,
                AvgKda = stats.AvgKda,
                Streak = stats.Streak,
                Positions = stats.Positions,
                TopChampions = stats.TopChampions,
            };

            var dict = new Dictionary<string, GameInfoPlayer>(_state.PlayerData);
            foreach (var key in dict.Keys.ToList())
            {
                if (ReferenceEquals(dict[key], player))
                {
                    dict[key] = updated;
                }
            }

            _state = _state with { PlayerData = dict };
            _stateStream.Publish(_state);
        }
    }

    /// <summary>
    /// 对局结束通知：启动 2s/3.5s/5s 链式轮询当前玩家战绩。
    /// 阶段重新进入活跃或再次结束时立即取消旧链（先 resolve 再 clear 语义，避免 await 永挂）。
    /// </summary>
    public void NotifyGameEnded()
    {
        if (_disposed)
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

                if (_disposed || cts.IsCancellationRequested)
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
                catch
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

    private void StopGameEndedChain()
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

    private void SavePlayerToDictionary(int cellId, long summonerId, string? puuid, GameInfoPlayer player)
    {
        lock (_gate)
        {
            var dict = new Dictionary<string, GameInfoPlayer>(_state.PlayerData)
            {
                [cellId.ToString()] = player,
            };

            if (summonerId != 0)
            {
                dict[summonerId.ToString()] = player;
            }

            if (!string.IsNullOrEmpty(puuid))
            {
                dict[puuid] = player;
            }

            _state = _state with { PlayerData = dict };
            _stateStream.Publish(_state);
        }

        // 每次玩家数据落位后更新保留快照（内存态无需防抖，写入规则自带缩水保护）
        WriteReservedSnapshot();
    }

    private async Task EnsureCurrentSummonerAsync(CancellationToken cancellationToken)
    {
        if (_state.CurrentSummonerId != 0 && !string.IsNullOrEmpty(_state.CurrentSummonerPuuid))
        {
            return;
        }

        var current = await _summonerService.GetCurrentSummonerAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            string? previousPuuid;
            lock (_gate)
            {
                previousPuuid = _state.CurrentSummonerPuuid;
            }

            UpdateState(s => s with
            {
                CurrentSummonerId = current.SummonerId,
                CurrentSummonerPuuid = current.Puuid,
            });

            // 当前召唤师变化（含首次加载）：加载路人集映射（§4.8）
            if (!string.IsNullOrEmpty(current.Puuid) &&
                !string.Equals(previousPuuid, current.Puuid, StringComparison.Ordinal))
            {
                LoadSavedPlayerMap();
            }
        }
    }

    private void UpdateState(Func<GameInfoState, GameInfoState> updater)
    {
        lock (_gate)
        {
            _state = updater(_state);
            _stateStream.Publish(_state);
        }
    }

    private void OnConnectionStateChanged(LcuConnectionState state)
    {
        if (state.Status == LcuConnectionStatus.Connected)
        {
            _ = RefreshStateAsync();
            // 连接变化：加载路人集映射（§4.8；首次连接时召唤师尚未就绪，由召唤师变化触发兜底）
            LoadSavedPlayerMap();
        }
        else if (state.Status == LcuConnectionStatus.Disconnected)
        {
            UpdateState(s => s with { Phase = GamePhase.None, ShouldShowContent = false });
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
                UpdateState(s => s with { Phase = phase });
                HandlePhaseObserved(phase);
                _ = RefreshStateAsync();
            }
        }
        else if (ev.Uri.StartsWith("/lol-champ-select/v1/session", StringComparison.OrdinalIgnoreCase))
        {
            if (_state.Phase == GamePhase.ChampSelect)
            {
                _ = LoadFromChampSelectAsync(CancellationToken.None);
            }
        }
        else if (ev.Uri.StartsWith("/lol-gameflow/v1/session", StringComparison.OrdinalIgnoreCase))
        {
            if (_state.Phase is GamePhase.GameStart or GamePhase.InProgress)
            {
                _ = LoadFromGameflowSessionAsync(CancellationToken.None);
            }
        }
    }

    // ── Live Client Data 补全（2999 端口，敌方脱敏补全，§4.11）─────────────────

    private void CancelLiveClientPolling()
    {
        lock (_gate)
        {
            _liveClientPollCts?.Cancel();
            _liveClientPollCts?.Dispose();
            _liveClientPollCts = null;
            _queriedLiveClientNames.Clear();
        }
    }

    private void StartLiveClientPolling()
    {
        if (_liveClientService is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_observedPhase != GamePhase.GameStart && _observedPhase != GamePhase.InProgress)
            {
                return;
            }

            _liveClientPollCts?.Cancel();
            _liveClientPollCts?.Dispose();
            _liveClientPollCts = new CancellationTokenSource();
            var token = _liveClientPollCts.Token;

            _ = Task.Run(() => PollLiveClientChampionsLoopAsync(token), token);
        }
    }

    private async Task PollLiveClientChampionsLoopAsync(CancellationToken cancellationToken)
    {
        for (int retry = 0; retry < LiveClientMaxRetries; retry++)
        {
            if (cancellationToken.IsCancellationRequested) return;

            lock (_gate)
            {
                if (_observedPhase != GamePhase.GameStart && _observedPhase != GamePhase.InProgress)
                {
                    return;
                }
            }

            bool allHaveHeroes;
            lock (_gate)
            {
                var all = _state.MyTeam.Concat(_state.TheirTeam);
                allHaveHeroes = all.All(p => p.ChampionId > 0);
            }

            if (allHaveHeroes && retry > 0)
            {
                return;
            }

            bool updated = await PollLiveClientChampionsOnceAsync(cancellationToken).ConfigureAwait(false);
            if (updated)
            {
                WriteReservedSnapshot();
            }

            try
            {
                await Task.Delay(LiveClientPollDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal Task<bool> PollLiveClientChampionsOnceForTestAsync(CancellationToken cancellationToken = default)
    {
        return PollLiveClientChampionsOnceAsync(cancellationToken);
    }

    private async Task<bool> PollLiveClientChampionsOnceAsync(CancellationToken cancellationToken)
    {
        if (_liveClientService is null) return false;

        var clientList = await _liveClientService.GetPlayerListAsync(cancellationToken).ConfigureAwait(false);
        if (clientList.Count == 0) return false;

        var catalogObj = _catalogProvider?.Invoke();
        var catalog = catalogObj?.Champions;
        var aliases = catalogObj?.ChampionAliases;
        bool hasUpdated = false;

        static string NormalizeName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            int hash = name.IndexOf('#');
            string pure = hash >= 0 ? name[..hash] : name;
            return pure.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        }

        // 1. 推导我方 LiveClient 阵营（ORDER 蓝方 / CHAOS 红方，§4.11）
        string? myLiveClientTeam = null;
        lock (_gate)
        {
            if (_state.MapSide == "blue") myLiveClientTeam = "ORDER";
            else if (_state.MapSide == "red") myLiveClientTeam = "CHAOS";
        }

        if (string.IsNullOrEmpty(myLiveClientTeam))
        {
            List<PremadePlayerLike> allyRealPlayers;
            lock (_gate)
            {
                allyRealPlayers = _state.MyTeam.Where(p =>
                {
                    string rawName = p.GameName ?? p.DisplayName ?? p.SummonerName ?? string.Empty;
                    bool hasIdentity = !string.IsNullOrEmpty(p.Puuid) || !string.IsNullOrEmpty(p.TagLine) ||
                                       (p.SummonerId != 0 && p.SummonerId != p.CellId);
                    return PlayerIdentity.IsRealSummonerName(rawName, p.IsBot, hasIdentity);
                }).ToList();
            }

            foreach (var lp in clientList)
            {
                var lpNames = new[] { NormalizeName(lp.RiotIdGameName), NormalizeName(lp.SummonerName), NormalizeName(lp.RiotId) }
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                bool isMyAlly = allyRealPlayers.Any(p =>
                {
                    var pNames = new[] { NormalizeName(p.GameName), NormalizeName(p.DisplayName), NormalizeName(p.SummonerName) }
                        .Where(s => !string.IsNullOrEmpty(s));
                    return pNames.Any(pn => lpNames.Contains(pn));
                });

                if (isMyAlly && !string.IsNullOrEmpty(lp.Team))
                {
                    myLiveClientTeam = lp.Team;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(myLiveClientTeam))
        {
            string? activeName = await _liveClientService.GetActivePlayerNameAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(activeName) && PlayerIdentity.IsRealSummonerName(activeName, false, true))
            {
                string activeNorm = NormalizeName(activeName);
                var matchedTeams = clientList
                    .Where(lp => new[] { NormalizeName(lp.RiotIdGameName), NormalizeName(lp.SummonerName), NormalizeName(lp.RiotId) }.Contains(activeNorm))
                    .Select(lp => lp.Team)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (matchedTeams.Count == 1)
                {
                    myLiveClientTeam = matchedTeams[0];
                }
            }
        }

        if (string.IsNullOrEmpty(myLiveClientTeam))
        {
            lock (_gate)
            {
                myLiveClientTeam = _state.MapSide == "red" ? "CHAOS" : "ORDER";
            }
        }

        string deducedMapSide = string.Equals(myLiveClientTeam, "ORDER", StringComparison.OrdinalIgnoreCase) ? "blue" : "red";
        lock (_gate)
        {
            if (string.IsNullOrEmpty(_state.MapSide))
            {
                _state = _state with { MapSide = deducedMapSide };
                _stateStream.Publish(_state);
            }
        }

        var enemyLiveClients = clientList.Where(lp => !string.Equals(lp.Team, myLiveClientTeam, StringComparison.OrdinalIgnoreCase)).ToList();
        var matchedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var lp in clientList)
        {
            int champId = LiveClientChampionMatcher.FindChampionId(catalog, lp.ChampionName, lp.RawChampionName, aliases);
            if (champId <= 0) continue;

            bool isEnemyLp = !string.Equals(lp.Team, myLiveClientTeam, StringComparison.OrdinalIgnoreCase);
            List<PremadePlayerLike> candidateTeam;
            lock (_gate)
            {
                candidateTeam = (isEnemyLp ? _state.TheirTeam : _state.MyTeam).ToList();
            }

            var lpCleanNames = new[] { NormalizeName(lp.RiotIdGameName), NormalizeName(lp.SummonerName), NormalizeName(lp.RiotId) }
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            // 1) 实名全等匹配
            PremadePlayerLike? targetPlayer = candidateTeam.FirstOrDefault(p =>
            {
                string rawPName = p.GameName ?? p.DisplayName ?? p.SummonerName ?? string.Empty;
                bool hasIdentity = !string.IsNullOrEmpty(p.Puuid) || !string.IsNullOrEmpty(p.TagLine) ||
                                   (p.SummonerId != 0 && p.SummonerId != p.CellId);
                if (!PlayerIdentity.IsRealSummonerName(rawPName, p.IsBot, hasIdentity)) return false;
                var pCleanNames = new[] { NormalizeName(p.GameName), NormalizeName(p.DisplayName), NormalizeName(p.SummonerName) }
                    .Where(s => !string.IsNullOrEmpty(s));
                return pCleanNames.Any(pn => lpCleanNames.Contains(pn));
            });

            // 2) 匹配已有相同 championId 的槽位
            if (targetPlayer is null)
            {
                targetPlayer = candidateTeam.FirstOrDefault(p =>
                {
                    string key = !string.IsNullOrEmpty(p.Puuid) ? p.Puuid : p.CellId.ToString();
                    if (matchedKeys.Contains(key)) return false;
                    return p.ChampionId == champId;
                });
            }

            // 3) 敌方脱敏槽位按序号回填
            string realName = lp.RiotIdGameName ?? lp.SummonerName ?? lp.RiotId ?? string.Empty;
            bool lpHasIdentity = !string.IsNullOrEmpty(lp.RiotIdTagLine) || realName.Contains('#');
            bool isRealName = PlayerIdentity.IsRealSummonerName(realName, lp.IsBot, lpHasIdentity);

            if (targetPlayer is null && isEnemyLp)
            {
                int lpIdx = enemyLiveClients.IndexOf(lp);
                if (lpIdx >= 0 && lpIdx < candidateTeam.Count)
                {
                    var slot = candidateTeam[lpIdx];
                    string key = !string.IsNullOrEmpty(slot.Puuid) ? slot.Puuid : slot.CellId.ToString();
                    if (!matchedKeys.Contains(key) && (slot.ChampionId <= 0 || slot.ChampionId == champId))
                    {
                        targetPlayer = slot;
                        if (isRealName && !string.IsNullOrEmpty(realName) && slot.DisplayName != realName)
                        {
                            slot = slot with { GameName = realName, DisplayName = realName };
                            candidateTeam[lpIdx] = slot;
                        }
                    }
                }
            }

            if (targetPlayer is null) continue;

            string targetKey = !string.IsNullOrEmpty(targetPlayer.Puuid) ? targetPlayer.Puuid : targetPlayer.CellId.ToString();
            matchedKeys.Add(targetKey);

            lock (_gate)
            {
                var dict = new Dictionary<string, GameInfoPlayer>(_state.PlayerData);
                string pdKey = !string.IsNullOrEmpty(targetPlayer.Puuid) ? targetPlayer.Puuid : targetPlayer.CellId.ToString();
                if (dict.TryGetValue(pdKey, out var existingPd))
                {
                    if (existingPd.ChampionId != champId)
                    {
                        dict[pdKey] = existingPd with { ChampionId = champId };
                        hasUpdated = true;
                    }
                }

                var myUpdated = isEnemyLp
                    ? _state.MyTeam
                    : _state.MyTeam.Select(tp => tp.CellId == targetPlayer.CellId ? tp with { ChampionId = champId } : tp).ToList();

                var theirUpdated = isEnemyLp
                    ? _state.TheirTeam.Select(tp => tp.CellId == targetPlayer.CellId ? tp with { ChampionId = champId } : tp).ToList()
                    : _state.TheirTeam;

                if (hasUpdated || targetPlayer.ChampionId != champId)
                {
                    _state = _state with
                    {
                        MyTeam = myUpdated,
                        TheirTeam = theirUpdated,
                        PlayerData = dict,
                    };
                    hasUpdated = true;
                }
            }

            // 实名回填与重新拉取战绩
            if (isRealName && !string.IsNullOrEmpty(realName) && string.IsNullOrEmpty(targetPlayer.Puuid))
            {
                bool shouldFetch;
                lock (_gate)
                {
                    shouldFetch = _queriedLiveClientNames.Add(realName);
                }

                if (shouldFetch)
                {
                    _ = FetchLiveClientRealSummonerAsync(realName, targetPlayer, cancellationToken);
                }
            }
        }

        if (hasUpdated)
        {
            lock (_gate)
            {
                _stateStream.Publish(_state);
            }
        }

        return hasUpdated;
    }

    private async Task FetchLiveClientRealSummonerAsync(
        string realName,
        PremadePlayerLike targetSlot,
        CancellationToken cancellationToken)
    {
        try
        {
            SummonerProfile? profile = null;
            if (realName.Contains('#'))
            {
                profile = await _summonerService.FindByNameAsync(realName, cancellationToken).ConfigureAwait(false);
            }

            if (profile is null)
            {
                string pureName = realName.Split('#')[0];
                profile = await _summonerService.FindByNameAsync(pureName, cancellationToken).ConfigureAwait(false);
            }

            if (profile is not null)
            {
                var updatedSlot = targetSlot with
                {
                    SummonerId = profile.SummonerId,
                    Puuid = profile.Puuid,
                    GameName = profile.GameName,
                    DisplayName = profile.DisplayName,
                    TagLine = profile.TagLine,
                    ProfileIconId = profile.ProfileIconId,
                };

                await LoadPlayerDataAsync(
                    targetSlot.CellId,
                    profile.SummonerId,
                    profile.Puuid,
                    updatedSlot,
                    cancellationToken).ConfigureAwait(false);

                WriteReservedSnapshot();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "LiveClient 回填实名拉取召唤师异常: {RealName}", realName);
            lock (_gate)
            {
                _queriedLiveClientNames.Remove(realName);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopGameEndedChain();
        CancelLiveClientPolling();

        _connStateSub?.Dispose();
        _connEventSub?.Dispose();
        _semaphore.Dispose();
        _extraMatchesGate.Dispose();
        _stateStream.Complete();
    }
}
