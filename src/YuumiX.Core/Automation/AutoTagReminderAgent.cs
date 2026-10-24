using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Features.SavedPlayers;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Automation;

/// <summary>
/// 选人阶段已标记路人提醒代理（对应旧版 agents/auto_match.rs::spawn_tag_reminder）。
/// 进入 ChampSelect 阶段后：检查己方队伍成员是否在本地已保存的标记字典中，
/// 若命中则在选人队伍聊天室中发送提醒信息（[Yuumi] 队友 {name} 历史标记: {tag}）。
/// </summary>
public sealed class AutoTagReminderAgent : IAutomationAgent
{
    private const int SessionPollMaxAttempts = 8;
    private const int ConversationPollMaxAttempts = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ChatRoomBufferDelay = TimeSpan.FromSeconds(2);

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Func<AppConfig> _configProvider;
    private readonly SavedPlayersStore _savedPlayersStore;
    private readonly AgentSupervisor _supervisor;
    private readonly AgentLifecycle _lifecycle;
    private readonly ILogger<AutoTagReminderAgent>? _logger;

    private readonly object _stateLock = new();
    private bool _isRunning;
    private long _lastReportedMatchEpoch;

    public string Name => "AutoTagReminderAgent";
    public bool IsRunning => _isRunning;

    public AutoTagReminderAgent(
        Func<ILcuTransport?> transportProvider,
        Func<AppConfig> configProvider,
        SavedPlayersStore savedPlayersStore,
        AgentSupervisor supervisor,
        AgentLifecycle lifecycle,
        ILogger<AutoTagReminderAgent>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(savedPlayersStore);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(lifecycle);

        _transportProvider = transportProvider;
        _configProvider = configProvider;
        _savedPlayersStore = savedPlayersStore;
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
        }

        _logger?.LogInformation("选人标记提醒代理已启动");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _isRunning = false;
        }

        _logger?.LogInformation("选人标记提醒代理已停止");
        return Task.CompletedTask;
    }

    public void OnPhaseChanged(GamePhase current)
    {
        if (!_isRunning) return;

        if (current != GamePhase.ChampSelect) return;

        var config = _configProvider();
        if (!config.Functions.EnableAutoTagReminder) return;

        var epoch = _lifecycle.Snapshot().MatchEpoch;
        lock (_stateLock)
        {
            if (_lastReportedMatchEpoch == epoch) return;
            _lastReportedMatchEpoch = epoch;
        }

        _ = _supervisor.TrySpawnAsync(
            TaskKind.AutoTagReminder,
            "SendTagReminder",
            async ct => await ExecuteTagReminderAsync(ct));
    }

    private async Task ExecuteTagReminderAsync(CancellationToken ct)
    {
        var transport = _transportProvider();
        if (transport is null) return;

        // 1. 获取当前自身的 PUUID
        string selfPuuid = string.Empty;
        var meRes = await transport.SendAsync(LcuRequest.Get("/lol-summoner/v1/current-summoner"), ct).ConfigureAwait(false);
        if (meRes.IsSuccess && !string.IsNullOrWhiteSpace(meRes.Body))
        {
            using var doc = JsonDocument.Parse(meRes.Body);
            if (doc.RootElement.TryGetProperty("puuid", out var pProp) && pProp.ValueKind == JsonValueKind.String)
            {
                selfPuuid = pProp.GetString() ?? string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(selfPuuid)) return;

        // 2. 查询当前账号所有带标记的玩家映射 (puuid -> tag)
        var taggedMap = await _savedPlayersStore.GetTaggedPlayersAsync(selfPuuid, ct).ConfigureAwait(false);
        if (taggedMap.Count == 0)
        {
            _logger?.LogDebug("无标记玩家，跳过选人提醒");
            return;
        }

        // 3. 轮询选人会话就绪
        JsonElement? session = null;
        JsonDocument? sessionDoc = null;
        for (int i = 0; i < SessionPollMaxAttempts; i++)
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            var res = await transport.SendAsync(LcuRequest.Get("/lol-champ-select/v1/session"), ct).ConfigureAwait(false);
            if (res.IsSuccess && !string.IsNullOrWhiteSpace(res.Body))
            {
                sessionDoc = JsonDocument.Parse(res.Body);
                if (sessionDoc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    session = sessionDoc.RootElement;
                    break;
                }
            }
        }

        using (sessionDoc)
        {
            if (session is null) return;

            // 4. 检查己方队伍成员
            if (!session.Value.TryGetProperty("myTeam", out var myTeam) || myTeam.ValueKind != JsonValueKind.Array) return;

            var matches = new List<(string Name, string Tag)>();
            foreach (var member in myTeam.EnumerateArray())
            {
                string memberPuuid = member.TryGetProperty("puuid", out var puuidProp) && puuidProp.ValueKind == JsonValueKind.String
                    ? puuidProp.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(memberPuuid) || string.Equals(memberPuuid, selfPuuid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (taggedMap.TryGetValue(memberPuuid, out var tag) && !string.IsNullOrWhiteSpace(tag))
                {
                    string name = member.TryGetProperty("summonerName", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                        ? nameProp.GetString() ?? string.Empty
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = "队友";
                    }

                    matches.Add((name, tag));
                }
            }

            if (matches.Count == 0) return;

            // 5. 查找选人聊天会话 ID
            string? roomHint = null;
            if (session.Value.TryGetProperty("chatDetails", out var chatDetails) && chatDetails.ValueKind == JsonValueKind.Object)
            {
                if (chatDetails.TryGetProperty("chatRoomName", out var crn) && crn.ValueKind == JsonValueKind.String)
                {
                    roomHint = crn.GetString();
                }
                else if (chatDetails.TryGetProperty("multiUserChatId", out var muc) && muc.ValueKind == JsonValueKind.String)
                {
                    roomHint = muc.GetString();
                }
            }

            var convId = await WaitForChampSelectConvIdAsync(transport, roomHint, ConversationPollMaxAttempts, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(convId))
            {
                _logger?.LogWarning("选人标记提醒：未找到选人聊天室会话");
                return;
            }

            await Task.Delay(ChatRoomBufferDelay, ct).ConfigureAwait(false);

            // 6. 发送提醒
            foreach (var (name, tag) in matches)
            {
                var bodyJson = JsonSerializer.Serialize(new
                {
                    body = $"[Yuumi] 队友 {name} 历史标记: {tag}",
                    type = "chat",
                });

                var postReq = LcuRequest.Post($"/lol-chat/v1/conversations/{convId}/messages", bodyJson);
                var postRes = await transport.SendAsync(postReq, ct).ConfigureAwait(false);
                if (postRes.IsSuccess)
                {
                    _logger?.LogInformation("已在队伍聊天播报标记提醒：队友 {Name} -> {Tag}", name, tag);
                }
            }
        }
    }

    private static async Task<string?> WaitForChampSelectConvIdAsync(
        ILcuTransport transport,
        string? roomHint,
        int maxAttempts,
        CancellationToken ct)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            if (i > 0) await Task.Delay(PollInterval, ct).ConfigureAwait(false);

            var res = await transport.SendAsync(LcuRequest.Get("/lol-chat/v1/conversations"), ct).ConfigureAwait(false);
            if (!res.IsSuccess || string.IsNullOrWhiteSpace(res.Body)) continue;

            using var doc = JsonDocument.Parse(res.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

            if (!string.IsNullOrWhiteSpace(roomHint))
            {
                foreach (var conv in doc.RootElement.EnumerateArray())
                {
                    string id = conv.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
                    string name = conv.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                    if (id.Contains(roomHint, StringComparison.OrdinalIgnoreCase) || string.Equals(name, roomHint, StringComparison.OrdinalIgnoreCase))
                    {
                        return id;
                    }
                }
            }

            foreach (var conv in doc.RootElement.EnumerateArray())
            {
                string type = conv.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? string.Empty : string.Empty;
                string id = conv.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;

                if (string.Equals(type, "championSelect", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "customGame", StringComparison.OrdinalIgnoreCase) ||
                    id.Contains("champ-select", StringComparison.OrdinalIgnoreCase) ||
                    id.Contains("champSelect", StringComparison.OrdinalIgnoreCase))
                {
                    return id;
                }
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
