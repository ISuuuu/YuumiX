using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Automation.Models;
using YuumiX.Core.Common;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Automation;

/// <summary>
/// 自动化主控宿主。负责挂载 LcuConnection 事件流，驱动生命周期代际更新，并分派事件给各后台代理。
/// </summary>
public sealed class AutomationHost : IDisposable
{
    private readonly LcuConnection _connection;
    private readonly AgentLifecycle _lifecycle;
    private readonly AutoAcceptAgent _autoAcceptAgent;
    private readonly AutoReconnectAgent _autoReconnectAgent;
    private readonly AutoBanPickAgent _autoBanPickAgent;
    private readonly AutoCreateLobbyAgent _autoCreateLobbyAgent;
    private readonly AramTeamSideAgent _aramTeamSideAgent;
    private readonly AutoEncounterRecorderAgent _autoEncounterRecorderAgent;
    private readonly AutoTagReminderAgent _autoTagReminderAgent;
    private readonly MultiKillCaptureAgent _multiKillCaptureAgent;
    private readonly AutoInviteAgent _autoInviteAgent;
    private readonly AutoHonorAgent _autoHonorAgent;
    private readonly AutoPlayAgainAgent _autoPlayAgainAgent;
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
        AutoAcceptAgent autoAcceptAgent,
        AutoReconnectAgent autoReconnectAgent,
        AutoBanPickAgent autoBanPickAgent,
        AutoCreateLobbyAgent autoCreateLobbyAgent,
        AramTeamSideAgent aramTeamSideAgent,
        AutoEncounterRecorderAgent autoEncounterRecorderAgent,
        AutoTagReminderAgent autoTagReminderAgent,
        MultiKillCaptureAgent multiKillCaptureAgent,
        AutoInviteAgent autoInviteAgent,
        AutoHonorAgent autoHonorAgent,
        AutoPlayAgainAgent autoPlayAgainAgent,
        ILogger<AutomationHost>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(autoAcceptAgent);
        ArgumentNullException.ThrowIfNull(autoReconnectAgent);
        ArgumentNullException.ThrowIfNull(autoBanPickAgent);
        ArgumentNullException.ThrowIfNull(autoCreateLobbyAgent);
        ArgumentNullException.ThrowIfNull(aramTeamSideAgent);
        ArgumentNullException.ThrowIfNull(autoEncounterRecorderAgent);
        ArgumentNullException.ThrowIfNull(autoTagReminderAgent);
        ArgumentNullException.ThrowIfNull(multiKillCaptureAgent);
        ArgumentNullException.ThrowIfNull(autoInviteAgent);
        ArgumentNullException.ThrowIfNull(autoHonorAgent);
        ArgumentNullException.ThrowIfNull(autoPlayAgainAgent);

        _connection = connection;
        _lifecycle = lifecycle;
        _autoAcceptAgent = autoAcceptAgent;
        _autoReconnectAgent = autoReconnectAgent;
        _autoBanPickAgent = autoBanPickAgent;
        _autoCreateLobbyAgent = autoCreateLobbyAgent;
        _aramTeamSideAgent = aramTeamSideAgent;
        _autoEncounterRecorderAgent = autoEncounterRecorderAgent;
        _autoTagReminderAgent = autoTagReminderAgent;
        _multiKillCaptureAgent = multiKillCaptureAgent;
        _autoInviteAgent = autoInviteAgent;
        _autoHonorAgent = autoHonorAgent;
        _autoPlayAgainAgent = autoPlayAgainAgent;
        _logger = logger;
    }

    /// <summary>
    /// 启动自动化监听与代理执行。
    /// </summary>
    public void Start(CancellationToken cancellationToken = default)
    {
        _startToken = cancellationToken;

        _ = _autoAcceptAgent.StartAsync(cancellationToken);
        _ = _autoReconnectAgent.StartAsync(cancellationToken);
        _ = _autoBanPickAgent.StartAsync(cancellationToken);
        _ = _autoCreateLobbyAgent.StartAsync(cancellationToken);
        _ = _aramTeamSideAgent.StartAsync(cancellationToken);
        _ = _autoEncounterRecorderAgent.StartAsync(cancellationToken);
        _ = _autoTagReminderAgent.StartAsync(cancellationToken);
        _ = _multiKillCaptureAgent.StartAsync(cancellationToken);
        _ = _autoInviteAgent.StartAsync(cancellationToken);
        _ = _autoHonorAgent.StartAsync(cancellationToken);
        _ = _autoPlayAgainAgent.StartAsync(cancellationToken);

        _eventSubscription = _connection.Events.Subscribe(OnLcuEvent);

        _stateSubscription = _connection.States.Subscribe(state =>
        {
            if (state.Status == LcuConnectionStatus.Disconnected)
            {
                _lifecycle.UpdatePhase(GamePhase.None);
                _autoAcceptAgent.OnPhaseChanged(GamePhase.None);
                _autoReconnectAgent.OnPhaseChanged(GamePhase.None);
                _autoCreateLobbyAgent.OnConnectionReset();
                _multiKillCaptureAgent.OnPhaseChanged(GamePhase.None);
                _autoPlayAgainAgent.OnPhaseChanged(GamePhase.None);
                _autoHonorAgent.OnPhaseChanged(GamePhase.None);
                Phases.Publish(GamePhase.None);
            }
            else if (state.Status == LcuConnectionStatus.Connected)
            {
                // 阶段对账：客户端可能早已处于某阶段（如空闲 None），订阅晚了就收不到阶段事件，
                // 主动拉取一次当前阶段，驱动自动建厅与标题栏状态。
                _ = ReconcilePhaseAsync();
            }
        });

        _logger?.LogInformation("自动化主控宿主已启动并监听 LCU 事件");
    }

    /// <summary>
    /// LCU 连接成功后的阶段对账：主动查询 gameflow-phase 并分派。
    /// 仅更新生命周期、通知自动建厅代理并发布阶段流；其余代理由事件流驱动，避免对
    /// 助手启动前已推进到一半的对局重复触发（原版即为纯事件驱动语义）。
    /// </summary>
    private async Task ReconcilePhaseAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _startToken).ConfigureAwait(false);

            var transport = _connection.Transport;
            if (transport is null) return;

            var resp = await transport.SendAsync(
                LcuRequest.Get("/lol-gameflow/v1/gameflow-phase"),
                _startToken).ConfigureAwait(false);
            if (!resp.IsSuccess) return;

            var phase = GamePhaseExtensions.ParseGamePhase(resp.Body.Trim().Trim('"'));
            _lifecycle.UpdatePhase(phase);
            _autoCreateLobbyAgent.OnPhaseChanged(phase);
            _autoEncounterRecorderAgent.OnPhaseChanged(phase);
            _multiKillCaptureAgent.OnPhaseChanged(phase);
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
    /// 每个代理单独隔离异常：一次事件会分发给多个代理（phase 事件挂了九个），
    /// 若共用一个 try/catch，任一代理抛异常，排在它后面的代理以及
    /// <see cref="Phases"/> 的发布都会被一起跳过——表现成「界面阶段停在上一个值 + 部分自动化
    /// 静默失效」，日志里只有一条笼统的「处理 LCU 事件失败」，看不出是谁挂的。
    /// </summary>
    private void OnLcuEvent(LcuEvent evt)
    {
        try
        {
            if (evt.Uri.StartsWith("/lol-gameflow/v1/gameflow-phase", StringComparison.OrdinalIgnoreCase))
            {
                string? phaseStr = evt.Data.ValueKind == JsonValueKind.String
                    ? evt.Data.GetString()
                    : evt.Data.GetRawText().Trim('"');

                // 阶段是全局状态真源，先落库再分发：代理挂掉不该让阶段本身停住。
                var phase = GamePhaseExtensions.ParseGamePhase(phaseStr);
                _lifecycle.UpdatePhase(phase);

                Dispatch(evt.Uri, nameof(AutoAcceptAgent), () => _autoAcceptAgent.OnPhaseChanged(phase));
                Dispatch(evt.Uri, nameof(AutoReconnectAgent), () => _autoReconnectAgent.OnPhaseChanged(phase));
                Dispatch(evt.Uri, nameof(AutoCreateLobbyAgent), () => _autoCreateLobbyAgent.OnPhaseChanged(phase));
                Dispatch(evt.Uri, nameof(AramTeamSideAgent), () => _aramTeamSideAgent.OnPhaseChanged(phase));
                Dispatch(evt.Uri, nameof(AutoEncounterRecorderAgent), () => _autoEncounterRecorderAgent.OnPhaseChanged(phase));
                Dispatch(evt.Uri, nameof(AutoTagReminderAgent), () => _autoTagReminderAgent.OnPhaseChanged(phase));
                Dispatch(evt.Uri, nameof(MultiKillCaptureAgent), () => _multiKillCaptureAgent.OnPhaseChanged(phase));
                Dispatch(evt.Uri, nameof(AutoPlayAgainAgent), () => _autoPlayAgainAgent.OnPhaseChanged(phase));
                Dispatch(evt.Uri, nameof(AutoHonorAgent), () => _autoHonorAgent.OnPhaseChanged(phase));

                Phases.Publish(phase);
            }
            else if (evt.Uri.StartsWith("/lol-matchmaking/v1/ready-check", StringComparison.OrdinalIgnoreCase))
            {
                Dispatch(evt.Uri, nameof(AutoAcceptAgent), () => _autoAcceptAgent.OnReadyCheckEvent(evt.Data));
            }
            else if (evt.Uri.StartsWith("/lol-lobby/v2/received-invitations", StringComparison.OrdinalIgnoreCase))
            {
                Dispatch(evt.Uri, nameof(AutoInviteAgent), () => _autoInviteAgent.OnInvitationsEvent(evt.Data));
            }
            else if (evt.Uri.StartsWith("/lol-honor-v2/v1/ballot", StringComparison.OrdinalIgnoreCase))
            {
                Dispatch(evt.Uri, nameof(AutoHonorAgent), () => _autoHonorAgent.OnBallotEvent(evt.Data));
            }
            else if (evt.Uri.StartsWith("/lol-champ-select/v1/session", StringComparison.OrdinalIgnoreCase))
            {
                Dispatch(evt.Uri, nameof(AutoBanPickAgent), () =>
                {
                    var session = JsonSerializer.Deserialize<ChampSelectSession>(evt.Data.GetRawText());
                    if (session is not null)
                    {
                        _ = RunBanPickAsync(evt.Uri, session);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "处理自动化 LCU 事件失败: {Uri}", evt.Uri);
        }
    }

    /// <summary>隔离单个代理的异常：失败只记日志，不影响同一事件里的其他代理。</summary>
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

    /// <summary>选人会话是异步处理，异常不会回到 <see cref="Dispatch"/> 的 try 里，单独兜住。</summary>
    private async Task RunBanPickAsync(string uri, ChampSelectSession session)
    {
        try
        {
            await _autoBanPickAgent.HandleSessionEventAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "代理 {Agent} 处理事件失败: {Uri}", nameof(AutoBanPickAgent), uri);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _eventSubscription?.Dispose();
        _stateSubscription?.Dispose();
        _ = _autoAcceptAgent.StopAsync();
        _ = _autoReconnectAgent.StopAsync();
        _ = _autoBanPickAgent.StopAsync();
        _ = _autoCreateLobbyAgent.StopAsync();
        _ = _aramTeamSideAgent.StopAsync();
        _ = _autoEncounterRecorderAgent.StopAsync();
        _ = _autoTagReminderAgent.StopAsync();
        _ = _multiKillCaptureAgent.StopAsync();
        _ = _autoInviteAgent.StopAsync();
        _ = _autoHonorAgent.StopAsync();
        _ = _autoPlayAgainAgent.StopAsync();
    }
}
