using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Automation.Models;
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
    private readonly ILogger<AutomationHost>? _logger;

    private IDisposable? _eventSubscription;
    private IDisposable? _stateSubscription;
    private bool _disposed;

    public AutomationHost(
        LcuConnection connection,
        AgentLifecycle lifecycle,
        AutoAcceptAgent autoAcceptAgent,
        AutoReconnectAgent autoReconnectAgent,
        AutoBanPickAgent autoBanPickAgent,
        ILogger<AutomationHost>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(autoAcceptAgent);
        ArgumentNullException.ThrowIfNull(autoReconnectAgent);
        ArgumentNullException.ThrowIfNull(autoBanPickAgent);

        _connection = connection;
        _lifecycle = lifecycle;
        _autoAcceptAgent = autoAcceptAgent;
        _autoReconnectAgent = autoReconnectAgent;
        _autoBanPickAgent = autoBanPickAgent;
        _logger = logger;
    }

    /// <summary>
    /// 启动自动化监听与代理执行。
    /// </summary>
    public void Start(CancellationToken cancellationToken = default)
    {
        _ = _autoAcceptAgent.StartAsync(cancellationToken);
        _ = _autoReconnectAgent.StartAsync(cancellationToken);
        _ = _autoBanPickAgent.StartAsync(cancellationToken);

        _eventSubscription = _connection.Events.Subscribe(OnLcuEvent);

        _stateSubscription = _connection.States.Subscribe(state =>
        {
            if (state.Status == LcuConnectionStatus.Disconnected)
            {
                _lifecycle.UpdatePhase(GamePhase.None);
                _autoAcceptAgent.OnPhaseChanged(GamePhase.None);
                _autoReconnectAgent.OnPhaseChanged(GamePhase.None);
            }
        });

        _logger?.LogInformation("自动化主控宿主已启动并监听 LCU 事件");
    }

    private void OnLcuEvent(LcuEvent evt)
    {
        try
        {
            if (evt.Uri.StartsWith("/lol-gameflow/v1/gameflow-phase", StringComparison.OrdinalIgnoreCase))
            {
                string? phaseStr = evt.Data.ValueKind == JsonValueKind.String
                    ? evt.Data.GetString()
                    : evt.Data.GetRawText().Trim('"');

                var phase = GamePhaseExtensions.ParseGamePhase(phaseStr);
                _lifecycle.UpdatePhase(phase);
                _autoAcceptAgent.OnPhaseChanged(phase);
                _autoReconnectAgent.OnPhaseChanged(phase);
            }
            else if (evt.Uri.StartsWith("/lol-matchmaking/v1/ready-check", StringComparison.OrdinalIgnoreCase))
            {
                _autoAcceptAgent.OnReadyCheckEvent(evt.Data);
            }
            else if (evt.Uri.StartsWith("/lol-champ-select/v1/session", StringComparison.OrdinalIgnoreCase))
            {
                var session = JsonSerializer.Deserialize<ChampSelectSession>(evt.Data.GetRawText());
                if (session is not null)
                {
                    _ = _autoBanPickAgent.HandleSessionEventAsync(session);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "处理自动化 LCU 事件失败: {Uri}", evt.Uri);
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
    }
}
