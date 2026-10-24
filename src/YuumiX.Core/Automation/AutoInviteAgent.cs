using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Automation;

/// <summary>
/// 自动接受组队邀请代理（对应旧版 agents/auto_match.rs::spawn_handle_invitations）。
/// 监听 /lol-lobby/v2/received-invitations 事件，当收到状态为 Pending 且可接受的邀请时自动同意。
/// </summary>
public sealed class AutoInviteAgent : IAutomationAgent
{
    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Func<AppConfig> _configProvider;
    private readonly AgentSupervisor _supervisor;
    private readonly ILogger<AutoInviteAgent>? _logger;

    private readonly object _stateLock = new();
    private bool _isRunning;

    public string Name => "AutoInviteAgent";
    public bool IsRunning => _isRunning;

    public AutoInviteAgent(
        Func<ILcuTransport?> transportProvider,
        Func<AppConfig> configProvider,
        AgentSupervisor supervisor,
        ILogger<AutoInviteAgent>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(supervisor);

        _transportProvider = transportProvider;
        _configProvider = configProvider;
        _supervisor = supervisor;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _isRunning = true;
        }

        _logger?.LogInformation("自动接受邀请代理已启动");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _isRunning = false;
        }

        _supervisor.CancelKind(TaskKind.AutoInvite);
        _logger?.LogInformation("自动接受邀请代理已停止");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 处理接收到的邀请事件。
    /// </summary>
    public void OnInvitationsEvent(JsonElement data)
    {
        if (!_isRunning) return;

        var cfg = _configProvider().Functions;
        if (!cfg.EnableAutoHandleInvite) return;

        if (data.ValueKind != JsonValueKind.Array) return;

        var invitationIds = new List<string>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            string? state = item.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            bool canAccept = item.TryGetProperty("canAcceptInvitation", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False && c.GetBoolean();
            string? id = item.TryGetProperty("invitationId", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : null;

            if (string.Equals(state, "Pending", StringComparison.OrdinalIgnoreCase) && canAccept && !string.IsNullOrEmpty(id))
            {
                invitationIds.Add(id);
            }
        }

        if (invitationIds.Count == 0) return;

        _ = _supervisor.TrySpawnAsync(TaskKind.AutoInvite, "AutoInvite", async ct =>
        {
            var transport = _transportProvider();
            if (transport is null) return;

            foreach (var id in invitationIds)
            {
                ct.ThrowIfCancellationRequested();
                var resp = await transport.SendAsync(
                    LcuRequest.Post($"/lol-lobby/v2/received-invitations/{id}/accept", "{}"),
                    ct).ConfigureAwait(false);

                if (resp.IsSuccess)
                {
                    _logger?.LogInformation("已自动接受好友邀请: {InvitationId}", id);
                }
                else
                {
                    _logger?.LogWarning("自动接受邀请 {InvitationId} 失败: HTTP {Status}", id, resp.StatusCode);
                }
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }
}
