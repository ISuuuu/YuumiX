using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Common;

namespace YuumiX.Core.Lcu;

/// <summary>
/// 一条 LCU WebSocket 事件，对应 WAMP 帧 <c>[8, "OnJsonApiEvent_xxx", { uri, eventType, data }]</c> 的第三个元素。
///
/// <see cref="Data"/> 是**未裁剪**的原始载荷（ADR-10 决策二）：原项目的 <c>event_projection.rs</c>
/// 只为压缩跨进程 IPC 体积，单进程后由各 Feature 的解析器自行反序列化，事件流不做字段投影。
/// 该元素已通过 <see cref="JsonElement.Clone"/> 脱离 <see cref="JsonDocument"/>，
/// 可安全跨线程持有，调用方无需关心文档生命周期。
/// </summary>
public sealed record LcuEvent(string Uri, string EventType, JsonElement Data)
{
    /// <summary><c>eventType</c> 缺失时的默认值，与原实现 <c>unwrap_or("Update")</c> 一致。</summary>
    public const string DefaultEventType = "Update";
}

/// <summary>
/// LCU 的 WebSocket 事件流，对应原 <c>lcu/ws.rs</c> 的 <c>try_connect</c> + <c>handle_messages</c>。
///
/// 职责边界：<b>一次</b>连接的生命周期——连接、订阅、读循环、解析、过滤、去重、下发。
/// **不含重连**：重连策略属于 <see cref="LcuConnection"/>，理由见下。
///
/// 为什么把重连上移一层：原实现的 WS 循环只重试**同一个** port/token，靠 monitor 发现客户端重启后
/// 再调 <c>connect()</c> 换掉旧循环。两个循环并存意味着一份重试逻辑永远在为自己看不见的变化空转。
/// 这里改为由连接层用「发现 → 探活 → 建流」这一条顺序循环承载重连：客户端重启导致端口变化时
/// 天然拿到新凭据，且重连等待时间由探活耗时决定（毫秒级），比固定 2 秒更快。
///
/// 四条必须保住的既有行为：
/// <list type="number">
///   <item><b>WSS + Basic 认证</b>：用户名固定 <c>riot</c>，与 <see cref="LcuCredentials.AuthorizationHeader"/> 同源；</item>
///   <item><b>忽略自签证书</b>：LCU 的证书不在任何信任链上，等价于原实现的 <c>ssl=False</c>；</item>
///   <item><b>只放行 5 个 URI</b>：其余事件即使到达也不解析、不下发（对齐原 <c>WATCHED_URIS</c>）；</item>
///   <item><b>按 URI 去重</b>：无人操作时 LCU 仍持续推送相同快照，内容未变不下发（ADR-10 保留项）。
///         去重表按连接新建，重连后的首个事件一定下发。</item>
/// </list>
///
/// <b>不节流</b>（ADR-10 决策三）：原实现的 50 ms <c>SESSION_THROTTLE_MS</c> 服务于 IPC 载荷，
/// 单进程下收益消失，而丢帧会让「事件流为何少了一拍」无法复现。需要合并时在消费侧做。
/// </summary>
public sealed class LcuEventStream : IAsyncDisposable
{
    /// <summary>
    /// 订阅帧。对齐 Python <c>LcuWebSocket</c> 的 <c>[5, "OnJsonApiEvent"]</c>——
    /// 订阅的是整个事件通道，URI 过滤在客户端侧完成（LCU 不提供按 URI 订阅的 WAMP 语义）。
    /// </summary>
    public const string SubscribeMessage = "[5, \"OnJsonApiEvent\"]";

    /// <summary>单帧读取缓冲区。选人会话满编约 60–120 KB，靠多帧拼装而非放大单次读取。</summary>
    private const int ReceiveBufferSize = 16 * 1024;

    /// <summary>单条消息上限。超过即视为协议异常并丢弃，避免异常对端把内存吃光。</summary>
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    private static readonly byte[] SubscribeBytes = Encoding.UTF8.GetBytes(SubscribeMessage);

    /// <summary>
    /// 关心的 URI 前缀。与原 <c>ws.rs</c> 的 <c>WATCHED_URIS</c> 逐条对齐；
    /// 全部落在 <see cref="LcuApiCatalog"/> 的白名单前缀内（有单测守住这条关系）。
    /// 用前缀而非全等匹配：LCU 对同一资源可能推送带后缀的变体。
    /// </summary>
    private static readonly string[] WatchedUriList =
    [
        "/lol-gameflow/v1/gameflow-phase",
        "/lol-gameflow/v1/session",
        "/lol-champ-select/v1/session",
        "/lol-champ-select/v1/current-champion",
        "/lol-matchmaking/v1/ready-check",
    ];

    private readonly Uri _endpoint;
    private readonly string _authorizationHeader;
    private readonly ILogger<LcuEventStream>? _logger;
    private readonly EventStream<LcuEvent> _events;

    private int _disposed;

    /// <param name="baseAddress">
    /// LCU 根地址，<c>https://127.0.0.1:&lt;port&gt;/</c>。
    /// 刻意同时接受 <c>http://</c>：测试用 <c>FakeLcuServer</c> 是明文回环服务，
    /// 若只接受凭据对象，WS 这条路径就完全无法回归。
    /// </param>
    /// <param name="authorizationHeader">完整的 Authorization 头值。</param>
    /// <param name="logger">可选日志。</param>
    public LcuEventStream(Uri baseAddress, string authorizationHeader, ILogger<LcuEventStream>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationHeader);

        _endpoint = ToWebSocketEndpoint(baseAddress);
        _authorizationHeader = authorizationHeader;
        _logger = logger;

        // 观察者异常必须在这里被吃掉并记录：EventStream 不会中断投递，但静默吞掉会让
        // 「UI 不再更新」这类问题失去唯一线索。
        _events = new EventStream<LcuEvent>(ex => logger?.LogWarning(ex, "LCU 事件观察者抛出异常，已隔离"));
    }

    /// <summary>事件流。订阅方在任意线程被同步回调，UI 侧需自行封送（ADR-07）。</summary>
    public EventStream<LcuEvent> Events => _events;

    /// <summary>实际连接的 WebSocket 端点，便于日志与诊断。</summary>
    public Uri Endpoint => _endpoint;

    /// <summary>关心的 URI 前缀（只读视图）。</summary>
    public static IReadOnlyList<string> WatchedUris => WatchedUriList;

    /// <summary>按凭据构造。生产代码应走这个入口，以保留证书忽略与绕代理的配置。</summary>
    public static LcuEventStream ForCredentials(LcuCredentials credentials, ILogger<LcuEventStream>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return new LcuEventStream(credentials.BaseAddress, credentials.AuthorizationHeader, logger);
    }

    /// <summary>
    /// 运行一次完整连接：握手 → 订阅 → 读循环，直到对端关闭、连接出错或令牌取消。
    /// 正常断开与连接失败都以异常/正常返回结束，**不在此处重试**。
    /// </summary>
    /// <exception cref="WebSocketException">握手失败（凭据失效、客户端正在退出等）。</exception>
    /// <exception cref="OperationCanceledException">令牌取消。</exception>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        using var socket = new ClientWebSocket();
        ConfigureOptions(socket.Options);

        try
        {
            await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            // 握手阶段的 HTTP 状态是排障的关键信息（403 多为 token 失效、404 多为客户端未就绪），
            // 而 WebSocketException 默认消息只有一句「无法连接到远程服务器」。
            _logger?.LogWarning(
                "LCU WebSocket 握手失败: {Endpoint}，HTTP 状态={StatusCode}，原因={Reason}",
                _endpoint,
                (int)socket.HttpStatusCode,
                ex.Message);
            throw;
        }

        _logger?.LogInformation("LCU WebSocket 已连接: {Endpoint}", _endpoint);

        await socket
            .SendAsync(SubscribeBytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);

        _logger?.LogDebug("LCU WebSocket 已发送订阅: {Subscribe}", SubscribeMessage);

        // 去重表按连接创建：重连后清空，保证重连后的首个事件一定下发（与原实现一致）
        var dedup = new Dictionary<string, string>(StringComparer.Ordinal);

        using var pending = new MemoryStream(ReceiveBufferSize);
        var buffer = new byte[ReceiveBufferSize];

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                _logger?.LogInformation(
                    "LCU WebSocket 收到关闭帧: {Status} {Description}",
                    result.CloseStatus,
                    result.CloseStatusDescription);
                break;
            }

            pending.Write(buffer, 0, result.Count);

            if (pending.Length > MaxMessageBytes)
            {
                _logger?.LogWarning("LCU WebSocket 单条消息超过 {Limit} 字节上限，已丢弃", MaxMessageBytes);
                pending.SetLength(0);
                continue;
            }

            if (!result.EndOfMessage)
            {
                continue;
            }

            Process(pending.GetBuffer().AsMemory(0, (int)pending.Length), dedup);
            pending.SetLength(0);
        }

        // 循环条件本身会挡住「令牌已取消」的入口，此时正常返回等于把取消伪装成
        // 「对端正常关闭」。而本方法的契约（见 XML 注释）是令牌取消抛
        // OperationCanceledException，调用方 LcuConnection 也按此区分「客户端退出」与
        // 「连接断开需重连」。取消发生在「一次事件处理完毕、尚未回到 ReceiveAsync」的
        // 窗口内就会走到这里，必须显式抛出，否则该次取消被静默吞掉。
        cancellationToken.ThrowIfCancellationRequested();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _events.Complete();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 匹配关心的 URI，返回命中的**前缀**（而非完整 URI）。
    /// 去重按前缀分桶：同一前缀下的不同变体共用一格，与原实现 <c>dedup.accept(watched, ...)</c> 一致。
    /// </summary>
    public static string? MatchWatchedUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return null;
        }

        foreach (var prefix in WatchedUriList)
        {
            if (uri.StartsWith(prefix, StringComparison.Ordinal))
            {
                return prefix;
            }
        }

        return null;
    }

    /// <summary>
    /// 解析一帧。解析失败一律静默丢弃——LCU 在关闭过程中会发出不完整的帧，
    /// 为它抛异常只会让读循环提前退出，反而丢掉后续正常事件。
    /// </summary>
    private void Process(ReadOnlyMemory<byte> payload, Dictionary<string, string> dedup)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            _logger?.LogDebug(ex, "LCU WebSocket 帧不是合法 JSON，已忽略（{Length} 字节）", payload.Length);
            return;
        }

        using (document)
        {
            var root = document.RootElement;

            // WAMP 帧：[8, "OnJsonApiEvent_xxx", {...}]。其他 opcode（如 [1,...] 欢迎帧）不属于事件。
            if (root.ValueKind != JsonValueKind.Array
                || root.GetArrayLength() < 3
                || !root[0].TryGetInt32(out var opcode)
                || opcode != 8)
            {
                return;
            }

            var body = root[2];
            if (body.ValueKind != JsonValueKind.Object
                || !body.TryGetProperty("uri", out var uriElement)
                || uriElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var uri = uriElement.GetString();
            if (uri is null)
            {
                return;
            }

            var watched = MatchWatchedUri(uri);
            if (watched is null)
            {
                return;
            }

            // data 可能是 Null（DELETE 事件的原实现也会下发 data:null），此时保留 null 元素
            var data = body.TryGetProperty("data", out var dataElement)
                ? dataElement.Clone()
                : JsonSerializer.SerializeToElement<object?>(null);

            var eventType = body.TryGetProperty("eventType", out var eventTypeElement)
                            && eventTypeElement.ValueKind == JsonValueKind.String
                ? eventTypeElement.GetString() ?? LcuEvent.DefaultEventType
                : LcuEvent.DefaultEventType;

            // 去重比较原始 JSON 文本。JSON 键顺序在同一 LCU 进程内稳定，
            // 因此文本比较等价于内容比较，且省掉一次序列化。
            var raw = data.GetRawText();
            if (dedup.TryGetValue(watched, out var previous) && string.Equals(previous, raw, StringComparison.Ordinal))
            {
                return;
            }

            dedup[watched] = raw;

            _logger?.LogDebug("LCU 事件: {Uri} ({EventType})", uri, eventType);
            _events.Publish(new LcuEvent(uri, eventType, data));
        }
    }

    private void ConfigureOptions(ClientWebSocketOptions options)
    {
        options.SetRequestHeader("Authorization", _authorizationHeader);

        // 与原实现一致：握手请求声明 JSON 期望。LCU 不校验，但省掉排障时的「是不是没带 Accept」疑问。
        options.SetRequestHeader("Content-Type", "application/json");
        options.SetRequestHeader("Accept", "application/json");

        // 忽略自签证书，等价于 rustls 的 NoVerifier / reqwest 的 danger_accept_invalid_certs
        options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;

        // 显式禁用代理。这是 LcuHttpTransport.UseProxy = false 在 WS 侧的同等约束：
        // 本机系统代理在 127.0.0.1:7897，回环请求一旦交给代理就必然连不上，
        // 且失败信息是「无法连接到远程服务器」，与「客户端没开」无法区分。
        // 不能写成 Proxy = null —— 那在 .NET 上表示「沿用系统默认代理」；
        // 无地址的 WebProxy 会让 GetProxy 返回目标本身，即直连，两种解读下行为都正确。
        options.Proxy = new WebProxy();

        // 心跳刻意关闭。原实现（tungstenite / aiohttp）都没有心跳，LCU 是否回应 pong 未经验证；
        // 一旦它不回，.NET 会在两个心跳周期后主动断开，表现为「连上几十秒就掉线」——
        // 这比「半开连接要等到下次发请求才暴露」严重得多。待真实客户端联调确认后再决定是否开启。
        options.KeepAliveInterval = TimeSpan.Zero;
    }

    /// <summary>
    /// 把 LCU 的 HTTP 根地址映射为 WebSocket 端点。
    /// 路径固定为 <c>/</c>：LCU 的 WS 服务只在根路径接受升级请求。
    /// </summary>
    private static Uri ToWebSocketEndpoint(Uri baseAddress)
    {
        var scheme = baseAddress.Scheme switch
        {
            "https" => "wss",
            "http" => "ws",
            "wss" => "wss",
            "ws" => "ws",
            _ => throw new ArgumentException($"不支持的 LCU 地址协议: {baseAddress.Scheme}", nameof(baseAddress)),
        };

        var builder = new UriBuilder(baseAddress)
        {
            Scheme = scheme,
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return builder.Uri;
    }
}
