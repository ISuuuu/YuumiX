using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Automation.Models;
using YuumiX.Core.Lcu;
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
    private readonly ILogger<AutoBanPickAgent>? _logger;

    private readonly object _stateLock = new();
    private bool _isRunning;
    private long _lastEpoch;
    private bool _isSummonerSpellSet;
    private bool _isChampionPickedCompleted;
    private int? _lastPresetActionId;
    private int _lastPresetChampionId;

    public string Name => "AutoBanPickAgent";
    public bool IsRunning => _isRunning;

    public AutoBanPickAgent(
        Func<ILcuTransport?> transportProvider,
        Func<AppConfig> configProvider,
        AgentSupervisor supervisor,
        AgentLifecycle lifecycle,
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
        _isSummonerSpellSet = false;
        _isChampionPickedCompleted = false;
        _lastPresetActionId = null;
        _lastPresetChampionId = 0;
    }

    /// <summary>
    /// 处理 /lol-champ-select/v1/session WebSocket 事件。
    /// </summary>
    public async Task HandleSessionEventAsync(ChampSelectSession session)
    {
        if (!_isRunning) return;
        ArgumentNullException.ThrowIfNull(session);

        var snap = _lifecycle.Snapshot();
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
        }

        var cfg = _configProvider().Functions;

        // 执行顺序：亮英雄/预选 → 禁人 → 技能 → 交换 → 交易 → 锁定
        await DoAutoPresetChampionAsync(session, cfg).ConfigureAwait(false);
        await DoAutoBanAsync(session, cfg).ConfigureAwait(false);
        await DoAutoSpellAsync(session, cfg).ConfigureAwait(false);
        await DoAutoSwapAsync(session, cfg).ConfigureAwait(false);
        await DoAutoTradeAsync(session, cfg).ConfigureAwait(false);
        await DoAutoCompleteAsync(session, cfg).ConfigureAwait(false);
    }

    // ─── 1. 自动预选/亮英雄 ───
    private async Task DoAutoPresetChampionAsync(ChampSelectSession session, FunctionsConfig cfg)
    {
        if (!cfg.EnableAutoHoverChampion && !cfg.EnableAutoSelectChampion) return;
        if (session.BenchEnabled) return;

        int cellId = session.LocalPlayerCellId;
        var player = session.MyTeam.FirstOrDefault(p => p.CellId == cellId);
        if (player is null) return;

        if (player.ChampionId != 0 || player.ChampionPickIntent != 0) return;

        string pos = player.AssignedPosition;
        var allBans = new HashSet<int>(session.Bans.MyTeamBans.Concat(session.Bans.TheirTeamBans));

        var candidates = GetPositionSelectCandidates(pos, cfg);
        candidates.AddRange(cfg.AutoSelectChampion);
        candidates = candidates.Distinct().Where(c => !allBans.Contains(c)).ToList();

        if (cfg.EnableAutoBanChampion)
        {
            var banCandidates = GetPositionBanCandidates(pos, cfg);
            banCandidates.AddRange(cfg.AutoBanChampion);
            candidates = candidates.Where(c => !banCandidates.Contains(c)).ToList();
        }

        if (candidates.Count == 0) return;
        int championId = candidates[0];

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
            if (_lastPresetActionId == pickAction.Id && _lastPresetChampionId == championId)
            {
                return;
            }
            _lastPresetActionId = pickAction.Id;
            _lastPresetChampionId = championId;
        }

        _logger?.LogDebug("预选亮出英雄: ChampionId={ChampId}, ActionId={ActionId}", championId, pickAction.Id);
        await PatchActionAsync(pickAction.Id, championId, completed: false).ConfigureAwait(false);
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

        var allBans = new HashSet<int>(session.Bans.MyTeamBans.Concat(session.Bans.TheirTeamBans));
        candidates = candidates.Distinct().Where(c => !allBans.Contains(c)).ToList();

        if (cfg.PretendBan)
        {
            var intents = new HashSet<int>(session.MyTeam
                .Select(p => p.ChampionPickIntent)
                .Where(id => id > 0));
            candidates = candidates.Where(c => !intents.Contains(c)).ToList();
        }

        if (candidates.Count == 0) return;

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

            var newBans = new HashSet<int>(freshSession.Bans.MyTeamBans.Concat(freshSession.Bans.TheirTeamBans));
            int championId = candidates.FirstOrDefault(c => !newBans.Contains(c));
            if (championId == 0) return;

            _logger?.LogInformation("自动禁用英雄: {ChampionId} (actionId={ActionId})", championId, actionId);
            await PatchActionAsync(actionId, championId, completed: true, ct).ConfigureAwait(false);
        });
    }

    // ─── 3. 自动召唤师技能 ───
    private async Task DoAutoSpellAsync(ChampSelectSession session, FunctionsConfig cfg)
    {
        lock (_stateLock)
        {
            if (_isSummonerSpellSet) return;
        }

        if (!cfg.EnableAutoSetSpells)
        {
            lock (_stateLock) _isSummonerSpellSet = true;
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
            lock (_stateLock) _isSummonerSpellSet = true;
            return;
        }

        var transport = _transportProvider();
        if (transport is null) return;

        var body = JsonSerializer.Serialize(new { spell1Id = spells[0], spell2Id = spells[1] });
        var resp = await transport.SendAsync(
            LcuRequest.Patch("/lol-champ-select/v1/session/my-selection", body)).ConfigureAwait(false);

        lock (_stateLock)
        {
            _isSummonerSpellSet = true;
        }

        if (resp.IsSuccess)
        {
            _logger?.LogInformation("自动配置召唤师技能成功: {S1} / {S2}", spells[0], spells[1]);
        }
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
                        cantSelect.Add(a.ChampionId);
                    }
                }
            }
            foreach (var b in freshSession.Bans.MyTeamBans) cantSelect.Add(b);
            foreach (var b in freshSession.Bans.TheirTeamBans) cantSelect.Add(b);

            int targetChampId = freshSession.Actions
                .SelectMany(g => g)
                .FirstOrDefault(a => a.Id == lease.ActionId)?.ChampionId ?? 0;

            if (targetChampId == 0) targetChampId = lease.ExpectedChampionId;
            if (targetChampId == 0 || cantSelect.Contains(targetChampId)) return;

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

    private async Task PatchActionAsync(int actionId, int championId, bool completed, CancellationToken ct = default)
    {
        var transport = _transportProvider();
        if (transport is null) return;

        var body = JsonSerializer.Serialize(new
        {
            championId,
            completed,
        });

        await transport.SendAsync(
            LcuRequest.Patch($"/lol-champ-select/v1/session/actions/{actionId}", body),
            ct).ConfigureAwait(false);
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
