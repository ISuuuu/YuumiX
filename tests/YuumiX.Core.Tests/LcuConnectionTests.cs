using System.Net.WebSockets;
using System.Text;
using Xunit;
using YuumiX.Core.Common;
using YuumiX.Core.Lcu;
using YuumiX.Testing.Lcu;

namespace YuumiX.Core.Tests;

/// <summary>
/// 连接聚合：状态流转、探活前置、传输复用、事件透传、断线重连、退出收敛。
///
/// 进程发现走内存桩（无需真客户端），探活与事件流走真实回环套接字之外的注入点——
/// 探活用 <see cref="StubTransport"/>，事件流用 <see cref="FakeLcuServer"/>，
/// 两者都只在连接层可见，因此这里测的正是「聚合逻辑」而不是下层细节。
/// </summary>
public sealed class LcuConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Run_WithoutClient_PublishesSingleDisconnectedState()
    {
        var source = new FakeProcessSource(); // 无客户端进程
        var finder = new LcuProcessFinder(source, _ => null);

        using var connection = new LcuConnection(
            finder,
            retryDelay: TimeSpan.FromMilliseconds(20),
            eventStreamFactory: _ => throw new InvalidOperationException("无客户端时不应建立事件流"));

        var states = Collect(connection.States);

        using var cts = new CancellationTokenSource(Timeout);
        var run = connection.RunAsync(cts.Token);

        await WaitUntilAsync(() => StateCount(states, LcuConnectionStatus.Disconnected) >= 1, cts.Token);
        await Task.Delay(150, cts.Token); // 留出若干轮重试，验证状态不重复发布

        var snapshot = Snapshot(states);
        Assert.Single(snapshot);
        Assert.Equal(LcuConnectionStatus.Disconnected, snapshot[0].Status);
        Assert.Contains("客户端", snapshot[0].Reason);
        Assert.Null(connection.Transport);

        await StopAsync(cts, run);
    }

    [Fact]
    public async Task Run_ProbesReadinessPathUntilItSucceeds()
    {
        var source = new FakeProcessSource();
        var finder = NewFinder(source);
        var transport = new StubTransport(_ => new LcuResponse(404, """{"message":"not found"}"""));

        using var connection = new LcuConnection(
            finder,
            retryDelay: TimeSpan.FromMilliseconds(20),
            transportFactory: _ => transport,
            eventStreamFactory: _ => throw new InvalidOperationException("探活未通过时不应建立事件流"));

        using var cts = new CancellationTokenSource(Timeout);
        var run = connection.RunAsync(cts.Token);

        await WaitUntilAsync(() => transport.Calls >= 3, cts.Token);

        Assert.All(transport.Paths, path => Assert.Equal(LcuApiCatalog.ReadinessPath, path));
        Assert.Equal(LcuConnectionStatus.Disconnected, connection.CurrentState.Status);
        Assert.Contains("尚未就绪", connection.CurrentState.Reason);

        await StopAsync(cts, run);
    }

    /// <summary>同一份凭据只建一个传输层，否则每轮重试都会甩掉一个连接池。</summary>
    [Fact]
    public async Task Run_ReusesTransportWhileCredentialsUnchanged()
    {
        var source = new FakeProcessSource();
        var finder = NewFinder(source);
        var created = 0;

        using var connection = new LcuConnection(
            finder,
            retryDelay: TimeSpan.FromMilliseconds(20),
            transportFactory: _ =>
            {
                Interlocked.Increment(ref created);
                return new StubTransport(_ => new LcuResponse(503, "{}"));
            },
            eventStreamFactory: _ => throw new InvalidOperationException("探活未通过时不应建立事件流"));

        using var cts = new CancellationTokenSource(Timeout);
        var run = connection.RunAsync(cts.Token);

        await WaitUntilAsync(() => Volatile.Read(ref created) >= 1, cts.Token);
        await Task.Delay(150, cts.Token); // 期间会发生多次重试

        Assert.Equal(1, Volatile.Read(ref created));

        await StopAsync(cts, run);
    }

    [Fact]
    public async Task Run_PublishesConnectingThenConnectedAndForwardsEvents()
    {
        await using var server = FakeLcuServer.Start();
        var source = new FakeProcessSource();
        var finder = NewFinder(source);
        var transport = new StubTransport(_ => new LcuResponse(200, "[]"));

        using var connection = new LcuConnection(
            finder,
            retryDelay: TimeSpan.FromMilliseconds(20),
            transportFactory: _ => transport,
            eventStreamFactory: _ => new LcuEventStream(server.BaseAddress, server.AuthorizationHeader));

        var states = Collect(connection.States);
        using var probe = new EventProbe(connection.Events);
        using var cts = new CancellationTokenSource(Timeout);
        var run = connection.RunAsync(cts.Token);

        var connected = await WaitForStateAsync(connection, s => s.Status == LcuConnectionStatus.Connected, cts.Token);

        Assert.Equal(50000u, connected.Credentials!.Port);
        Assert.Equal("CN1", connected.Credentials.Server);
        Assert.Same(transport, connection.Transport);

        var sequence = Snapshot(states).Select(s => s.Status).ToArray();
        Assert.Contains(LcuConnectionStatus.Connecting, sequence);
        Assert.Equal(LcuConnectionStatus.Connected, sequence[^1]);

        // 事件必须经连接层透传到订阅方（订阅一次，跨重连有效）
        using var socket = await server.NextWebSocketAsync(cts.Token);
        await ReceiveTextAsync(socket, cts.Token);
        await SendFrameAsync(socket, """"[8,"OnJsonApiEvent",{"uri":"/lol-gameflow/v1/gameflow-phase","eventType":"Update","data":"InProgress"}]"""", cts.Token);

        var received = await probe.NextAsync(cts.Token);
        Assert.Equal("/lol-gameflow/v1/gameflow-phase", received.Uri);

        await StopAsync(cts, run);
    }

    /// <summary>
    /// 事件流断开后必须回到 Disconnected 并自动重连；服务端接受第二个连接即证明重连发生。
    /// </summary>
    [Fact]
    public async Task Run_WhenStreamDrops_ReturnsToDisconnectedAndReconnects()
    {
        await using var server = FakeLcuServer.Start();
        var source = new FakeProcessSource();
        var finder = NewFinder(source);

        using var connection = new LcuConnection(
            finder,
            retryDelay: TimeSpan.FromMilliseconds(20),
            transportFactory: _ => new StubTransport(_ => new LcuResponse(200, "[]")),
            eventStreamFactory: _ => new LcuEventStream(server.BaseAddress, server.AuthorizationHeader));

        var states = Collect(connection.States);

        using var cts = new CancellationTokenSource(Timeout);
        var run = connection.RunAsync(cts.Token);

        await WaitForStateAsync(connection, s => s.Status == LcuConnectionStatus.Connected, cts.Token);

        var firstSocket = await server.NextWebSocketAsync(cts.Token);
        await ReceiveTextAsync(firstSocket, cts.Token);

        // 模拟客户端侧链路中断：服务端直接 Abort，客户端读循环会立刻抛异常
        firstSocket.Abort();

        await WaitUntilAsync(
            () => Snapshot(states).Count(s => s.Status == LcuConnectionStatus.Disconnected) >= 1,
            cts.Token);

        await WaitUntilAsync(() => server.WebSocketConnectionCount >= 2, cts.Token);
        await WaitUntilAsync(
            () => connection.CurrentState.Status == LcuConnectionStatus.Connected,
            cts.Token);

        await StopAsync(cts, run);
    }

    [Fact]
    public async Task Start_IsIdempotent()
    {
        await using var server = FakeLcuServer.Start();
        var source = new FakeProcessSource();
        var finder = NewFinder(source);

        using var connection = new LcuConnection(
            finder,
            retryDelay: TimeSpan.FromMilliseconds(20),
            transportFactory: _ => new StubTransport(_ => new LcuResponse(200, "[]")),
            eventStreamFactory: _ => new LcuEventStream(server.BaseAddress, server.AuthorizationHeader));

        using var cts = new CancellationTokenSource(Timeout);

        connection.Start(cts.Token);
        connection.Start(cts.Token); // 第二次必须被忽略

        await WaitForStateAsync(connection, s => s.Status == LcuConnectionStatus.Connected, cts.Token);
        await Task.Delay(150, cts.Token);

        Assert.Equal(1, server.WebSocketConnectionCount);
    }

    [Fact]
    public async Task Dispose_StopsLoopAndCompletesStreams()
    {
        await using var server = FakeLcuServer.Start();
        var source = new FakeProcessSource();
        var finder = NewFinder(source);

        var connection = new LcuConnection(
            finder,
            retryDelay: TimeSpan.FromMilliseconds(20),
            transportFactory: _ => new StubTransport(_ => new LcuResponse(200, "[]")),
            eventStreamFactory: _ => new LcuEventStream(server.BaseAddress, server.AuthorizationHeader));

        using var cts = new CancellationTokenSource(Timeout);
        connection.Start(cts.Token);

        await WaitForStateAsync(connection, s => s.Status == LcuConnectionStatus.Connected, cts.Token);

        connection.Dispose();

        Assert.True(connection.States.IsCompleted);
        Assert.True(connection.Events.IsCompleted);

        var connectionsAtDispose = server.WebSocketConnectionCount;
        await Task.Delay(200, cts.Token);

        // 循环已退出：不再重连（若循环仍在跑，这里会看到新连接）
        Assert.Equal(connectionsAtDispose, server.WebSocketConnectionCount);

        connection.Dispose(); // 幂等
    }

    [Fact]
    public void Constructor_RejectsInvalidConcurrency()
    {
        var finder = NewFinder(new FakeProcessSource());

        Assert.Throws<ArgumentOutOfRangeException>(() => new LcuConnection(finder, maxConcurrency: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LcuConnection(finder, maxConcurrency: 33));
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private static LcuProcessFinder NewFinder(FakeProcessSource source, uint port = 50000, string token = "conn-token")
    {
        const string directory = @"Game\LeagueClient";

        source.Processes.Add(new LcuProcessInfo(
            4242,
            Path.Combine(directory, "LeagueClientUx.exe"),
            $"--app-port={port} --remoting-auth-token={token} --rso_platform_id=CN1"));
        source.AliveProcessIds.Add(4242);

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(directory, "lockfile")] = $"LeagueClient:4242:{port}:{token}:https",
        };

        return new LcuProcessFinder(source, path => files.TryGetValue(path, out var text) ? text : null);
    }

    private static List<LcuConnectionState> Collect(EventStream<LcuConnectionState> states)
    {
        var collected = new List<LcuConnectionState>();

        states.Subscribe(state =>
        {
            lock (collected)
            {
                collected.Add(state);
            }
        });

        return collected;
    }

    private static int StateCount(List<LcuConnectionState> states, LcuConnectionStatus status)
    {
        lock (states)
        {
            return states.Count(s => s.Status == status);
        }
    }

    private static LcuConnectionState[] Snapshot(List<LcuConnectionState> states)
    {
        lock (states)
        {
            return [.. states];
        }
    }

    private static async Task<LcuConnectionState> WaitForStateAsync(
        LcuConnection connection,
        Func<LcuConnectionState, bool> predicate,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<LcuConnectionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = connection.States.Subscribe(state =>
        {
            if (predicate(state))
            {
                completion.TrySetResult(state);
            }
        });

        // 先订阅再查当前值：状态可能在订阅之前就已到达
        if (predicate(connection.CurrentState))
        {
            return connection.CurrentState;
        }

        return await completion.Task.WaitAsync(Timeout, cancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Timeout;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待条件超时");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    /// <summary>取消后等待循环退出：<see cref="LcuConnection.RunAsync"/> 以正常返回收敛，不抛异常。</summary>
    private static async Task StopAsync(CancellationTokenSource cts, Task run)
    {
        await cts.CancelAsync();
        await run.WaitAsync(Timeout);
    }

    private static Task SendFrameAsync(WebSocket socket, string payload, CancellationToken cancellationToken)
        => socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, cancellationToken);

    private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var accumulated = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return string.Empty;
            }

            accumulated.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(accumulated.ToArray());
            }
        }
    }

    private sealed class FakeProcessSource : ILcuProcessSource
    {
        public List<LcuProcessInfo> Processes { get; } = [];

        public HashSet<int> AliveProcessIds { get; } = [];

        public IReadOnlyList<LcuProcessInfo> FindClientProcesses() => [.. Processes];

        public bool IsProcessAlive(int processId) => AliveProcessIds.Contains(processId);
    }

    /// <summary>按调用次数可编程响应的传输桩。只关心「第几次调用得到什么」，不模拟网络。</summary>
    private sealed class StubTransport(Func<int, LcuResponse> responder) : ILcuTransport
    {
        private readonly List<string> _paths = [];

        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_paths)
                {
                    return [.. _paths];
                }
            }
        }

        public Task<LcuResponse> SendAsync(LcuRequest request, CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _calls);
            lock (_paths)
            {
                _paths.Add(request.Path);
            }

            return Task.FromResult(responder(attempt));
        }

        public void Dispose()
        {
        }
    }

    /// <summary>把连接层的事件流接到无界通道，避免用轮询断言「有没有收到事件」。</summary>
    private sealed class EventProbe : IDisposable
    {
        private readonly System.Threading.Channels.Channel<LcuEvent> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<LcuEvent>();

        private readonly IDisposable _subscription;

        public EventProbe(EventStream<LcuEvent> events)
        {
            _subscription = events.Subscribe(evt => _channel.Writer.TryWrite(evt));
        }

        public Task<LcuEvent> NextAsync(CancellationToken cancellationToken)
            => _channel.Reader.ReadAsync(cancellationToken).AsTask();

        public void Dispose() => _subscription.Dispose();
    }
}
