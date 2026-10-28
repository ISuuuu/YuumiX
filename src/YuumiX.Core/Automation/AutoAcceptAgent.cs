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
    private const string ReadyCheckPath = "/lol-matchmaking/v1/ready-check";
    private const string ReadyCheckAcceptPath = "/lol-matchmaking/v1/ready-check/accept";

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

        // ready-check 结束时 LCU 会下发 JSON null（"清空"语义）。
        // JsonElement.TryGetProperty 只对 Object 有效，对 Null 直接抛
        // InvalidOperationException——以前每次确认结束都抛一次（日志里成片的 ERROR），
        // 更糟的是它把 _readyCheckAccepted 永久卡在 true：下一次确认若恰好没有
        // phase 事件来复位，整局都不会再触发自动接受。这里按"本次确认已结束"复位。
        if (data.ValueKind != JsonValueKind.Object)
        {
            ResetAcceptedFlag();
            return;
        }

        var cfg = _configProvider().Functions;
        if (!cfg.EnableAutoAcceptMatching) return;

        string? playerResponse = ReadPlayerResponse(data);

        if (IsTerminalResponse(playerResponse))
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

    private void ResetAcceptedFlag()
    {
        lock (_stateLock)
        {
            _readyCheckAccepted = false;
        }
    }

    /// <summary>读取 ready-check 载荷里的 playerResponse；缺失或非字符串返回 <c>null</c>。</summary>
    private static string? ReadPlayerResponse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return data.TryGetProperty("playerResponse", out var elem) && elem.ValueKind == JsonValueKind.String
            ? elem.GetString()
            : null;
    }

    /// <summary>解析 ready-check 响应体里的 playerResponse；载荷异常一律返回 <c>null</c>。</summary>
    private static string? ReadPlayerResponse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return ReadPlayerResponse(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>玩家是否已经给出最终响应（接受/拒绝）——这两种情况都不该再替他点。</summary>
    private static bool IsTerminalResponse(string? response) =>
        string.Equals(response, "Accepted", StringComparison.OrdinalIgnoreCase)
        || string.Equals(response, "Declined", StringComparison.OrdinalIgnoreCase);

    private void TriggerAutoAccept(int delaySeconds)
    {
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

            var transport = _transportProvider();
            if (transport is null)
            {
                _logger?.LogWarning("LCU 传输层未就绪，取消自动接受");
                return;
            }

            // 唯一判据是 ready-check 接口本身——它是「当前是否有进行中的确认」的权威事实。
            //
            // 这里刻意不再用 gameflow phase，也不用生命周期代际守卫（CaptureGuard/EnsureValid）：
            // 两者都由另一条事件流驱动，与 ready-check 事件之间存在竞态，实测会以两种方式
            // 让自动接受静默失效：
            //   · 延迟窗口内 phase 从 Lobby/Matchmaking 推进到 ReadyCheck，PhaseRevision 随之
            //     递增，EnsureValid 抛 StaleGenerationException，任务作废且只记 Debug 日志；
            //   · 反向滞后时 phase 仍停在 Lobby，又被当成「确认已结束」主动取消。
            // 结果都是「计划了却什么都没发生」，日志里看不出原因。
            var currentLifecycle = _lifecycle.Snapshot();
            _logger?.LogDebug(
                "ReadyCheck 前置快照: 阶段={Phase}（仅记录，不作为判定依据）",
                currentLifecycle.Phase);

            var checkResp = await transport.SendAsync(
                LcuRequest.Get(ReadyCheckPath),
                ct).ConfigureAwait(false);

            if (!checkResp.IsSuccess)
            {
                _logger?.LogInformation(
                    "ReadyCheck 已结束（HTTP {Status}），放弃自动接受",
                    checkResp.StatusCode);
                return;
            }

            string? currentResponse = ReadPlayerResponse(checkResp.Body);
            if (IsTerminalResponse(currentResponse))
            {
                _logger?.LogInformation(
                    "ReadyCheck 显示玩家已响应（{Response}），放弃自动接受",
                    currentResponse);
                return;
            }

            _logger?.LogInformation("向 LCU 发送自动接受匹配请求");
            await transport.SendAsync(
                LcuRequest.Post(ReadyCheckAcceptPath, "{}"),
                ct).ConfigureAwait(false);
        });
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }
}
