using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Automation;

/// <summary>
/// 掉线自动重连代理（对应旧版 agents/auto_match.rs::spawn_auto_reconnect）。
/// 检测到游戏进程掉线回到 Reconnect 阶段后，自动以指数退避向客户端发起重连请求。
/// </summary>
public sealed class AutoReconnectAgent : IAutomationAgent
{
    private const int MaxAttempts = 5;

    /// <summary>
    /// 游戏进程两次采样的间隔。Alt+F4 之后 Windows 回收进程可能滞后数百毫秒，
    /// 单次采样有机会读到残留进程，把「主动退出」误判成「仍在对局中」。
    /// </summary>
    private static readonly TimeSpan GameProcessProbeInterval = TimeSpan.FromMilliseconds(300);

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Func<AppConfig> _configProvider;
    private readonly Func<bool> _gameProcessAliveProbe;
    private readonly AgentSupervisor _supervisor;
    private readonly AgentLifecycle _lifecycle;
    private readonly ILogger<AutoReconnectAgent>? _logger;

    private readonly object _stateLock = new();
    private bool _isRunning;
    private GamePhase _previousPhase = GamePhase.None;

    public string Name => "AutoReconnectAgent";
    public bool IsRunning => _isRunning;

    public AutoReconnectAgent(
        Func<ILcuTransport?> transportProvider,
        Func<AppConfig> configProvider,
        AgentSupervisor supervisor,
        AgentLifecycle lifecycle,
        ILogger<AutoReconnectAgent>? logger = null,
        Func<bool>? gameProcessAliveProbe = null)
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
        _gameProcessAliveProbe = gameProcessAliveProbe ?? GameProcessProbe.IsRunning;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _isRunning = true;
        }

        _logger?.LogInformation("自动重连代理已启动");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _isRunning = false;
        }

        _supervisor.CancelKind(TaskKind.AutoReconnect);
        _logger?.LogInformation("自动重连代理已停止");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 处理游戏阶段变化。
    /// </summary>
    public void OnPhaseChanged(GamePhase currentPhase)
    {
        if (!_isRunning) return;

        GamePhase prev;
        lock (_stateLock)
        {
            prev = _previousPhase;
            _previousPhase = currentPhase;
        }

        if (currentPhase != GamePhase.Reconnect)
        {
            _supervisor.CancelKind(TaskKind.AutoReconnect);
            return;
        }

        var cfg = _configProvider().Functions;
        if (!cfg.EnableAutoReconnect) return;

        // 如果前一阶段已经是结算相关阶段或空闲阶段，说明对局已结束，绝不触发重连
        if (prev is GamePhase.WaitingForStats
            or GamePhase.PreEndOfGame
            or GamePhase.EndOfGame
            or GamePhase.Lobby
            or GamePhase.None)
        {
            _logger?.LogInformation("前一阶段为 {PrevPhase}，对局已结算/脱离对局，忽略断线重连触发", prev);
            return;
        }

        TriggerAutoReconnect();
    }

    private void TriggerAutoReconnect()
    {
        var guard = _lifecycle.CaptureGuard(requireSession: false);
        _logger?.LogInformation("检测到断线重连阶段，准备自动重连...");

        _ = _supervisor.TrySpawnAsync(TaskKind.AutoReconnect, "AutoReconnect", async ct =>
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                // 校验仍处于同一对局的断线重连状态
                var currentLifecycle = _lifecycle.Snapshot();
                guard.EnsureValid(currentLifecycle);

                if (currentLifecycle.Phase != GamePhase.Reconnect)
                {
                    _logger?.LogInformation("当前游戏阶段为 {Phase}，已脱离断线状态，取消自动重连", currentLifecycle.Phase);
                    return;
                }

                var transport = _transportProvider();
                if (transport is null)
                {
                    _logger?.LogWarning("LCU 传输层未连接，重连尝试终止");
                    return;
                }

                // 发起请求前做权威校验：游戏进程存活 + LCU 阶段仍为 Reconnect
                if (!await IsReconnectStillValidAsync(transport, ct).ConfigureAwait(false))
                {
                    return;
                }

                _logger?.LogInformation("向 LCU 发送重连请求 (第 {Attempt}/{MaxAttempts} 次)...", attempt, MaxAttempts);
                var reconnectResp = await transport.SendAsync(
                    LcuRequest.Post("/lol-gameflow/v1/reconnect", "{}"),
                    ct).ConfigureAwait(false);

                if (reconnectResp.IsSuccess)
                {
                    _logger?.LogInformation("自动重连成功");
                    return;
                }

                if (attempt < MaxAttempts)
                {
                    var delay = TimeSpan.FromMilliseconds(500 * (1 << (attempt - 1)));
                    _logger?.LogInformation("重连请求失败 (HTTP {StatusCode})，将在 {Delay}s 后重试",
                        reconnectResp.StatusCode, delay.TotalSeconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }

            _logger?.LogWarning("自动重连 {MaxAttempts} 次尝试均失败", MaxAttempts);
        });
    }

    /// <summary>
    /// 发起重连前的权威校验：游戏进程仍存活，且 LCU 权威阶段仍是 <c>Reconnect</c>。
    /// </summary>
    /// <remarks>
    /// 「真实掉线」与「对局结束后 Alt+F4 退出」在 gameflow 上**都表现为 Reconnect**，
    /// 唯一可靠的区分点是游戏进程：真实掉线时进程仍在运行（只是与游戏服务器失联），
    /// 而主动退出后进程已消失，LCU 却会停留在 Reconnect 等待服务端结算写入（实测 2~4 秒）。
    /// 缺了进程校验，就会把已经结束的对局重新拉起并卡在加载页。
    /// </remarks>
    private async Task<bool> IsReconnectStillValidAsync(ILcuTransport transport, CancellationToken ct)
    {
        if (!await IsGameProcessAliveAsync(ct).ConfigureAwait(false))
        {
            _logger?.LogInformation("游戏进程已退出，判定对局已结束或用户主动退出，取消自动重连");
            return false;
        }

        // 阶段二次校验：WS 事件可能延迟，或对局结束瞬间闪现 Reconnect
        var phaseResp = await transport.SendAsync(
            LcuRequest.Get("/lol-gameflow/v1/gameflow-phase"),
            ct).ConfigureAwait(false);

        if (!phaseResp.IsSuccess || !IsReconnectBody(phaseResp.Body))
        {
            _logger?.LogInformation("LCU 二次校验显示已脱离断线状态，取消自动重连");
            return false;
        }

        // session.phase 是客户端的权威阶段，比 gameflow-phase 更贴近真实会话状态
        var sessionResp = await transport.SendAsync(
            LcuRequest.Get("/lol-gameflow/v1/session"),
            ct).ConfigureAwait(false);

        if (sessionResp.IsSuccess && TryReadSessionPhase(sessionResp.Body) is { } sessionPhase
            && sessionPhase != GamePhase.Reconnect)
        {
            _logger?.LogInformation("Gameflow session 权威阶段为 {Phase}，非重连状态，取消自动重连", sessionPhase);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 两次采样确认游戏进程存活（间隔 <see cref="GameProcessProbeInterval"/>）。
    /// </summary>
    private async Task<bool> IsGameProcessAliveAsync(CancellationToken ct)
    {
        if (!_gameProcessAliveProbe())
        {
            return false;
        }

        await Task.Delay(GameProcessProbeInterval, ct).ConfigureAwait(false);
        return _gameProcessAliveProbe();
    }

    private static bool IsReconnectBody(string body) =>
        string.Equals(body.Trim('"', ' '), "Reconnect", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 读取 gameflow session 的 <c>phase</c> 字段。读不到（字段缺失或响应非 JSON）时返回
    /// <c>null</c>，由调用方放行——不能让格式变化把真实掉线的自动重连一并禁掉。
    /// </summary>
    private static GamePhase? TryReadSessionPhase(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("phase", out var phaseElement)
                && phaseElement.ValueKind == JsonValueKind.String)
            {
                return GamePhaseExtensions.ParseGamePhase(phaseElement.GetString());
            }
        }
        catch (JsonException)
        {
            // 会话体非预期格式：交给后续阶段校验与 LCU 的 403 兜底
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }
}
