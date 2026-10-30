using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Automation.Models;
using YuumiX.Core.Features.Opgg;
using YuumiX.Core.Features.Opgg.Models;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Lcu;
using YuumiX.Core.Parsers;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Automation;

/// <summary>
/// 英雄选择全流程自动化代理（对应旧版 agents/auto_bp.rs）。
/// 处理选人预选亮起、自动禁用（含防撞车假禁用）、召唤师技能配置、楼层/英雄交换与超时确认锁定。
/// </summary>
public sealed class AutoBanPickAgent : IAutomationAgent
{
    private const ulong LockBufferMs = 5000;
    private const ulong DefaultPhaseLeftMs = 10000;
    private const ulong MaxPhaseWaitMs = 180000;

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Func<AppConfig> _configProvider;
    private readonly AgentSupervisor _supervisor;
    private readonly AgentLifecycle _lifecycle;
    private readonly IOpggService? _opggService;
    private readonly ILogger<AutoBanPickAgent>? _logger;

    private readonly object _stateLock = new();
    private bool _isRunning;
    private long _lastEpoch;
    private bool _isSummonerSpellSet;
    private bool _isSummonerSpellSetting;
    private bool _isChampionPickedCompleted;
    private bool _isPresetting;
    private long _selectionGeneration;
    private int? _lastPresetActionId;
    private int _lastPresetChampionId;
    private int _lastOpggChampionId;

    public string Name => "AutoBanPickAgent";
    public bool IsRunning => _isRunning;

    public AutoBanPickAgent(
        Func<ILcuTransport?> transportProvider,
        Func<AppConfig> configProvider,
        AgentSupervisor supervisor,
        AgentLifecycle lifecycle,
        IOpggService? opggService = null,
        ILogger<AutoBanPickAgent>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(lifecycle);

        _transportProvider = transportProvider;
        _configProvider = configProvider;
        _supervisor = supervisor;
        _lifecycle = lifecycle;
        _opggService = opggService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _isRunning = true;
            ResetSelectionTracking();
        }

        _logger?.LogInformation("自动选人/禁用代理已启动");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _isRunning = false;
            ResetSelectionTracking();
        }

        _supervisor.CancelKind(TaskKind.AutoPickLock);
        _supervisor.CancelKind(TaskKind.AutoBanLock);
        _supervisor.CancelKind(TaskKind.AutoSwap);
        _supervisor.CancelKind(TaskKind.AutoTrade);
        _supervisor.CancelKind(TaskKind.AutoSpell);

        _logger?.LogInformation("自动选人/禁用代理已停止");
        return Task.CompletedTask;
    }

    private void ResetSelectionTracking()
    {
        _selectionGeneration++;
        _isSummonerSpellSet = false;
        _isSummonerSpellSetting = false;
        _isChampionPickedCompleted = false;
        _isPresetting = false;
        _lastPresetActionId = null;
        _lastPresetChampionId = 0;
        _lastOpggChampionId = 0;
    }

    /// <summary>
    /// 处理 /lol-champ-select/v1/session WebSocket 事件。
    /// </summary>
    public async Task HandleSessionEventAsync(ChampSelectSession session)
    {
        if (!_isRunning) return;
        ArgumentNullException.ThrowIfNull(session);

        var snap = _lifecycle.Snapshot();
        long generation;
        lock (_stateLock)
        {
            if (snap.MatchEpoch != _lastEpoch || snap.Phase != GamePhase.ChampSelect)
            {
                _lastEpoch = snap.MatchEpoch;
                ResetSelectionTracking();
            }

            if (session.LocalPlayerCellId == -1)
            {
                _logger?.LogDebug("收到会话重置信号 (LocalPlayerCellId=-1)");
                ResetSelectionTracking();
                _supervisor.CancelKind(TaskKind.AutoPickLock);
                _supervisor.CancelKind(TaskKind.AutoBanLock);
                _supervisor.CancelKind(TaskKind.AutoSwap);
                _supervisor.CancelKind(TaskKind.AutoTrade);
                return;
            }
            generation = _selectionGeneration;
        }

        var cfg = _configProvider().Functions;

        // 执行顺序：亮英雄/预选 → 禁人 → 技能 → 交换 → 交易 → 锁定 → OP.GG 联动
        await DoAutoPresetChampionAsync(session, cfg, generation).ConfigureAwait(false);
        await DoAutoBanAsync(session, cfg).ConfigureAwait(false);
        await DoAutoSpellAsync(session, cfg, generation).ConfigureAwait(false);
        await DoAutoSwapAsync(session, cfg).ConfigureAwait(false);
        await DoAutoTradeAsync(session, cfg).ConfigureAwait(false);
        await DoAutoCompleteAsync(session, cfg).ConfigureAwait(false);
        await DoShowOpggBuildAsync(session, cfg).ConfigureAwait(false);
    }

    // ─── 1. 自动预选/亮英雄 ───
    private async Task DoAutoPresetChampionAsync(ChampSelectSession session, FunctionsConfig cfg, long generation)
    {
        if (!cfg.EnableAutoHoverChampion && !cfg.EnableAutoSelectChampion) return;
        if (session.BenchEnabled) return;

        int cellId = session.LocalPlayerCellId;
        var player = session.MyTeam.FirstOrDefault(p => p.CellId == cellId);
        if (player is null) return;

        if (player.ChampionId != 0 || player.ChampionPickIntent != 0) return;

        string pos = player.AssignedPosition;
        // 会话里的禁选 id 可能是经典模式形态（60000+原 id），一律折回当前版本再比较，
        // 否则「已禁英雄」过滤会失效（同一英雄两种 id 互不相等）。
        var allBans = new HashSet<int>(session.Bans.MyTeamBans
            .Concat(session.Bans.TheirTeamBans)
            .Select(ChampionVariants.ToCurrent));

        var candidates = GetPositionSelectCandidates(pos, cfg);
        candidates.AddRange(cfg.AutoSelectChampion);
        candidates = candidates.Select(ChampionVariants.ToCurrent).Distinct().Where(c => !allBans.Contains(c)).ToList();

        if (cfg.EnableAutoBanChampion)
        {
            var banCandidates = GetPositionBanCandidates(pos, cfg);
            banCandidates.AddRange(cfg.AutoBanChampion);
            candidates = candidates.Where(c => !banCandidates.Contains(c)).ToList();
        }

        if (candidates.Count == 0) return;

        // 配置里存的是当前版本 id；经典模式会话要换成经典形态的 id 才认。
        int championId = ChampionVariants.Resolve(candidates[0], IsClassicSession(session));

        // 寻找 pick action
        ChampSelectAction? pickAction = null;
        for (int i = session.Actions.Count - 1; i >= 0; i--)
        {
            var group = session.Actions[i];
            pickAction = group.FirstOrDefault(a => a.ActorCellId == cellId && string.Equals(a.ActionType, "pick", StringComparison.OrdinalIgnoreCase));
            if (pickAction is not null) break;
        }

        if (pickAction is null) return;
        if (!cfg.EnableAutoHoverChampion && !pickAction.IsInProgress) return;

        lock (_stateLock)
        {
            if (generation != _selectionGeneration || _isPresetting ||
                (_lastPresetActionId == pickAction.Id && _lastPresetChampionId == championId))
            {
                return;
            }
            _isPresetting = true;
        }

        _logger?.LogDebug("预选亮出英雄: ChampionId={ChampId}, ActionId={ActionId}", championId, pickAction.Id);
        bool succeeded = false;
        try
        {
            succeeded = await PatchActionAsync(pickAction.Id, championId, completed: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "自动预选英雄失败");
        }
        finally
        {
            lock (_stateLock)
            {
                if (generation == _selectionGeneration)
                {
                    _isPresetting = false;
                    if (succeeded)
                    {
                        _lastPresetActionId = pickAction.Id;
                        _lastPresetChampionId = championId;
                    }
                }
            }
        }
    }

    // ─── 2. 自动禁用 ───
    private async Task DoAutoBanAsync(ChampSelectSession session, FunctionsConfig cfg)
    {
        if (!cfg.EnableAutoBanChampion) return;

        int cellId = session.LocalPlayerCellId;
        ChampSelectAction? banAction = null;
        foreach (var group in session.Actions)
        {
            banAction = group.FirstOrDefault(a => a.ActorCellId == cellId
                && string.Equals(a.ActionType, "ban", StringComparison.OrdinalIgnoreCase)
                && a.IsInProgress
                && !a.Completed);
            if (banAction is not null) break;
        }

        if (banAction is null) return;

        string pos = session.MyTeam.FirstOrDefault(p => p.CellId == cellId)?.AssignedPosition ?? "";
        var candidates = GetPositionBanCandidates(pos, cfg);
        candidates.AddRange(cfg.AutoBanChampion);
        candidates = candidates.Select(ChampionVariants.ToCurrent).Distinct().ToList();

        var allBans = new HashSet<int>(session.Bans.MyTeamBans
            .Concat(session.Bans.TheirTeamBans)
            .Select(ChampionVariants.ToCurrent));
        candidates = candidates.Where(c => !allBans.Contains(c)).ToList();

        if (cfg.PretendBan)
        {
            var intents = new HashSet<int>(session.MyTeam
                .Select(p => p.ChampionPickIntent)
                .Where(id => id > 0)
                .Select(ChampionVariants.ToCurrent));
            candidates = candidates.Where(c => !intents.Contains(c)).ToList();
        }

        if (candidates.Count == 0) return;

        bool classicSession = IsClassicSession(session);

        int actionId = banAction.Id;
        var currentLifecycle = _lifecycle.Snapshot();
        if (currentLifecycle.SessionId is null) return;

        var guard = _lifecycle.CaptureGuard(requireSession: true);
        var lease = new ActionLease(guard, actionId, cellId, "ban", candidates[0]);
        double delaySecs = cfg.AutoBanDelay;

        string desc = $"AutoBanLock(action={actionId}, cell={cellId})";
        _ = _supervisor.TrySpawnAsync(TaskKind.AutoBanLock, desc, async ct =>
        {
            if (delaySecs > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySecs), ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            var freshSession = await GetFreshSessionAsync(ct).ConfigureAwait(false);
            if (freshSession is null) return;

            var freshLifecycle = _lifecycle.Snapshot();
            lease.VerifyTarget(freshLifecycle, freshSession);

            var newBans = new HashSet<int>(freshSession.Bans.MyTeamBans
                .Concat(freshSession.Bans.TheirTeamBans)
                .Select(ChampionVariants.ToCurrent));
            int championId = candidates.FirstOrDefault(c => !newBans.Contains(c));
            if (championId == 0) return;

            // 候选是当前版本 id，发请求前换成会话形态（经典模式下是 60000+原 id）。
            championId = ChampionVariants.Resolve(championId, classicSession);

            _logger?.LogInformation("自动禁用英雄: {ChampionId} (actionId={ActionId})", championId, actionId);
            await PatchActionAsync(actionId, championId, completed: true, ct).ConfigureAwait(false);
        });
    }

    // ─── 3. 自动召唤师技能 ───
    private async Task DoAutoSpellAsync(ChampSelectSession session, FunctionsConfig cfg, long generation)
    {
        lock (_stateLock)
        {
            if (generation != _selectionGeneration || _isSummonerSpellSet || _isSummonerSpellSetting) return;
        }

        if (!cfg.EnableAutoSetSpells)
        {
            lock (_stateLock)
            {
                if (generation == _selectionGeneration) _isSummonerSpellSet = true;
            }
            return;
        }

        int cellId = session.LocalPlayerCellId;
        var player = session.MyTeam.FirstOrDefault(p => p.CellId == cellId);
        if (player is null) return;

        string pos = player.AssignedPosition;
        var spells = GetPositionSpellCandidates(pos, cfg);
        if (spells.Count < 2 || spells.Contains(54) || spells.All(s => s == 0))
        {
            spells = [.. cfg.AutoSetSummonerSpell];
        }

        if (spells.Count < 2 || spells.Contains(54) || spells[0] == 0 || spells[1] == 0)
        {
            lock (_stateLock)
            {
                if (generation == _selectionGeneration) _isSummonerSpellSet = true;
            }
            return;
        }

        var transport = _transportProvider();
        if (transport is null) return;

        lock (_stateLock)
        {
            if (generation != _selectionGeneration || _isSummonerSpellSet || _isSummonerSpellSetting) return;
            _isSummonerSpellSetting = true;
        }

        var body = JsonSerializer.Serialize(new { spell1Id = spells[0], spell2Id = spells[1] });
        bool succeeded = false;
        try
        {
            var resp = await transport.SendAsync(
                LcuRequest.Patch("/lol-champ-select/v1/session/my-selection", body)).ConfigureAwait(false);
            succeeded = resp.IsSuccess;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "自动配置召唤师技能失败");
        }
        finally
        {
            lock (_stateLock)
            {
                if (generation == _selectionGeneration)
                {
                    _isSummonerSpellSetting = false;
                    _isSummonerSpellSet = succeeded;
                }
            }
        }

        if (succeeded) _logger?.LogInformation("自动配置召唤师技能成功: {S1} / {S2}", spells[0], spells[1]);
    }

    // ─── 4. 自动接受楼层交换 ───
    private async Task DoAutoSwapAsync(ChampSelectSession session, FunctionsConfig cfg)
    {
        if (!cfg.AutoAcceptCeilSwap) return;

        foreach (var swap in session.PickOrderSwaps)
        {
            if (string.Equals(swap.State, "RECEIVED", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogInformation("自动接受楼层交换请求: swapId={SwapId}", swap.Id);
                var transport = _transportProvider();
                if (transport is not null)
                {
                    await transport.SendAsync(
                        LcuRequest.Post($"/lol-champ-select/v1/session/swaps/pick-order/{swap.Id}/accept", "{}")).ConfigureAwait(false);
                }
                break;
            }
        }
    }

    // ─── 5. 自动接受英雄交换 ───
    private async Task DoAutoTradeAsync(ChampSelectSession session, FunctionsConfig cfg)
    {
        if (!cfg.AutoAcceptChampTrade) return;

        foreach (var trade in session.Trades)
        {
            if (string.Equals(trade.State, "RECEIVED", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogInformation("自动接受英雄交换请求: tradeId={TradeId}", trade.Id);
                var transport = _transportProvider();
                if (transport is not null)
                {
                    await transport.SendAsync(
                        LcuRequest.Post($"/lol-champ-select/v1/session/trades/{trade.Id}/accept", "{}")).ConfigureAwait(false);
                }
                break;
            }
        }
    }

    // ─── 6. 超时前自动锁定 ───
    private async Task DoAutoCompleteAsync(ChampSelectSession session, FunctionsConfig cfg)
    {
        lock (_stateLock)
        {
            if (!cfg.AutoSelectConfirmOnTimeout || _isChampionPickedCompleted) return;
        }

        int cellId = session.LocalPlayerCellId;
        ChampSelectAction? pickAction = null;
        foreach (var group in session.Actions)
        {
            pickAction = group.FirstOrDefault(a => a.ActorCellId == cellId
                && string.Equals(a.ActionType, "pick", StringComparison.OrdinalIgnoreCase)
                && a.IsInProgress
                && !a.Completed);
            if (pickAction is not null) break;
        }

        if (pickAction is null) return;

        var lifecycleState = _lifecycle.Snapshot();
        if (lifecycleState.SessionId is null) return;

        var guard = _lifecycle.CaptureGuard(requireSession: true);
        ulong sleepMs = ComputeLockDelayMs(session.Timer);

        int initialChampId = pickAction.ChampionId;
        if (initialChampId == 0)
        {
            var p = session.MyTeam.FirstOrDefault(p => p.CellId == cellId);
            if (p is not null)
            {
                initialChampId = p.ChampionPickIntent > 0 ? p.ChampionPickIntent : p.ChampionId;
            }
        }

        var lease = new ActionLease(guard, pickAction.Id, cellId, "pick", initialChampId);
        string desc = $"AutoPickLock(action={pickAction.Id}, cell={cellId})";

        var (spawned, _) = await _supervisor.TrySpawnAsync(TaskKind.AutoPickLock, desc, async ct =>
        {
            if (sleepMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(sleepMs), ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            var freshSession = await GetFreshSessionAsync(ct).ConfigureAwait(false);
            if (freshSession is null) return;

            var currentLifecycle = _lifecycle.Snapshot();
            lease.VerifyTarget(currentLifecycle, freshSession);

            var cantSelect = new HashSet<int>();
            foreach (var g in freshSession.Actions)
            {
                foreach (var a in g)
                {
                    if (string.Equals(a.ActionType, "pick", StringComparison.OrdinalIgnoreCase)
                        && a.Completed
                        && a.ActorCellId != cellId)
                    {
                        cantSelect.Add(ChampionVariants.ToCurrent(a.ChampionId));
                    }
                }
            }
            foreach (var b in freshSession.Bans.MyTeamBans) cantSelect.Add(ChampionVariants.ToCurrent(b));
            foreach (var b in freshSession.Bans.TheirTeamBans) cantSelect.Add(ChampionVariants.ToCurrent(b));

            int targetChampId = freshSession.Actions
                .SelectMany(g => g)
                .FirstOrDefault(a => a.Id == lease.ActionId)?.ChampionId ?? 0;

            if (targetChampId == 0) targetChampId = lease.ExpectedChampionId;
            if (targetChampId == 0 || cantSelect.Contains(ChampionVariants.ToCurrent(targetChampId))) return;

            _logger?.LogInformation("超时自动锁定英雄: {ChampionId}", targetChampId);
            await PatchActionAsync(lease.ActionId, targetChampId, completed: true, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (spawned)
        {
            lock (_stateLock)
            {
                _isChampionPickedCompleted = true;
            }
        }
    }

    // ─── 7. 选人锁定后自动触发 OP.GG 出装联动 ───
    private Task DoShowOpggBuildAsync(ChampSelectSession session, FunctionsConfig cfg)
    {
        if (_opggService is null) return Task.CompletedTask;

        int cellId = session.LocalPlayerCellId;

        // 检查当前玩家的 pick action 是否已完成
        foreach (var group in session.Actions)
        {
            foreach (var a in group)
            {
                if (a.ActorCellId == cellId &&
                    string.Equals(a.ActionType, "pick", StringComparison.OrdinalIgnoreCase) &&
                    !a.Completed)
                {
                    return Task.CompletedTask;
                }
            }
        }

        var player = session.MyTeam.FirstOrDefault(p => p.CellId == cellId);
        if (player is null) return Task.CompletedTask;

        int championId = player.ChampionId != 0 ? player.ChampionId : player.ChampionPickIntent;
        if (championId == 0) return Task.CompletedTask;

        // OP.GG 的数据只按当前版本英雄组织，经典模式会话里拿到的是 60000+原 id，先折回来。
        championId = ChampionVariants.ToCurrent(championId);

        lock (_stateLock)
        {
            if (championId == _lastOpggChampionId)
            {
                return Task.CompletedTask;
            }
            _lastOpggChampionId = championId;
        }

        string opggPosition = OpggPosition.FromAssignedPosition(player.AssignedPosition);

        string mode = session.QueueId.HasValue
            ? MatchQueueCatalog.ToOpggMode(session.QueueId.Value)
            : (session.BenchEnabled ? OpggMode.Aram : (session.MyTeam.Count == 2 ? OpggMode.Arena : OpggMode.Ranked));

        _logger?.LogInformation("OP.GG 选人锁定联动事件触发: ChampionId={ChampId}, Mode={Mode}, Pos={Pos}",
            championId, mode, opggPosition);

        _opggService.PublishChampSelectBuild(new OpggBuildEvent(championId, mode, opggPosition));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 本次选人是否为经典模式会话。
    /// <para>
    /// 主判据是队列 id（4300 / 4310，见 <c>MatchQueueCatalog</c>）；兜底看会话里是否已经出现
    /// 经典形态的英雄 id（禁选列表、队友已选与预选）—— 经典模式的会话内 id 是 60000+原 id，
    /// 出现即证明形态，不再依赖队列 id 是否配置齐全。
    /// </para>
    /// </summary>
    private static bool IsClassicSession(ChampSelectSession session)
    {
        if (session.QueueId is 4300 or 4310)
        {
            return true;
        }

        return session.Bans.MyTeamBans
            .Concat(session.Bans.TheirTeamBans)
            .Concat(session.MyTeam.Select(p => p.ChampionId))
            .Concat(session.MyTeam.Select(p => p.ChampionPickIntent))
            .Any(ChampionVariants.IsClassic);
    }

    private static ulong ComputeLockDelayMs(ChampSelectTimer? timer)
    {
        if (timer is null)
        {
            return DefaultPhaseLeftMs > LockBufferMs ? DefaultPhaseLeftMs - LockBufferMs : 0;
        }

        ulong nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ulong elapsedMs = timer.InternalNowInEpochMs is { } lcuNow && nowMs > lcuNow
            ? nowMs - lcuNow
            : 0;

        ulong remaining = timer.AdjustedTimeLeftInPhase ?? DefaultPhaseLeftMs;
        if (remaining > MaxPhaseWaitMs) remaining = MaxPhaseWaitMs;
        remaining = remaining > elapsedMs ? remaining - elapsedMs : 0;

        return remaining > LockBufferMs ? remaining - LockBufferMs : 0;
    }

    private async Task<ChampSelectSession?> GetFreshSessionAsync(CancellationToken cancellationToken)
    {
        var transport = _transportProvider();
        if (transport is null) return null;

        var resp = await transport.SendAsync(
            LcuRequest.Get("/lol-champ-select/v1/session"),
            cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccess) return null;

        try
        {
            return JsonSerializer.Deserialize<ChampSelectSession>(resp.Body);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "解析最新选人会话 JSON 异常");
            return null;
        }
    }

    private async Task<bool> PatchActionAsync(int actionId, int championId, bool completed, CancellationToken ct = default)
    {
        var transport = _transportProvider();
        if (transport is null) return false;

        var body = JsonSerializer.Serialize(new
        {
            championId,
            completed,
        });

        var response = await transport.SendAsync(
            LcuRequest.Patch($"/lol-champ-select/v1/session/actions/{actionId}", body),
            ct).ConfigureAwait(false);
        return response.IsSuccess;
    }

    private static List<int> GetPositionSelectCandidates(string pos, FunctionsConfig cfg) => pos.ToLowerInvariant() switch
    {
        "top" => [.. cfg.AutoSelectChampionTop],
        "jungle" => [.. cfg.AutoSelectChampionJug],
        "middle" => [.. cfg.AutoSelectChampionMid],
        "bottom" => [.. cfg.AutoSelectChampionBot],
        "utility" => [.. cfg.AutoSelectChampionSup],
        _ => [],
    };

    private static List<int> GetPositionBanCandidates(string pos, FunctionsConfig cfg) => pos.ToLowerInvariant() switch
    {
        "top" => [.. cfg.AutoBanChampionTop],
        "jungle" => [.. cfg.AutoBanChampionJug],
        "middle" => [.. cfg.AutoBanChampionMid],
        "bottom" => [.. cfg.AutoBanChampionBot],
        "utility" => [.. cfg.AutoBanChampionSup],
        _ => [],
    };

    private static List<int> GetPositionSpellCandidates(string pos, FunctionsConfig cfg) => pos.ToLowerInvariant() switch
    {
        "top" => [.. cfg.AutoSetSummonerSpellTop],
        "jungle" => [.. cfg.AutoSetSummonerSpellJug],
        "middle" => [.. cfg.AutoSetSummonerSpellMid],
        "bottom" => [.. cfg.AutoSetSummonerSpellBot],
        "utility" => [.. cfg.AutoSetSummonerSpellSup],
        _ => [],
    };

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }
}
