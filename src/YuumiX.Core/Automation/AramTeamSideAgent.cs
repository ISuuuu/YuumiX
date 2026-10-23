using Microsoft.Extensions.Logging;
using System.Text.Json;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Automation;

/// <summary>
/// 大乱斗（ARAM）自动报边代理（对应旧版 agents/auto_match.rs::spawn_aram_team_side）。
/// 进入 ChampSelect 阶段后：优先从 pin-drop-notification 读取阵营，回退用选人格子位判定；
/// 等待聊天室完成入房后向选人聊天会话发送阵营播报消息。
/// </summary>
public sealed class AramTeamSideAgent : IAutomationAgent
{
    /// <summary>轮询选人会话的最多次数（每 500ms 一次）。</summary>
    private const int SessionPollMaxAttempts = 8;

    /// <summary>轮询选人聊天会话的最多次数（每 500ms 一次）。</summary>
    private const int ConversationPollMaxAttempts = 10;

    /// <summary>消息发送重试次数。</summary>
    private const int SendMaxAttempts = 3;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SendRetryInterval = TimeSpan.FromMilliseconds(600);

    /// <summary>聊天室完成入房的缓冲等待：避免消息在入房初始化前被冲刷覆盖。</summary>
    private static readonly TimeSpan ChatRoomBufferDelay = TimeSpan.FromSeconds(2);

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Func<AppConfig> _configProvider;
    private readonly AgentSupervisor _supervisor;
    private readonly AgentLifecycle _lifecycle;
    private readonly ILogger<AramTeamSideAgent>? _logger;

    private readonly object _stateLock = new();
    private bool _isRunning;
    private long _lastReportedMatchEpoch;

    public string Name => "AramTeamSideAgent";
    public bool IsRunning => _isRunning;

    public AramTeamSideAgent(
        Func<ILcuTransport?> transportProvider,
        Func<AppConfig> configProvider,
        AgentSupervisor supervisor,
        AgentLifecycle lifecycle,
        ILogger<AramTeamSideAgent>? logger = null)
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

        _logger?.LogInformation("ARAM 自动报边代理已启动");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _isRunning = false;
        }

        _supervisor.CancelKind(TaskKind.AramTeamSide);
        _logger?.LogInformation("ARAM 自动报边代理已停止");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 处理游戏阶段变化：进入 ChampSelect 时触发报边。
    /// </summary>
    public void OnPhaseChanged(GamePhase currentPhase)
    {
        if (!_isRunning) return;

        var cfg = _configProvider().Functions;
        if (!cfg.EnableAutoAramTeamSide) return;

        if (currentPhase != GamePhase.ChampSelect) return;

        // 同一对局（MatchEpoch 相同）只报边一次，避免选人期间重复事件导致刷屏。
        var epoch = _lifecycle.Snapshot().MatchEpoch;
        lock (_stateLock)
        {
            if (epoch == _lastReportedMatchEpoch) return;
            _lastReportedMatchEpoch = epoch;
        }

        TriggerReport(cfg.AramTeamSideVisibleToTeam);
    }

    private void TriggerReport(bool visibleToTeam)
    {
        _logger?.LogInformation("进入选人阶段，准备自动报边 (队友可见={VisibleToTeam})", visibleToTeam);

        _ = _supervisor.TrySpawnAsync(TaskKind.AramTeamSide, "AramTeamSide", async ct =>
        {
            var transport = _transportProvider();
            if (transport is null)
            {
                _logger?.LogWarning("LCU 传输层未连接，自动报边终止");
                return;
            }

            // 1. 轮询选人会话，直到拿到会话信息（选人初期会话可能尚未建立）。
            string? roomHint = null;
            int localPlayerCellId = int.MaxValue;
            bool sessionReady = false;
            for (int i = 0; i < SessionPollMaxAttempts && !ct.IsCancellationRequested; i++)
            {
                var sessionResp = await transport.SendAsync(
                    LcuRequest.Get("/lol-champ-select/v1/session"),
                    ct).ConfigureAwait(false);

                if (sessionResp.IsSuccess && TryParseSession(sessionResp.Body, out localPlayerCellId, out roomHint))
                {
                    sessionReady = true;
                    break;
                }

                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }

            if (!sessionReady)
            {
                _logger?.LogWarning("未能获取选人会话信息，放弃自动报边");
                return;
            }

            // 2. 判定阵营：优先 pin-drop-notification 的 mapSide，回退按格子位（<5 即蓝方）。
            string side = await ResolveMapSideAsync(transport, localPlayerCellId, ct).ConfigureAwait(false);
            string sideName = side == "blue" ? "蓝色方" : "红色方";
            _logger?.LogInformation("报边阵营判定: {Side} ({SideName})", side, sideName);

            // 3. 轮询查找已建立的选人聊天会话。
            string? convId = await WaitForChampSelectConversationAsync(transport, roomHint, ct).ConfigureAwait(false);
            if (convId is null)
            {
                _logger?.LogWarning("未找到选人聊天会话，放弃自动报边");
                return;
            }

            // 4. 缓冲等待聊天室完成入房，避免消息被覆盖。
            await Task.Delay(ChatRoomBufferDelay, ct).ConfigureAwait(false);

            // 5. 发送播报消息（type=chat 队友可见；celebration 仅本地可见）。
            string message = $"[YuumiX] 本局我方在{sideName}";
            var messageBody = JsonSerializer.Serialize(new
            {
                body = message,
                type = visibleToTeam ? "chat" : "celebration",
            });

            for (int attempt = 1; attempt <= SendMaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var sendResp = await transport.SendAsync(
                    LcuRequest.Post($"/lol-chat/v1/conversations/{convId}/messages", messageBody),
                    ct).ConfigureAwait(false);

                if (sendResp.IsSuccess)
                {
                    _logger?.LogInformation("报边消息发送成功: {Message}", message);
                    return;
                }

                _logger?.LogWarning("报边消息发送失败 (HTTP {StatusCode}, 第 {Attempt}/{SendMaxAttempts} 次)",
                    sendResp.StatusCode, attempt, SendMaxAttempts);
                if (attempt < SendMaxAttempts)
                {
                    await Task.Delay(SendRetryInterval, ct).ConfigureAwait(false);
                }
            }

            _logger?.LogWarning("报边消息 {SendMaxAttempts} 次尝试均失败", SendMaxAttempts);
        });
    }

    /// <summary>
    /// 解析选人会话 JSON，提取本方格子位与聊天室提示（chatRoomName / multiUserChatId）。
    /// </summary>
    private static bool TryParseSession(string body, out int localPlayerCellId, out string? roomHint)
    {
        localPlayerCellId = int.MaxValue;
        roomHint = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (root.TryGetProperty("localPlayerCellId", out var cellId) && cellId.ValueKind == JsonValueKind.Number)
            {
                localPlayerCellId = cellId.GetInt32();
            }

            if (root.TryGetProperty("chatDetails", out var chatDetails) && chatDetails.ValueKind == JsonValueKind.Object)
            {
                if (chatDetails.TryGetProperty("chatRoomName", out var roomName) && roomName.ValueKind == JsonValueKind.String)
                {
                    roomHint = roomName.GetString();
                }

                if (string.IsNullOrEmpty(roomHint)
                    && chatDetails.TryGetProperty("multiUserChatId", out var mucId) && mucId.ValueKind == JsonValueKind.String)
                {
                    roomHint = mucId.GetString();
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 解析 pin-drop-notification 的 mapSide；读取失败时回退按格子位判定。
    /// </summary>
    private async Task<string> ResolveMapSideAsync(ILcuTransport transport, int localPlayerCellId, CancellationToken ct)
    {
        var pinResp = await transport.SendAsync(
            LcuRequest.Get("/lol-champ-select/v1/pin-drop-notification"),
            ct).ConfigureAwait(false);

        if (pinResp.IsSuccess)
        {
            try
            {
                using var doc = JsonDocument.Parse(pinResp.Body);
                if (doc.RootElement.TryGetProperty("mapSide", out var mapSide) && mapSide.ValueKind == JsonValueKind.String)
                {
                    var value = mapSide.GetString()?.ToLowerInvariant();
                    if (value is "blue" or "red") return value;
                }
            }
            catch (JsonException)
            {
                // 落入回退判定
            }
        }

        return localPlayerCellId < 5 ? "blue" : "red";
    }

    /// <summary>
    /// 轮询聊天会话列表，查找已建立的选人聊天会话。
    /// 优先匹配聊天室提示（会话 id 包含 roomHint），回退匹配 champ-select/unifiedchatselect 类型会话。
    /// </summary>
    private async Task<string?> WaitForChampSelectConversationAsync(
        ILcuTransport transport, string? roomHint, CancellationToken ct)
    {
        for (int i = 0; i < ConversationPollMaxAttempts; i++)
        {
            var resp = await transport.SendAsync(
                LcuRequest.Get("/lol-chat/v1/conversations"),
                ct).ConfigureAwait(false);

            if (resp.IsSuccess)
            {
                string? convId = FindChampSelectConversationId(resp.Body, roomHint);
                if (convId is not null) return convId;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        return null;
    }

    private static string? FindChampSelectConversationId(string body, string? roomHint)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            string? fallback = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string? id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                if (string.IsNullOrEmpty(id)) continue;

                if (!string.IsNullOrEmpty(roomHint) && id!.Contains(roomHint, StringComparison.OrdinalIgnoreCase))
                {
                    return id;
                }

                if (fallback is null)
                {
                    string type = item.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                        ? typeEl.GetString() ?? string.Empty
                        : string.Empty;
                    if (type.Contains("champ-select", StringComparison.OrdinalIgnoreCase)
                        || id!.Contains("unifiedchatselect", StringComparison.OrdinalIgnoreCase))
                    {
                        fallback = id;
                    }
                }
            }

            return fallback;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }
}
