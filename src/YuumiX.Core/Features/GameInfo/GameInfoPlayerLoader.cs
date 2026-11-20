using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.SavedPlayers;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Parsers;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 玩家三路数据加载（战绩/排位/熟练度）、熟练度补填、宿命检测与路人集映射去重查询协作者。
/// </summary>
internal sealed class GameInfoPlayerLoader : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly IMatchService _matchService;
    private readonly ICareerService _careerService;
    private readonly IMasteryService _masteryService;
    private readonly IFateService _fateService;
    private readonly SavedPlayersStore? _savedPlayersStore;
    private readonly ILogger? _logger;
    private readonly GameInfoMatchExpander _matchExpander;
    private readonly GameInfoStateStore _stateStore;
    private readonly SemaphoreSlim _semaphore;

    private readonly Func<bool> _isDisposed;
    private readonly Action _writeReservedSnapshot;

    private readonly Dictionary<string, Task<GameInfoPlayer?>> _inFlightLoads = new();

    /// <summary>路人集映射 in-flight 去重条目（同 puuid 并发只发一次，对应旧版 querySavedPlayersMapDedup）。</summary>
    private (string Puuid, Task<IReadOnlyDictionary<string, SavedPlayerMarker>> Task)? _savedPlayersMapInFlight;

    /// <summary>路人集映射查询注入点（测试用；null 时走 SavedPlayersStore）。</summary>
    internal Func<string, CancellationToken, Task<IReadOnlyDictionary<string, SavedPlayerMarker>>>? SavedPlayersMapQueryOverride { get; set; }

    /// <summary>是否具备路人集映射查询能力（store 或测试注入点存在）。</summary>
    internal bool HasSavedPlayersMapSource => _savedPlayersStore is not null || SavedPlayersMapQueryOverride is not null;

    public GameInfoPlayerLoader(
        Func<ILcuTransport?> transportProvider,
        IMatchService matchService,
        ICareerService careerService,
        IMasteryService masteryService,
        IFateService fateService,
        SavedPlayersStore? savedPlayersStore,
        AppConfig config,
        ILogger? logger,
        GameInfoMatchExpander matchExpander,
        GameInfoStateStore stateStore,
        Func<bool> isDisposed,
        Action writeReservedSnapshot)
    {
        _transportProvider = transportProvider;
        _matchService = matchService;
        _careerService = careerService;
        _masteryService = masteryService;
        _fateService = fateService;
        _savedPlayersStore = savedPlayersStore;
        _logger = logger;
        _matchExpander = matchExpander;
        _stateStore = stateStore;
        _isDisposed = isDisposed;
        _writeReservedSnapshot = writeReservedSnapshot;

        int concurrency = Math.Clamp(config.Functions.ApiConcurrencyNumber, 1, 10);
        _semaphore = new SemaphoreSlim(concurrency, concurrency);
    }

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

            ClearSavedPlayersInFlight(completion.Task);
            completion.TrySetResult(map);
        }
        catch (OperationCanceledException)
        {
            ClearSavedPlayersInFlight(completion.Task);
            completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            ClearSavedPlayersInFlight(completion.Task);
            completion.TrySetException(ex);
        }
    }

    private void ClearSavedPlayersInFlight(Task<IReadOnlyDictionary<string, SavedPlayerMarker>> completedTask)
    {
        lock (_gate)
        {
            if (_savedPlayersMapInFlight is { } inflight &&
                ReferenceEquals(inflight.Task, completedTask))
            {
                _savedPlayersMapInFlight = null;
            }
        }
    }

    /// <summary>
    /// 触发路人集映射加载并发布到状态流（§4.8 触发时机：进入选人 / 连接变化 / 当前召唤师变化 / 恢复快照后）。
    /// </summary>
    internal void LoadSavedPlayerMap()
    {
        if (!HasSavedPlayersMapSource)
        {
            return;
        }

        string? puuid = _stateStore.CurrentState.CurrentSummonerPuuid;
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
            _stateStore.UpdateState(s => s with { SavedPlayerMap = map });
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

    internal async Task<GameInfoPlayer?> LoadPlayerDataAsync(
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
        var (capturedGameId, capturedEpoch) = _stateStore.CaptureGameContext();

        // 1. 尝试复用已加载的玩家数据
        GameInfoState state = _stateStore.CurrentState;
        GameInfoPlayer? existing = null;
        if (!string.IsNullOrEmpty(puuid)) state.PlayerData.TryGetValue(puuid, out existing);
        if (existing is null && summonerId != 0) state.PlayerData.TryGetValue(summonerId.ToString(), out existing);
        if (existing is null) state.PlayerData.TryGetValue(cellId.ToString(), out existing);

        if (existing is not null && !existing.Loading &&
            PlayerIdentity.IsIdentityCompatible(existing, puuid, summonerId, cellId))
        {
            if (fallback is { ChampionId: > 0 } && existing.ChampionId != fallback.ChampionId)
            {
                existing = existing with { ChampionId = fallback.ChampionId };
                _stateStore.SavePlayerToDictionary(cellId, summonerId, puuid, existing);
                _writeReservedSnapshot();
            }

            // 复用的条目缺少熟练度（如部分加载异常）时，后台静默补填
            bool reuseNeedsMastery = (existing.Masteries is not { Count: > 0 }) &&
                (!string.IsNullOrEmpty(puuid) || summonerId != 0);
            string? reusePuuid = puuid ?? existing.Info?.Puuid;
            long reuseSid = summonerId != 0 ? summonerId : (existing.Info?.SummonerId ?? 0);
            bool reuseIsMe = (reuseSid != 0 && reuseSid == state.CurrentSummonerId) ||
                (!string.IsNullOrEmpty(reusePuuid) && reusePuuid == state.CurrentSummonerPuuid);

            if (reuseNeedsMastery && (!string.IsNullOrEmpty(reusePuuid) || reuseSid != 0))
            {
                _ = BackfillMasteryAsync(existing, reusePuuid, reuseSid, reuseIsMe, CancellationToken.None);
            }

            // 复用的是当前玩家且非对局中：后台同步其最新战绩
            if (reuseIsMe && state.Phase is not (GamePhase.GameStart or GamePhase.InProgress))
            {
                _ = _matchExpander.SyncCurrentPlayerMatchesAsync();
            }

            return existing;
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

            if (!_stateStore.TrySavePlayerToDictionary(cellId, summonerId, puuid, botPlayer, capturedGameId, capturedEpoch))
            {
                return null;
            }

            _writeReservedSnapshot();
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

            GameInfoState currentState = _stateStore.CurrentState;
            bool isMe = profile.SummonerId == currentState.CurrentSummonerId ||
                        (!string.IsNullOrEmpty(currentState.CurrentSummonerPuuid) && profile.Puuid == currentState.CurrentSummonerPuuid);

            // 并行拉取战绩、排位、熟练度
            var matchTask = _matchService.GetMatchHistoryAsync(profile.Puuid, 0, 9, cancellationToken);
            var rankTask = _careerService.GetRankedStatsAsync(profile.Puuid, cancellationToken: cancellationToken);
            var masteryTask = _masteryService.GetPlayerMasteriesAsync(profile.Puuid, profile.SummonerId, isMe, cancellationToken);

            await Task.WhenAll(matchTask, rankTask, masteryTask).ConfigureAwait(false);

            MatchHistoryPage history = await matchTask.ConfigureAwait(false);
            RankedStats ranked = await rankTask.ConfigureAwait(false);
            IReadOnlyList<PlayerChampionMastery> masteries = await masteryTask.ConfigureAwait(false);

            // 无论 privacy 是否为 PRIVATE，以实际能否拉取到对局为准（国服即使设为私密通常也能拉取到战绩；只有当拉取被拒或返回空时才判定为隐藏）
            IReadOnlyList<MatchSummary> rawMatches = history.Games;
            bool isHidden = rawMatches.Count == 0;

            // GameInfoFilter 开启时按当前队列过滤首屏战绩
            List<MatchSummary> matches = _matchExpander.ApplyQueueFilter(rawMatches);

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

            if (!_stateStore.TrySavePlayerToDictionary(cellId, summonerId, profile.Puuid, player, capturedGameId, capturedEpoch))
            {
                _logger?.LogDebug("玩家数据晚到且对局上下文已切换，丢弃写回 cellId={CellId}, capturedGameId={GameId}", cellId, capturedGameId);
                return null;
            }

            _writeReservedSnapshot();

            // 异步触发宿命检测
            _ = TriggerFateDetectionAsync(player, matches, cancellationToken);

            // 第二阶段：战绩未隐藏时后台静默扩充第 11~50 局样本（仅刷新统计字段）
            if (!player.MatchHistoryHidden)
            {
                _ = _matchExpander.EnqueueExtraMatchesAsync(player, matches);
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

        var (capturedGameId, capturedEpoch) = _stateStore.CaptureGameContext();

        GameInfoState state = _stateStore.CurrentState;
        long currentSummonerId = state.CurrentSummonerId;
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
        if (HasSavedPlayersMapSource && !string.IsNullOrEmpty(state.CurrentSummonerPuuid))
        {
            try
            {
                IReadOnlyDictionary<string, SavedPlayerMarker> savedMap =
                    await QuerySavedPlayersMapAsync(state.CurrentSummonerPuuid, cancellationToken).ConfigureAwait(false);
                savedMap.TryGetValue(player.Info.Puuid, out savedMarker);
            }
            catch (Exception)
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

                if (_stateStore.TryApplyFateUpdate(
                        player.Info.Puuid,
                        player.Info.SummonerId,
                        capturedGameId,
                        capturedEpoch,
                        latest => latest with
                        {
                            FateFlag = fate.FateFlag,
                            RecentlyChampionName = fate.RecentlyChampionName,
                            FateGameCreation = gameCreation,
                            FateAllyCount = fate.AllyCount,
                            FateEnemyCount = fate.EnemyCount,
                            FateIsLastGame = isLastGame,
                        }))
                {
                    _writeReservedSnapshot();
                }
            }
        }
        catch (Exception)
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
            catch (Exception) { /* ignore */ }
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
            catch (Exception) { /* ignore */ }
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
            catch (Exception) { /* ignore */ }
        }

        return null;
    }

    /// <summary>对快照恢复后的玩家批量后台补拉缺失熟练度（每人间隔 100ms 防突发并发）。</summary>
    internal async Task BackfillMissingMasteriesAsync(IEnumerable<GameInfoPlayer> players)
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
            if (_isDisposed())
            {
                return;
            }

            GameInfoState state = _stateStore.CurrentState;
            await BackfillMasteryAsync(
                player,
                player.Info!.Puuid,
                player.Info.SummonerId,
                isMe: player.Info.SummonerId == state.CurrentSummonerId ||
                      player.Info.Puuid == state.CurrentSummonerPuuid,
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

            string key = !string.IsNullOrEmpty(puuid) ? puuid : (summonerId != 0 ? summonerId.ToString() : string.Empty);
            if (key.Length == 0)
            {
                return;
            }

            _stateStore.TryUpdatePlayerEntryByReferenceNoEpoch(
                key,
                player,
                current => current with { Masteries = masteries, MasteryFetched = true });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 静默降级，保持 MasteryFetched=false 以便后续重试
        }
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }
}
