using Microsoft.Extensions.Logging;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Automation;

/// <summary>
/// 对局结束后自动再来一局代理（对应旧版 agents/auto_match.rs::spawn_auto_play_again）。
/// 进入 WaitingForStats (10s) / PreEndOfGame (3.25s) / EndOfGame (1.575s) 后延迟校验并自动点击再来一局。
/// </summary>
public sealed class AutoPlayAgainAgent : IAutomationAgent
{
    private static readonly TimeSpan WaitingForStatsDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PreEndOfGameDelay = TimeSpan.FromMilliseconds(3250);
    private static readonly TimeSpan EndOfGameDelay = TimeSpan.FromMilliseconds(1575);

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Func<AppConfig> _configProvider;
    private readonly AgentSupervisor _supervisor;
    private readonly AgentLifecycle _lifecycle;
    private readonly ILogger<AutoPlayAgainAgent>? _logger;

    private readonly object _stateLock = new();
    private bool _isRunning;

    public string Name => "AutoPlayAgainAgent";
    public bool IsRunning => _isRunning;

    public AutoPlayAgainAgent(
        Func<ILcuTransport?> transportProvider,
        Func<AppConfig> configProvider,
        AgentSupervisor supervisor,
        AgentLifecycle lifecycle,
        ILogger<AutoPlayAgainAgent>? logger = null)
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

        _logger?.LogInformation("自动再来一局代理已启动");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _isRunning = false;
        }

        _supervisor.CancelKind(TaskKind.AutoPlayAgain);
        _logger?.LogInformation("自动再来一局代理已停止");
        return Task.CompletedTask;
    }

    public void OnPhaseChanged(GamePhase currentPhase)
    {
        if (!_isRunning) return;

        // 如果离开结算阶段，立即协同取消正在倒计时的自动再来一局任务
        if (currentPhase is not (GamePhase.WaitingForStats or GamePhase.PreEndOfGame or GamePhase.EndOfGame))
        {
            _supervisor.CancelKind(TaskKind.AutoPlayAgain);
            return;
        }

        var cfg = _configProvider().Functions;
        if (!cfg.EnableAutoPlayAgain) return;

        var delay = currentPhase switch
        {
            GamePhase.WaitingForStats => WaitingForStatsDelay,
            GamePhase.PreEndOfGame => PreEndOfGameDelay,
            GamePhase.EndOfGame => EndOfGameDelay,
            _ => WaitingForStatsDelay,
        };

        TriggerAutoPlayAgain(delay);
    }

    private void TriggerAutoPlayAgain(TimeSpan delay)
    {
        var guard = _lifecycle.CaptureGuard(requireSession: false);
        _logger?.LogInformation("检测到对局结算阶段，将在 {Delay}s 后自动再来一局...", delay.TotalSeconds);

        _ = _supervisor.TrySpawnAsync(TaskKind.AutoPlayAgain, $"AutoPlayAgain({delay.TotalSeconds}s)", async ct =>
        {
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogInformation("自动再来一局任务已协同取消");
                return;
            }

            ct.ThrowIfCancellationRequested();

            var currentLifecycle = _lifecycle.Snapshot();
            try
            {
                guard.EnsureValid(currentLifecycle);
            }
            catch (Exception ex)
            {
                _logger?.LogInformation("自动再来一局放弃: 生命周期代际已失效 ({Message})", ex.Message);
                return;
            }

            if (currentLifecycle.Phase is not (GamePhase.WaitingForStats or GamePhase.PreEndOfGame or GamePhase.EndOfGame))
            {
                _logger?.LogInformation("当前游戏阶段已变为 {Phase}，取消自动再来一局", currentLifecycle.Phase);
                return;
            }

            var transport = _transportProvider();
            if (transport is null)
            {
                _logger?.LogWarning("LCU 传输层未连接，自动再来一局终止");
                return;
            }

            _logger?.LogInformation("生命周期校验通过，向 LCU 发送再来一局请求...");
            var resp = await transport.SendAsync(
                LcuRequest.Post("/lol-lobby/v2/play-again", "{}"),
                ct).ConfigureAwait(false);

            if (resp.IsSuccess)
            {
                _logger?.LogInformation("自动再来一局成功");
            }
            else
            {
                _logger?.LogWarning("自动再来一局失败: HTTP {Status}", resp.StatusCode);
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }
}
