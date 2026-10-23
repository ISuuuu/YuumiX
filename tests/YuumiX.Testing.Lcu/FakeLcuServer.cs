using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace YuumiX.Testing.Lcu;

/// <summary>服务端收到的一次请求快照，供断言使用。</summary>
public sealed record RecordedRequest(
    string Method,
    string Path,
    string? Authorization,
    string? Body,
    string? ContentType = null);

/// <summary>服务端要返回的响应。</summary>
public sealed record FakeResponse(int StatusCode, string Body = "", string ContentType = "application/json")
{
    public static FakeResponse Ok(string body = "{}") => new(200, body);

    public static FakeResponse NotFound() => new(404, "{\"message\":\"not found\"}");

    public static FakeResponse ServerError() => new(500, "{\"message\":\"boom\"}");
}

/// <summary>
/// 假 LCU 服务端：一个仅回环可达的极小 HTTP/1.1 + WebSocket 实现。
///
/// 为什么不用 <see cref="HttpListener"/>：它需要管理员权限或 <c>netsh http add urlacl</c>
/// 才能注册前缀，在别人机器/CI 上会以「拒绝访问」假失败。用 <see cref="TcpListener"/>
/// 自建只依赖普通套接字权限，行为完全可控。
///
/// 为什么不用 Kestrel：为测试引入 ASP.NET Core 依赖过重，且它默认不允许动态端口绑回环以外的场景。
///
/// 端口一律绑定 <c>0</c>（系统分配），因此多个 worktree / 多个测试并行时不会抢端口。
/// </summary>
public sealed class FakeLcuServer : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly List<Task> _connections = [];
    private readonly Task _acceptLoop;

    private FakeLcuServer(TcpListener listener, Uri baseAddress, string authorizationHeader)
    {
        _listener = listener;
        BaseAddress = baseAddress;
        AuthorizationHeader = authorizationHeader;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>服务根地址，形如 <c>http://127.0.0.1:54321/</c>。</summary>
    public Uri BaseAddress { get; }

    /// <summary>期望客户端携带的 Authorization 头，便于断言鉴权是否正确注入。</summary>
    public string AuthorizationHeader { get; }

    /// <summary>已收到的请求，按到达顺序。</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>请求处理器；为 <c>null</c> 时一律返回 200 <c>{}</c>。</summary>
    public Func<RecordedRequest, FakeResponse>? Handler { get; set; }

    /// <summary>WebSocket 连接建立后的回调（用于推送事件）。</summary>
    public Func<WebSocket, Task>? WebSocketHandler { get; set; }

    public static FakeLcuServer Start(string token = "test-token")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");
        var authorizationHeader = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{token}"));

        return new FakeLcuServer(listener, baseAddress, authorizationHeader);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();

        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // 已停止
        }

        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }

        Task[] pending;
        lock (_connections)
        {
            pending = [.. _connections];
        }

        try
        {
            await Task.WhenAll(pending);
        }
        catch (Exception)
        {
            // 关停期间的连接异常无需关心
        }

        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            var connection = Task.Run(() => HandleConnectionAsync(client));
            lock (_connections)
            {
                _connections.Add(connection);
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var raw = await ReadRequestAsync(stream, _shutdown.Token);
                if (raw is null)
                {
                    return;
                }

                lock (_requests)
                {
                    _requests.Add(raw.Request);
                }

                var isWebSocketUpgrade =
                    raw.Headers.TryGetValue("Upgrade", out var upgrade)
                    && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase);

                if (isWebSocketUpgrade && WebSocketHandler is not null)
                {
                    await AcceptWebSocketAsync(stream, raw.Headers.GetValueOrDefault("Sec-WebSocket-Key"), _shutdown.Token);
                    return;
                }

                var response = Handler?.Invoke(raw.Request) ?? FakeResponse.Ok();
                await WriteResponseAsync(stream, response, _shutdown.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
                // 客户端提前断开，测试里属正常
            }
        }
    }

    private async Task AcceptWebSocketAsync(NetworkStream stream, string? webSocketKey, CancellationToken cancellationToken)
    {
        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes((webSocketKey ?? string.Empty) + WebSocketGuid)));

        var handshake = "HTTP/1.1 101 Switching Protocols\r\n" +
                        "Upgrade: websocket\r\n" +
                        "Connection: Upgrade\r\n" +
                        $"Sec-WebSocket-Accept: {accept}\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        using var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        if (WebSocketHandler is not null)
        {
            await WebSocketHandler(socket);
        }
    }

    private async Task<RawRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var accumulated = new List<byte>();
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return null;
            }

            accumulated.AddRange(buffer.AsSpan(0, read));
            headerEnd = FindHeaderEnd(accumulated);
        }

        var headerText = Encoding.UTF8.GetString([.. accumulated.Take(headerEnd)]);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');

        var method = requestLine[0];
        var target = requestLine[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        var bodyStart = headerEnd + 4;
        var contentLength = headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var length)
            ? length
            : 0;

        var body = new List<byte>(accumulated.Skip(bodyStart));
        while (contentLength > 0 && body.Count < contentLength)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            body.AddRange(buffer.AsSpan(0, read));
        }

        var bodyText = body.Count == 0 ? null : Encoding.UTF8.GetString([.. body.Take(contentLength)]);

        var request = new RecordedRequest(
            method,
            target,
            headers.GetValueOrDefault("Authorization"),
            bodyText,
            headers.GetValueOrDefault("Content-Type"));
        return new RawRequest(request, headers);
    }

    /// <summary>一次请求的原始形态：对外快照 + 内部需要的头部（WebSocket 握手要用）。</summary>
    private sealed record RawRequest(RecordedRequest Request, IReadOnlyDictionary<string, string> Headers);

    private static int FindHeaderEnd(List<byte> data)
    {
        for (var i = 0; i + 3 < data.Count; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, FakeResponse response, CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(response.Body);
        var header = $"HTTP/1.1 {response.StatusCode} {ReasonPhrase(response.StatusCode)}\r\n" +
                     $"Content-Type: {response.ContentType}\r\n" +
                     $"Content-Length: {bodyBytes.Length}\r\n" +
                     "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Unknown",
    };
}
