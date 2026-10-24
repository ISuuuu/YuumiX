using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Common;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;
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
    private readonly AppConfig _config;
    private readonly ILogger<GameInfoService>? _logger;
    private readonly EventStream<GameInfoState> _stateStream;
    private readonly SemaphoreSlim _semaphore;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<GameInfoPlayer?>> _inFlightLoads = new();

    private readonly IDisposable? _connStateSub;
    private readonly IDisposable? _connEventSub;

    private GameInfoState _state = GameInfoState.Empty;
    private GameInfoState? _reservedState;
    private long _currentSessionRequestId;
    private bool _disposed;

    public GameInfoService(
        Func<ILcuTransport?> transportProvider,
        ISummonerService summonerService,
        IMatchService matchService,
        ICareerService careerService,
        IMasteryService masteryService,
        IFateService fateService,
        AppConfig config,
        LcuConnection? connection = null,
        ILogger<GameInfoService>? logger = null)
    {
        _transportProvider = transportProvider;
        _summonerService = summonerService;
        _matchService = matchService;
        _careerService = careerService;
        _masteryService = masteryService;
        _fateService = fateService;
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
            _state = GameInfoSampleFactory.CreateSample();
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

            if (teamOne.Count == 0 && teamTwo.Count == 0)
            {
                return;
            }

            TeamBuildContext ctx;
            lock (_gate)
            {
                ctx = new TeamBuildContext
                {
                    CurrentSummonerId = _state.CurrentSummonerId,
                    CurrentSummonerPuuid = _state.CurrentSummonerPuuid,
                    MapSide = _state.MapSide,
                    GameflowMyTeam = _state.MyTeam,
                    GameflowTheirTeam = _state.TheirTeam,
                    PlayerData = _state.PlayerData,
                };
            }

            var (allyTeam, enemyTeam, side) = TeamBuilder.ResolveTeamSide(teamOne, teamTwo, ctx);
            var (mappedMy, mappedTheir) = TeamBuilder.BuildGameflowTeams(allyTeam, enemyTeam, side, ctx);

            var newPlayerData = new Dictionary<string, GameInfoPlayer>(ctx.PlayerData);
            TeamBuilder.SeedInitialPlayerData(mappedMy.Concat(mappedTheir).ToList(), newPlayerData);

            var colorsMy = TeamBuilder.ResolvePremadeColors(mappedMy, _state.PremadeColorsMy);
            var colorsTheir = TeamBuilder.ResolvePremadeColors(mappedTheir, _state.PremadeColorsTheir);

            UpdateState(s => s with
            {
                MyTeam = mappedMy,
                TheirTeam = mappedTheir,
                MapSide = side,
                PlayerData = newPlayerData,
                PremadeColorsMy = colorsMy,
                PremadeColorsTheir = colorsTheir,
                ShouldShowContent = true,
                IsLoadingTeams = false,
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

                return existing;
            }
        }

        // 2. 电脑玩家判定（严格靠官方标记）
        bool isBot = fallback is not null && (fallback.Bot || fallback.IsBot || fallback.BotChampionId > 0);
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

            IReadOnlyList<MatchSummary> matches = history.Games;

            // 「对局信息过滤」开启时只保留与当前对局相同队列的战绩
            // （对照旧 useGamePlayerData.ts：filterEnabled && currentQueueId 非空 → 按 queueId 过滤）
            if (_config.Functions.GameInfoFilter && _state.CurrentQueueId != 0)
            {
                matches = matches.Where(m => m.QueueId == _state.CurrentQueueId).ToList();
            }

            if (matches.Count == 0 && profile.SummonerLevel > PlayerIdentity.NewPlayerMaxLevel)
            {
                isHidden = true;
            }

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

        var candidateGameIds = matches.Select(m => m.GameId).Distinct().ToList();
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
                lock (_gate)
                {
                    if (_state.PlayerData.TryGetValue(player.Info.Puuid, out var latest))
                    {
                        var updated = latest with
                        {
                            FateFlag = fate.FateFlag,
                            RecentlyChampionName = fate.RecentlyChampionName,
                            FateGameCreation = fate.GameCreation,
                            FateAllyCount = fate.AllyCount,
                            FateEnemyCount = fate.EnemyCount,
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

            var myTeam = (session.MyTeam ?? []).Select(m => new PremadePlayerLike
            {
                CellId = m.CellId,
                ChampionId = ChampionResolver.ResolvePlayerChampionId(
                    new PremadePlayerLike { CellId = m.CellId, Puuid = m.Puuid, SummonerId = m.SummonerId },
                    session),
                ChampionPickIntent = m.ChampionPickIntent,
                Puuid = m.Puuid,
                SummonerId = m.SummonerId,
                IsEnemy = false,
            }).ToList();

            var theirTeam = (session.TheirTeam ?? []).Select(m => new PremadePlayerLike
            {
                CellId = m.CellId,
                ChampionId = ChampionResolver.ResolvePlayerChampionId(
                    new PremadePlayerLike { CellId = m.CellId, Puuid = m.Puuid, SummonerId = m.SummonerId, IsEnemy = true },
                    session),
                ChampionPickIntent = m.ChampionPickIntent,
                Puuid = m.Puuid,
                SummonerId = m.SummonerId,
                IsEnemy = true,
            }).ToList();

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
            });

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

        return new ChampSelectSessionLike(actionsList, myTeamList, theirTeamList);
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
                list.Add(new ChampSelectMemberLike(cellId, champId, pickIntent, puuid, sid));
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
                bool bot = JsonRead.Bool(p, "bot", false) || JsonRead.Bool(p, "isBot", false);
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
        lock (_gate)
        {
            if (_config.Functions.EnableReserveGameinfo)
            {
                if (_state.PlayerData.Count > 0)
                {
                    _reservedState = _state;
                }

                _state = _state with
                {
                    Phase = phase,
                    ShouldShowContent = _reservedState is not null && _reservedState.PlayerData.Count > 0,
                };
            }
            else
            {
                _reservedState = null;
                _state = GameInfoState.Empty with { Phase = phase };
            }

            _stateStream.Publish(_state);
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
            UpdateState(s => s with
            {
                CurrentSummonerId = current.SummonerId,
                CurrentSummonerPuuid = current.Puuid,
            });
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _connStateSub?.Dispose();
        _connEventSub?.Dispose();
        _semaphore.Dispose();
        _stateStream.Complete();
    }
}
