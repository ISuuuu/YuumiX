using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Automation;

/// <summary>
/// 自动接受对局代理（对齐旧版 agents/auto_match.rs）。
/// 监听 ready-check 事件与 gameflow-phase 事件，在设定的延迟后自动确认对局。
/// </summary>
public sealed class AutoAcceptAgent : IAutomationAgent
{
    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Func<AppConfig> _configProvider;
    private readonly AgentSupervisor _supervisor;
    private readonly AgentLifecycle _lifecycle;
    private readonly ILogger<AutoAcceptAgent>? _logger;

    private readonly object _stateLock = new();
    private bool _readyCheckAccepted;
    private bool _isRunning;
    private CancellationTokenSource? _agentCts;

    public string Name => "AutoAcceptAgent";
    public bool IsRunning => _isRunning;

    public AutoAcceptAgent(
        Func<ILcuTransport?> transportProvider,
        Func<AppConfig> configProvider,
        AgentSupervisor supervisor,
        AgentLifecycle lifecycle,
        ILogger<AutoAcceptAgent>? logger = null)
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
            if (_isRunning) return Task.CompletedTask;
            _isRunning = true;
            _agentCts = new CancellationTokenSource();
        }

        _logger?.LogInformation("自动接受对局代理已启动");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (!_isRunning) return Task.CompletedTask;
            _isRunning = false;
            _agentCts?.Cancel();
            _agentCts?.Dispose();
            _agentCts = null;
        }

        _supervisor.CancelKind(TaskKind.AutoAccept);
        _logger?.LogInformation("自动接受对局代理已停止");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 处理阶段变化（来自 LcuEventStream 或 Gameflow 监听）。
    /// </summary>
    public void OnPhaseChanged(GamePhase phase)
    {
        if (!_isRunning) return;

        if (phase != GamePhase.ReadyCheck)
        {
            lock (_stateLock)
            {
                _readyCheckAccepted = false;
            }
            _supervisor.CancelKind(TaskKind.AutoAccept);
            return;
        }

        var cfg = _configProvider().Functions;
        if (!cfg.EnableAutoAcceptMatching) return;

        bool shouldTrigger = false;
        lock (_stateLock)
        {
            if (!_readyCheckAccepted)
            {
                _readyCheckAccepted = true;
                shouldTrigger = true;
            }
        }

        if (shouldTrigger)
        {
            TriggerAutoAccept(cfg.AutoAcceptMatchingDelay);
        }
    }

    /// <summary>
    /// 处理 /lol-matchmaking/v1/ready-check 事件载荷。
    /// </summary>
    public void OnReadyCheckEvent(JsonElement data)
    {
        if (!_isRunning) return;

        var cfg = _configProvider().Functions;
        if (!cfg.EnableAutoAcceptMatching) return;

        string? playerResponse = null;
        if (data.TryGetProperty("playerResponse", out var respElem) && respElem.ValueKind == JsonValueKind.String)
        {
            playerResponse = respElem.GetString();
        }

        if (string.Equals(playerResponse, "Accepted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(playerResponse, "Declined", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogDebug("玩家已手动响应匹配 ({Response})，取消自动接受", playerResponse);
            lock (_stateLock)
            {
                _readyCheckAccepted = true;
            }
            _supervisor.CancelKind(TaskKind.AutoAccept);
            return;
        }

        bool shouldTrigger = false;
        lock (_stateLock)
        {
            if (!_readyCheckAccepted)
            {
                _readyCheckAccepted = true;
                shouldTrigger = true;
            }
        }

        if (shouldTrigger)
        {
            TriggerAutoAccept(cfg.AutoAcceptMatchingDelay);
        }
    }

    private void TriggerAutoAccept(int delaySeconds)
    {
        var guard = _lifecycle.CaptureGuard(requireSession: false);
        string desc = $"AutoAccept(delay={delaySeconds}s)";

        _ = _supervisor.TrySpawnAsync(TaskKind.AutoAccept, desc, async ct =>
        {
            if (delaySeconds > 0)
            {
                _logger?.LogInformation("将在 {Delay} 秒后自动接受匹配...", delaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }
            else
            {
                _logger?.LogInformation("立即自动接受匹配");
            }

            ct.ThrowIfCancellationRequested();

            // 延迟后，校验生命周期与对局代际
            var currentLifecycle = _lifecycle.Snapshot();
            guard.EnsureValid(currentLifecycle);

            if (currentLifecycle.Phase != GamePhase.ReadyCheck)
            {
                _logger?.LogInformation("延迟后当前游戏阶段为 {Phase}，不再是 ReadyCheck，取消自动接受", currentLifecycle.Phase);
                return;
            }

            var transport = _transportProvider();
            if (transport is null)
            {
                _logger?.LogWarning("LCU 传输层未就绪，取消自动接受");
                return;
            }

            // 再次验证当前 ready-check 状态
            var checkResp = await transport.SendAsync(
                LcuRequest.Get("/lol-matchmaking/v1/ready-check"),
                ct).ConfigureAwait(false);

            if (checkResp.IsSuccess)
            {
                using var doc = JsonDocument.Parse(checkResp.Body);
                if (doc.RootElement.TryGetProperty("playerResponse", out var pr) && pr.ValueKind == JsonValueKind.String)
                {
                    string? r = pr.GetString();
                    if (string.Equals(r, "Accepted", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(r, "Declined", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.LogDebug("ReadyCheck 再次查询显示玩家已响应 ({Response})，取消自动接受", r);
                        return;
                    }
                }
            }

            _logger?.LogInformation("向 LCU 发送自动接受匹配请求");
            await transport.SendAsync(
                LcuRequest.Post("/lol-matchmaking/v1/ready-check/accept", "{}"),
                ct).ConfigureAwait(false);
        });
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }
}
