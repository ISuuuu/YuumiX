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
    private readonly List<WebSocket> _webSockets = [];
    private readonly List<WebSocket> _readyWebSockets = [];
    private readonly List<TaskCompletionSource<WebSocket>> _webSocketWaiters = [];
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

    /// <summary>WebSocket 连接建立后的回调（用于推送事件）。返回即关闭该连接。</summary>
    public Func<WebSocket, Task>? WebSocketHandler { get; set; }

    /// <summary>已接受的 WebSocket 连接数，用于断言重连行为。</summary>
    public int WebSocketConnectionCount
    {
        get
        {
            lock (_webSockets)
            {
                return _webSockets.Count;
            }
        }
    }

    /// <summary>
    /// 等待下一个 WebSocket 连接，返回服务端一侧的套接字供测试直接驱动。
    ///
    /// 与 <see cref="WebSocketHandler"/> 的分工：handler 适合「连上就自动推一串帧」的脚本式场景；
    /// 本方法适合「由测试逐步控制」的场景（先断言订阅帧，再逐条推事件，最后断开）。
    /// 两者可同时使用——套接字会先交给等待者，再交给 handler。
    /// </summary>
    public Task<WebSocket> NextWebSocketAsync(CancellationToken cancellationToken = default)
    {
        lock (_webSockets)
        {
            if (_readyWebSockets.Count > 0)
            {
                var pending = _readyWebSockets[0];
                _readyWebSockets.RemoveAt(0);
                return Task.FromResult(pending);
            }

            var waiter = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            _webSocketWaiters.Add(waiter);

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            }

            return waiter.Task;
        }
    }

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

        // 先掐断 WS：读循环阻塞在 ReceiveAsync 上，不主动 Abort 的话连接任务不会结束，
        // 下面的 WhenAll 会一直等到测试超时。
        WebSocket[] sockets;
        lock (_webSockets)
        {
            sockets = [.. _webSockets];
            _webSockets.Clear();
        }

        foreach (var socket in sockets)
        {
            socket.Abort();
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

                if (isWebSocketUpgrade)
                {
                    await AcceptWebSocketAsync(
                        stream,
                        raw.Headers.GetValueOrDefault("Sec-WebSocket-Key"),
                        raw.Leftover,
                        _shutdown.Token);
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

    private async Task AcceptWebSocketAsync(
        NetworkStream stream,
        string? webSocketKey,
        byte[] leftover,
        CancellationToken cancellationToken)
    {
        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes((webSocketKey ?? string.Empty) + WebSocketGuid)));

        var handshake = "HTTP/1.1 101 Switching Protocols\r\n" +
                        "Upgrade: websocket\r\n" +
                        "Connection: Upgrade\r\n" +
                        $"Sec-WebSocket-Accept: {accept}\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        // 客户端若把首帧与握手写在同一个包内，握手读取会把它一并读走。不回放这些字节，
        // 首帧（通常正是订阅消息）就会静默丢失，表现为「偶发收不到任何事件」——
        // 这类 flaky 的排查成本远高于这一层包装。
        var transport = leftover.Length == 0 ? (Stream)stream : new PrefixedStream(leftover, stream);

        using var socket = WebSocket.CreateFromStream(transport, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        PublishWebSocket(socket);

        try
        {
            if (WebSocketHandler is not null)
            {
                await WebSocketHandler(socket);
            }
            else
            {
                // 无 handler 时由测试直接驱动套接字：保持连接直到服务端关停。
                // 若此处直接返回，socket 会被释放，测试还没来得及读订阅帧连接就断了。
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 服务端关停
        }
    }

    private void PublishWebSocket(WebSocket socket)
    {
        lock (_webSockets)
        {
            _webSockets.Add(socket);

            if (_webSocketWaiters.Count > 0)
            {
                var waiter = _webSocketWaiters[0];
                _webSocketWaiters.RemoveAt(0);
                waiter.TrySetResult(socket);
            }
            else
            {
                _readyWebSockets.Add(socket);
            }
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

        // 超出 Content-Length 的字节属于后续帧（WebSocket 首帧最典型），必须原样还给上层
        var leftover = body.Count > contentLength
            ? body.GetRange(contentLength, body.Count - contentLength).ToArray()
            : [];

        var request = new RecordedRequest(
            method,
            target,
            headers.GetValueOrDefault("Authorization"),
            bodyText,
            headers.GetValueOrDefault("Content-Type"));
        return new RawRequest(request, headers, leftover);
    }

    /// <summary>
    /// 一次请求的原始形态：对外快照 + 内部需要的头部（WebSocket 握手要用）+
    /// 已被读入缓冲但属于后续帧的字节。
    /// </summary>
    private sealed record RawRequest(
        RecordedRequest Request,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Leftover);

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

    /// <summary>
    /// 先吐出「握手时已被读入缓冲、但归属于后续帧」的字节，再转发给底层流。
    /// <see cref="WebSocket.CreateFromStream"/> 没有注入前缀字节的入口，只能靠这层薄包装。
    /// </summary>
    private sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (TryReadPrefix(buffer, out var read))
            {
                return read;
            }

            return inner.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (TryReadPrefix(buffer.Span, out var read))
            {
                return new ValueTask<int>(read);
            }

            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.WriteAsync(buffer, cancellationToken);

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }

        /// <summary>从回放缓冲取数据；返回 <c>false</c> 表示缓冲已空，应转向底层流读取。</summary>
        private bool TryReadPrefix(Span<byte> buffer, out int read)
        {
            if (_offset < prefix.Length && buffer.Length > 0)
            {
                var take = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsSpan(_offset, take).CopyTo(buffer);
                _offset += take;
                read = take;
                return true;
            }

            read = 0;
            return false;
        }
    }
}
