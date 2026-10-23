using Microsoft.Extensions.Logging;
using System.Text.Json;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Automation;

/// <summary>
/// 自动创建默认模式大厅代理（对应旧版 agents/auto_match.rs::try_create_default_lobby）。
/// 客户端处于空闲阶段（None）时按默认 queueId 自动创建大厅：
/// 重试上限内轮询执行，覆盖「客户端启动初期页面跳转把大厅冲掉」与「已在有效大厅」两种场景。
/// 语义与原版一致：每个 LCU 进程生命周期内只在进入空闲阶段时建厅一次，
/// 用户离开空闲阶段后不再自动重建（除非连接重置）。
/// </summary>
public sealed class AutoCreateLobbyAgent : IAutomationAgent
{
    /// <summary>建厅重试上限（每轮间隔约 2 秒，覆盖客户端启动初期冲掉大厅的场景）。</summary>
    private const int MaxAttempts = 12;

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PostCreateVerifyDelay = TimeSpan.FromSeconds(1);

    /// <summary>视为"空闲"的阶段集合：允许发起建厅。</summary>
    private static readonly GamePhase[] IdlePhases = [GamePhase.None, GamePhase.WaitingForStats, GamePhase.PreEndOfGame];

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Func<AppConfig> _configProvider;
    private readonly AgentSupervisor _supervisor;
    private readonly AgentLifecycle _lifecycle;
    private readonly ILogger<AutoCreateLobbyAgent>? _logger;

    private readonly object _stateLock = new();
    private bool _isRunning;
    private bool _createdForCurrentClient;

    public string Name => "AutoCreateLobbyAgent";
    public bool IsRunning => _isRunning;

    public AutoCreateLobbyAgent(
        Func<ILcuTransport?> transportProvider,
        Func<AppConfig> configProvider,
        AgentSupervisor supervisor,
        AgentLifecycle lifecycle,
        ILogger<AutoCreateLobbyAgent>? logger = null)
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

        _logger?.LogInformation("自动创建大厅代理已启动");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _isRunning = false;
        }

        _supervisor.CancelKind(TaskKind.AutoCreateLobby);
        _logger?.LogInformation("自动创建大厅代理已停止");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 处理游戏阶段变化：仅空闲阶段（None）触发建厅。
    /// </summary>
    public void OnPhaseChanged(GamePhase currentPhase)
    {
        if (!_isRunning) return;
        if (currentPhase != GamePhase.None) return;

        var cfg = _configProvider().Functions;
        if (!cfg.EnableAutoCreateLobby) return;

        lock (_stateLock)
        {
            if (_createdForCurrentClient)
            {
                _logger?.LogDebug("本轮客户端生命周期内已建过大厅，跳过自动建厅");
                return;
            }
        }

        _ = _supervisor.TrySpawnAsync(TaskKind.AutoCreateLobby, "AutoCreateLobby", CreateLobbyTaskAsync);
    }

    /// <summary>
    /// LCU 连接重置（断开/客户端重启）：清空建厅标记，允许下个客户端实例重新建厅。
    /// </summary>
    public void OnConnectionReset()
    {
        lock (_stateLock)
        {
            _createdForCurrentClient = false;
        }
    }

    private async Task CreateLobbyTaskAsync(CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // 阶段守卫：仅在空闲阶段允许建厅，用户已离开空闲阶段则视为本轮机会已消费。
            var phase = _lifecycle.Snapshot().Phase;
            if (!IdlePhases.Contains(phase))
            {
                lock (_stateLock)
                {
                    _createdForCurrentClient = true;
                }

                _logger?.LogInformation("当前阶段 {Phase} 非空闲，放弃自动建厅", phase);
                return;
            }

            var transport = _transportProvider();
            if (transport is null)
            {
                _logger?.LogWarning("LCU 传输层未连接，自动建厅终止");
                return;
            }

            // 已在有效大厅：不再重复创建，避免踢出已有队伍。
            var lobbyResp = await transport.SendAsync(
                LcuRequest.Get("/lol-lobby/v2/lobby"),
                ct).ConfigureAwait(false);
            if (lobbyResp.IsSuccess)
            {
                lock (_stateLock)
                {
                    _createdForCurrentClient = true;
                }

                _logger?.LogInformation("检测到已存在有效大厅，跳过自动建厅");
                return;
            }

            var queueId = _configProvider().Functions.DefaultGameMode;
            var body = JsonSerializer.Serialize(new { queueId });
            var createResp = await transport.SendAsync(
                LcuRequest.Post("/lol-lobby/v2/lobby", body),
                ct).ConfigureAwait(false);

            if (!createResp.IsSuccess)
            {
                _logger?.LogWarning("创建大厅失败 (HTTP {StatusCode}, 第 {Attempt}/{MaxAttempts} 次)",
                    createResp.StatusCode, attempt, MaxAttempts);
                await Task.Delay(RetryInterval, ct).ConfigureAwait(false);
                continue;
            }

            // 创建后缓冲 1 秒再校验：客户端启动初期页面跳转可能把刚建的大厅冲掉。
            await Task.Delay(PostCreateVerifyDelay, ct).ConfigureAwait(false);
            var verifyResp = await transport.SendAsync(
                LcuRequest.Get("/lol-lobby/v2/lobby"),
                ct).ConfigureAwait(false);

            if (verifyResp.IsSuccess)
            {
                lock (_stateLock)
                {
                    _createdForCurrentClient = true;
                }

                _logger?.LogInformation("自动创建大厅成功 (queueId={QueueId})", queueId);
                return;
            }

            _logger?.LogInformation("创建的大厅已被客户端冲掉 (queueId={QueueId}, 第 {Attempt}/{MaxAttempts} 次)，继续重试",
                queueId, attempt, MaxAttempts);
            await Task.Delay(RetryInterval, ct).ConfigureAwait(false);
        }

        _logger?.LogWarning("自动创建大厅 {MaxAttempts} 次尝试均失败", MaxAttempts);
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }
}
