using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using YuumiX.Core.Common;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;
using YuumiX.Core.Upload;

namespace YuumiX.Core.Realtime;

/// <summary>SignalR 连接状态（四态，对齐旧前端 signalr-status 徽标）。</summary>
public enum SignalrConnectionState
{
    /// <summary>未连接（未启用或已停止）。</summary>
    Disconnected,

    /// <summary>正在建立连接（含重连尝试）。</summary>
    Connecting,

    /// <summary>已连接并完成握手。</summary>
    Connected,

    /// <summary>连接失败（error 态，附错误详情）。</summary>
    Error,
}

/// <summary>状态快照：四态 + error 态的红字详情（对齐旧「连接异常： {payload}」）。</summary>
public sealed record SignalrStatus(SignalrConnectionState State, string? Error = null);

/// <summary>
/// LCU 实时查询的 SignalR Hub 服务（对应旧 <c>signalr.rs</c>）。
///
/// 语义对齐：
/// <list type="number">
///   <item>Hub 地址由 <see cref="UploadUrlBuilder.BuildHubBaseUrl"/> 从 <c>General.UploadApiUrl</c>
///         剥后缀再拼 <c>/lcuHub</c>（旧 clean_server_url 规则）——该字段同时是上传 API 与 Hub 基址；</item>
///   <item>userId 解析顺序：<c>General.SignalrUserId</c> → <c>Functions.LcuUserId</c> → 兜底 <c>lcu_user_001</c>；</item>
///   <item>连接成功后向 Hub 发送心跳（target=Heartbeat，30s 间隔）并推送召唤师对齐信息；</item>
///   <item>收到 <c>ReceiveCommand</c> 时经 LCU 端点白名单校验后执行 GET 并以
///         <c>ReportResult(userId, payload, queryId)</c> 回传（白名单与旧实现逐字一致）；</item>
///   <item>状态经 <see cref="EventStream{T}"/> 推送（ADR-10），订阅方（设置页徽标）threadSafe 消费。</item>
/// </list>
///
/// 实现差异（报告已注明）：旧实现用 tokio-tungstenite 手写 SignalR JSON 协议（协商 + 握手 + 分帧），
/// 这里换用官方 <c>Microsoft.AspNetCore.SignalR.Client</c>（10.0.13，net10.0 兼容），
/// 协商/握手/心跳 Ping/自动重连由库承担；连接建立失败后的 5 秒重试循环保留在服务内。
/// </summary>
public sealed class SignalrService : IDisposable
{
    /// <summary>允许远程查询的 LCU 端点白名单前缀（旧 ALLOWED_PREFIXES 原样迁移）。</summary>
    public static readonly string[] AllowedPrefixes =
    [
        "/lol-summoner/",
        "/lol-match-history/",
        "/lol-ranked/",
        "/lol-champ-select/",
        "/lol-gameflow/",
        "/lol-game-queues/",
        "/lol-perks/",
        "/lol-game-data/",
    ];

    private const string DefaultUserId = "lcu_user_001";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Func<AppConfig> _configProvider;
    private readonly ILogger<SignalrService>? _logger;
    private readonly EventStream<SignalrStatus> _statusStream = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _connection;
    private CancellationTokenSource? _cts;
    private Task? _retryLoop;
    private bool _disposed;

    /// <summary>当前状态（设置页初始回显用）。</summary>
    public SignalrStatus Current { get; private set; } = new(SignalrConnectionState.Disconnected);

    /// <summary>状态流（ADR-10：Core 暴露可订阅源，UI 用 hook 消费）。</summary>
    public EventStream<SignalrStatus> Status => _statusStream;

    public SignalrService(
        Func<ILcuTransport?> transportProvider,
        Func<AppConfig> configProvider,
        ILogger<SignalrService>? logger = null)
    {
        _transportProvider = transportProvider;
        _configProvider = configProvider;
        _logger = logger;
    }

    /// <summary>解析生效的 userId（旧后端解析顺序）。</summary>
    public static string ResolveUserId(AppConfig config) =>
        !string.IsNullOrWhiteSpace(config.General.SignalrUserId)
            ? config.General.SignalrUserId
            : (!string.IsNullOrWhiteSpace(config.Functions.LcuUserId)
                ? config.Functions.LcuUserId
                : DefaultUserId);

    /// <summary>LCU 端点是否在远程查询白名单内（旧 is_endpoint_allowed）。</summary>
    public static bool IsEndpointAllowed(string endpoint)
    {
        if (endpoint.Length == 0 || endpoint.Contains("..") || endpoint.Contains("//"))
        {
            return false;
        }

        foreach (string prefix in AllowedPrefixes)
        {
            if (endpoint.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 按当前配置应用云端服务状态（设置页写回后调用，对应旧 update_config 的差异副作用）：
    /// 实时开关开启且 URL 非空 → 连接（URL/userid 变化时重启）；否则 → 停止。
    /// </summary>
    public async Task ApplyConfigAsync()
    {
        var config = _configProvider();
        bool enabled = config.Functions.LcuRealtimeEnabled
                       && !string.IsNullOrWhiteSpace(config.General.UploadApiUrl);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!enabled)
            {
                await StopCoreAsync().ConfigureAwait(false);
                return;
            }

            string hubUrl = UploadUrlBuilder.BuildHubBaseUrl(config.General.UploadApiUrl);
            string userId = ResolveUserId(config);
            string desired = $"{hubUrl}?userId={userId}";

            if (_connection is not null && Current.State == SignalrConnectionState.Connected)
            {
                // 已连接的同目标连接不动（避免设置页无关字段写回触发重连）
                if (_activeTarget == desired)
                {
                    return;
                }
            }

            await RestartCoreAsync(hubUrl, userId, desired).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? _activeTarget;

    /// <summary>立即断开并按新目标重建（旧 update_config 的「重启 SignalR」分支）。</summary>
    private async Task RestartCoreAsync(string hubUrl, string userId, string target)
    {
        await StopCoreAsync().ConfigureAwait(false);

        _cts = new CancellationTokenSource();
        _activeTarget = target;

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            // 库级自动重连：断线后按默认策略重连；建连失败的 5 秒重试由 RetryLoop 兜底
            .WithAutomaticReconnect()
            .Build();

        connection.Reconnecting += _ =>
        {
            Publish(new SignalrStatus(SignalrConnectionState.Connecting));
            return Task.CompletedTask;
        };
        connection.Reconnected += _ =>
        {
            Publish(new SignalrStatus(SignalrConnectionState.Connected));
            return Task.CompletedTask;
        };
        connection.Closed += error =>
        {
            if (error is not null && !_cts.IsCancellationRequested)
            {
                Publish(new SignalrStatus(SignalrConnectionState.Error, error.Message));
            }
            else
            {
                Publish(new SignalrStatus(SignalrConnectionState.Disconnected));
            }

            return Task.CompletedTask;
        };

        // Hub 端 ReceiveCommand 的参数为 (commandJson, queryId)（旧 handle_receive_command 的 arguments[0]/[1]）
        connection.On<string, string>("ReceiveCommand", (command, queryId) =>
            _ = HandleReceiveCommandAsync(connection, userId, command, queryId));

        _connection = connection;
        _retryLoop = Task.Run(() => RetryLoopAsync(connection, userId, _cts.Token));
    }

    /// <summary>连接重试循环：建立失败 5 秒后重试（旧 start 循环同款），成功后驻留处理消息。</summary>
    private async Task RetryLoopAsync(HubConnection connection, string userId, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Publish(new SignalrStatus(SignalrConnectionState.Connecting));
                await connection.StartAsync(token).ConfigureAwait(false);

                Publish(new SignalrStatus(SignalrConnectionState.Connected));
                _logger?.LogInformation("[SignalR] 已连接到 Hub，userId={UserId}", userId);

                // 连接成功后推送召唤师对齐信息（旧 handle_connection 同款，延迟 1s 等 LCU 就绪）
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    await PushSummonerInfoAsync(connection, userId).ConfigureAwait(false);
                }, token);

                // 官方客户端自带心跳 Ping；这里补业务层 Heartbeat 目标（旧实现的心跳语义）
                _ = HeartbeatLoopAsync(connection, userId, token);

                // StartAsync 返回后 Closed 事件负责后续状态；此处驻留直到取消
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Publish(new SignalrStatus(SignalrConnectionState.Error, ex.Message));
                _logger?.LogError(ex, "[SignalR] 建立连接失败，{Delay} 秒后重试", RetryDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(RetryDelay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>业务层心跳（30s 间隔，旧 heartbeat 语义）。</summary>
    private async Task HeartbeatLoopAsync(HubConnection connection, string userId, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (connection.State == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
                {
                    await connection.SendAsync("Heartbeat", userId, token).ConfigureAwait(false);
                    _logger?.LogDebug("[SignalR] 心跳已发送");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[SignalR] 心跳循环退出");
        }
    }

    /// <summary>连接成功后查询当前召唤师并推送 summoner_info（旧 update_summoner_info）。</summary>
    private async Task PushSummonerInfoAsync(HubConnection connection, string userId)
    {
        try
        {
            var transport = _transportProvider();
            if (transport is null)
            {
                return;
            }

            var resp = await transport.SendAsync(
                LcuRequest.Get("/lol-summoner/v1/current-summoner"),
                CancellationToken.None).ConfigureAwait(false);
            if (!resp.IsSuccess || string.IsNullOrWhiteSpace(resp.Body))
            {
                return;
            }

            using var doc = JsonDocument.Parse(resp.Body);
            var root = doc.RootElement;
            var info = new Dictionary<string, object?>
            {
                ["summonerName"] = GetStringOrNull(root, "displayName"),
                ["summonerId"] = GetInt64OrNull(root, "summonerId"),
                ["puuid"] = GetStringOrNull(root, "puuid"),
                ["platformId"] = GetStringOrNull(root, "platformId"),
                ["level"] = GetInt64OrNull(root, "summonerLevel") ?? GetInt64OrNull(root, "level"),
                ["profileIconId"] = GetInt64OrNull(root, "profileIconId"),
            };

            await SendEventAsync(connection, userId, "summoner_info", info).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SignalR] 推送召唤师对齐信息失败");
        }
    }

    /// <summary>ReceiveCommand：白名单校验 → LCU GET → ReportResult 回传（旧 handle_receive_command）。</summary>
    private async Task HandleReceiveCommandAsync(HubConnection connection, string userId, string commandJson, string queryId)
    {
        string endpoint = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(commandJson);
            var root = doc.RootElement;
            endpoint = GetStringOrNull(root, "Endpoint") ?? GetStringOrNull(root, "endpoint") ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SignalR] ReceiveCommand 参数解析失败");
        }

        _logger?.LogInformation("[SignalR] <<< 收到后端远程命令: endpoint={Endpoint}, queryId={QueryId}", endpoint, queryId);

        string payload;
        if (!IsEndpointAllowed(endpoint))
        {
            _logger?.LogWarning("[SignalR] ReceiveCommand 拒绝执行未授权路径: {Endpoint}", endpoint);
            payload = """{"error":"endpoint 未授权"}""";
        }
        else
        {
            payload = await ExecuteLcuGetAsync(endpoint).ConfigureAwait(false);
        }

        try
        {
            await connection.InvokeAsync(
                "ReportResult", userId, payload, queryId,
                CancellationToken.None).ConfigureAwait(false);
            _logger?.LogInformation("[SignalR] >>> 成功发送结果回执给云端, queryId={QueryId}", queryId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SignalR] ReportResult 回执发送失败");
        }
    }

    /// <summary>经 LCU Transport 执行远程 GET（8 秒超时，旧 lcu_get + timeout 语义）。</summary>
    private async Task<string> ExecuteLcuGetAsync(string endpoint)
    {
        var transport = _transportProvider();
        if (transport is null)
        {
            return """{"error":"LCU 未连接"}""";
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var resp = await transport.SendAsync(LcuRequest.Get(endpoint), timeoutCts.Token).ConfigureAwait(false);
            return resp.IsSuccess
                ? resp.Body
                : $"{{\"error\":\"HTTP {resp.StatusCode}\"}}";
        }
        catch (OperationCanceledException)
        {
            return """{"error":"LCU 请求超时 (8s)"}""";
        }
        catch (Exception ex)
        {
            return $"{{\"error\":\"{ex.Message.Replace("\"", "'")}\"}}";
        }
    }

    /// <summary>向 Hub 发送云端事件（旧 send_event：target=ReportResult，载荷含 eventType/data/timestamp）。</summary>
    public async Task SendEventAsync(string eventType, object data)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is null)
            {
                throw new InvalidOperationException("SignalR 未连接");
            }

            var config = _configProvider();
            await SendEventAsync(_connection, ResolveUserId(config), eventType, data).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task SendEventAsync(HubConnection connection, string userId, string eventType, object data)
    {
        var payload = new
        {
            eventType,
            data,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        string queryId = Guid.NewGuid().ToString("N");
        await connection.InvokeAsync(
            "ReportResult", userId, JsonSerializer.Serialize(payload), queryId,
            CancellationToken.None).ConfigureAwait(false);
    }

    private void Publish(SignalrStatus status)
    {
        Current = status;
        _statusStream.Publish(status);
    }

    private async Task StopCoreAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
        }

        _retryLoop = null;

        if (_connection is not null)
        {
            try
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[SignalR] 关闭旧连接时出现异常（可忽略）");
            }

            _connection = null;
        }

        _cts = null;
        _activeTarget = null;
        Publish(new SignalrStatus(SignalrConnectionState.Disconnected));
    }

    /// <summary>停止连接（设置页关开关 / 应用关闭）。</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? GetStringOrNull(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? GetInt64OrNull(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        _ = Task.Run(async () =>
        {
            if (_connection is not null)
            {
                try
                {
                    await _connection.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 关闭异常无需传播
                }
            }
        });
        _statusStream.Complete();
    }
}
