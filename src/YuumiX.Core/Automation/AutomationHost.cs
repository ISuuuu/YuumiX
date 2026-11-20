using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Common;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Automation;

/// <summary>
/// 自动化主控宿主。负责挂载 LcuConnection 事件流，驱动生命周期代际更新，并按观察者接口分派事件给各后台代理。
/// </summary>
public sealed class AutomationHost : IDisposable
{
    private const string GameflowPhasePath = "/lol-gameflow/v1/gameflow-phase";

    private readonly LcuConnection _connection;
    private readonly AgentLifecycle _lifecycle;
    private readonly IReadOnlyList<IAutomationAgent> _agents;
    private readonly IReadOnlyList<(string Name, IGamePhaseObserver Observer)> _phaseObservers;
    private readonly IReadOnlyList<(string Name, IPhaseReconcileObserver Observer)> _phaseReconcileObservers;
    private readonly IReadOnlyList<(string Name, ILcuEventObserver Observer)> _lcuEventObservers;
    private readonly ILogger<AutomationHost>? _logger;

    private IDisposable? _eventSubscription;
    private IDisposable? _stateSubscription;
    private CancellationToken _startToken;
    private bool _disposed;

    /// <summary>
    /// 当前游戏阶段流：Core 向 UI（标题栏等）暴露阶段的唯一载体（ADR-05）。
    /// 覆盖事件推送与连接成功后的阶段对账；断开连接时推送 <see cref="GamePhase.None"/>。
    /// </summary>
    public EventStream<GamePhase> Phases { get; } = new();

    /// <summary>
    /// 当前已知的游戏阶段快照。
    /// </summary>
    public GamePhase CurrentPhase => _lifecycle.Snapshot().Phase;

    public AutomationHost(
        LcuConnection connection,
        AgentLifecycle lifecycle,
        IEnumerable<IAutomationAgent> agents,
        ILogger<AutomationHost>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(agents);

        _connection = connection;
        _lifecycle = lifecycle;
        _logger = logger;

        var agentList = new List<IAutomationAgent>();
        var phaseObservers = new List<(string, IGamePhaseObserver)>();
        var phaseReconcileObservers = new List<(string, IPhaseReconcileObserver)>();
        var lcuEventObservers = new List<(string, ILcuEventObserver)>();

        foreach (var agent in agents)
        {
            ArgumentNullException.ThrowIfNull(agent);
            agentList.Add(agent);

            if (agent is IGamePhaseObserver phaseObserver)
            {
                phaseObservers.Add((agent.Name, phaseObserver));
            }

            if (agent is IPhaseReconcileObserver reconcileObserver)
            {
                phaseReconcileObservers.Add((agent.Name, reconcileObserver));
            }

            if (agent is ILcuEventObserver lcuEventObserver)
            {
                lcuEventObservers.Add((agent.Name, lcuEventObserver));
            }
        }

        _agents = agentList;
        _phaseObservers = phaseObservers;
        _phaseReconcileObservers = phaseReconcileObservers;
        _lcuEventObservers = lcuEventObservers;
    }

    /// <summary>
    /// 启动自动化监听与代理执行。
    /// </summary>
    public void Start(CancellationToken cancellationToken = default)
    {
        _startToken = cancellationToken;

        foreach (var agent in _agents)
        {
            _ = agent.StartAsync(cancellationToken);
        }

        _eventSubscription = _connection.Events.Subscribe(OnLcuEvent);

        _stateSubscription = _connection.States.Subscribe(state =>
        {
            if (state.Status == LcuConnectionStatus.Disconnected)
            {
                _lifecycle.UpdatePhase(GamePhase.None);

                foreach (var agent in _agents)
                {
                    if (agent is IConnectionResetObserver resetObserver)
                    {
                        Dispatch("disconnect", agent.Name, resetObserver.OnConnectionReset);
                    }
                    else if (agent is IGamePhaseObserver phaseObserver)
                    {
                        Dispatch("disconnect", agent.Name, () => phaseObserver.OnPhaseChanged(GamePhase.None));
                    }
                }

                Phases.Publish(GamePhase.None);
            }
            else if (state.Status == LcuConnectionStatus.Connected)
            {
                // 阶段对账：客户端可能早已处于某阶段（如空闲 None），订阅晚了就收不到阶段事件，
                // 主动拉取一次当前阶段，驱动自动建厅与标题栏状态。
                _ = ReconcilePhaseAsync();
            }
        });

        _logger?.LogInformation("自动化主控宿主已启动并监听 LCU 事件（已挂载 {Count} 个代理）", _agents.Count);
    }

    /// <summary>
    /// LCU 连接成功后的阶段对账：主动查询 gameflow-phase 并分派。
    /// 仅更新生命周期、通知实现 <see cref="IPhaseReconcileObserver"/> 的代理并发布阶段流；
    /// 其余代理由实时事件流驱动，避免对助手启动前已推进到一半的对局重复触发。
    /// </summary>
    private async Task ReconcilePhaseAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _startToken).ConfigureAwait(false);

            var transport = _connection.Transport;
            if (transport is null) return;

            var resp = await transport.SendAsync(
                LcuRequest.Get(GameflowPhasePath),
                _startToken).ConfigureAwait(false);
            if (!resp.IsSuccess) return;

            var phase = GamePhaseExtensions.ParseGamePhase(resp.Body.Trim().Trim('"'));
            _lifecycle.UpdatePhase(phase);

            foreach (var (name, observer) in _phaseReconcileObservers)
            {
                Dispatch(GameflowPhasePath, name, () => observer.OnPhaseChanged(phase));
            }

            Phases.Publish(phase);

            _logger?.LogInformation("LCU 连接阶段对账完成: {Phase}", phase);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LCU 连接阶段对账失败");
        }
    }

    /// <summary>
    /// 事件分发入口。
    ///
    /// 每个代理单独隔离异常：一次事件会分发给多个代理，若共用一个 try/catch，
    /// 任一代理抛异常会导致排在后面的代理以及 <see cref="Phases"/> 的发布被跳过。
    /// </summary>
    private void OnLcuEvent(LcuEvent evt)
    {
        try
        {
            if (evt.Uri.StartsWith(GameflowPhasePath, StringComparison.OrdinalIgnoreCase))
            {
                string? phaseStr = evt.Data.ValueKind == JsonValueKind.String
                    ? evt.Data.GetString()
                    : evt.Data.GetRawText().Trim('"');

                // 阶段是全局状态真源，先落库再分发：代理挂掉不该让阶段本身停住。
                var phase = GamePhaseExtensions.ParseGamePhase(phaseStr);
                _lifecycle.UpdatePhase(phase);

                foreach (var (name, observer) in _phaseObservers)
                {
                    Dispatch(evt.Uri, name, () => observer.OnPhaseChanged(phase));
                }

                Phases.Publish(phase);
                return;
            }

            foreach (var (name, observer) in _lcuEventObservers)
            {
                if (observer.CanHandle(evt.Uri))
                {
                    DispatchAsync(evt.Uri, name, () => observer.OnLcuEventAsync(evt));
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "处理自动化 LCU 事件失败: {Uri}", evt.Uri);
        }
    }

    /// <summary>隔离单个代理的同步异常：失败只记日志，不影响同一事件里的其他代理。</summary>
    private void Dispatch(string uri, string agent, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "代理 {Agent} 处理事件失败: {Uri}", agent, uri);
        }
    }

    /// <summary>隔离单个代理的同步/异步异常：同步完成不分配额外任务，异步任务挂接异常兜底。</summary>
    private void DispatchAsync(string uri, string agent, Func<ValueTask> action)
    {
        try
        {
            ValueTask task = action();
            if (!task.IsCompletedSuccessfully)
            {
                _ = ObserveAsync(uri, agent, task);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "代理 {Agent} 处理事件失败: {Uri}", agent, uri);
        }
    }

    private async Task ObserveAsync(string uri, string agent, ValueTask task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "代理 {Agent} 处理事件失败: {Uri}", agent, uri);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _eventSubscription?.Dispose();
        _stateSubscription?.Dispose();

        foreach (var agent in _agents)
        {
            _ = agent.StopAsync();
        }
    }
}
