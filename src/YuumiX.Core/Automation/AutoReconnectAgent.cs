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
    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Func<AppConfig> _configProvider;
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
        ILogger<AutoReconnectAgent>? logger = null)
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

                // 发起请求前向 LCU 主动二次校验当前阶段
                var phaseResp = await transport.SendAsync(
                    LcuRequest.Get("/lol-gameflow/v1/gameflow-phase"),
                    ct).ConfigureAwait(false);

                if (!phaseResp.IsSuccess || !string.Equals(phaseResp.Body.Trim('"', ' '), "Reconnect", StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogInformation("LCU 二次校验显示已脱离断线状态，取消自动重连");
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

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }
}
