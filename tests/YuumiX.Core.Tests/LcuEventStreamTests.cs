using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;
using YuumiX.Core.Lcu;
using YuumiX.Testing.Lcu;

namespace YuumiX.Core.Tests;

/// <summary>
/// LCU WebSocket 事件流：订阅帧、URI 过滤、去重、分片拼装、断开的处理。
///
/// 全部走真实回环套接字（<see cref="FakeLcuServer"/>），不用假的 WebSocket 抽象——
/// 这一层出错的地方几乎都在协议细节上（握手头、分片、关闭帧），mock 掉就等于没测。
/// </summary>
public sealed class LcuEventStreamTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Run_SendsSubscribeFrameWithBasicAuthorization()
    {
        await using var server = FakeLcuServer.Start("event-token");
        using var cts = new CancellationTokenSource(Timeout);

        await using var stream = new LcuEventStream(server.BaseAddress, server.AuthorizationHeader);
        var run = stream.RunAsync(cts.Token);

        using var socket = await server.NextWebSocketAsync(cts.Token);
        var frame = await ReceiveTextAsync(socket, cts.Token);

        Assert.Equal(LcuEventStream.SubscribeMessage, frame);

        var recorded = Assert.Single(server.Requests);
        Assert.Equal("GET", recorded.Method);
        Assert.Equal("/", recorded.Path);
        Assert.Equal(server.AuthorizationHeader, recorded.Authorization);

        await StopAsync(cts, run);
    }

    /// <summary>只放行 5 个 URI；其余事件即使到达也必须丢弃（对齐原 WATCHED_URIS）。</summary>
    [Fact]
    public async Task Run_DeliversWatchedUriAndDropsOthers()
    {
        await using var server = FakeLcuServer.Start();
        using var cts = new CancellationTokenSource(Timeout);

        await using var stream = new LcuEventStream(server.BaseAddress, server.AuthorizationHeader);
        using var probe = new EventProbe(stream);
        var run = stream.RunAsync(cts.Token);

        using var socket = await server.NextWebSocketAsync(cts.Token);
        await ReceiveTextAsync(socket, cts.Token); // 订阅帧

        await SendFrameAsync(socket, Frame("/lol-summoner/v1/current-summoner", """{"displayName":"被忽略"}"""), cts.Token);
        await SendFrameAsync(socket, Frame("/lol-gameflow/v1/gameflow-phase", "\"ChampSelect\""), cts.Token);

        var received = await probe.NextAsync(cts.Token);

        Assert.Equal("/lol-gameflow/v1/gameflow-phase", received.Uri);
        Assert.Equal("Update", received.EventType);
        Assert.Equal("ChampSelect", received.Data.GetString());

        // 未被关注的那条不应排队
        Assert.False(probe.TryRead(out _));

        await StopAsync(cts, run);
    }

    [Fact]
    public async Task Run_DeduplicatesIdenticalPayloadPerUri()
    {
        await using var server = FakeLcuServer.Start();
        using var cts = new CancellationTokenSource(Timeout);

        await using var stream = new LcuEventStream(server.BaseAddress, server.AuthorizationHeader);
        using var probe = new EventProbe(stream);
        var run = stream.RunAsync(cts.Token);

        using var socket = await server.NextWebSocketAsync(cts.Token);
        await ReceiveTextAsync(socket, cts.Token);

        var frame = Frame("/lol-matchmaking/v1/ready-check", """{"state":"InProgress","playerResponse":"None"}""");
        await SendFrameAsync(socket, frame, cts.Token);
        await SendFrameAsync(socket, frame, cts.Token); // 内容一致，应被去重

        _ = await probe.NextAsync(cts.Token);
        Assert.False(probe.TryRead(out _), "相同内容不应重复下发");

        // 内容变化后必须重新下发
        await SendFrameAsync(
            socket,
            Frame("/lol-matchmaking/v1/ready-check", """{"state":"InProgress","playerResponse":"Accepted"}"""),
            cts.Token);

        var changed = await probe.NextAsync(cts.Token);
        Assert.Contains("Accepted", changed.Data.GetRawText());

        await StopAsync(cts, run);
    }

    /// <summary>去重表按连接创建：重连后的首个事件即使内容与上一连接相同也必须下发。</summary>
    [Fact]
    public async Task Run_NewConnection_ResetsDeduplication()
    {
        await using var server = FakeLcuServer.Start();
        using var cts = new CancellationTokenSource(Timeout);

        await using var stream = new LcuEventStream(server.BaseAddress, server.AuthorizationHeader);
        using var probe = new EventProbe(stream);

        var frame = Frame("/lol-champ-select/v1/current-champion", "103");
        using var firstCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var firstRun = stream.RunAsync(firstCts.Token);

        var firstSocket = await server.NextWebSocketAsync(cts.Token);
        await ReceiveTextAsync(firstSocket, cts.Token);
        await SendFrameAsync(firstSocket, frame, cts.Token);
        _ = await probe.NextAsync(cts.Token);

        // 先取消再关套接字：顺序反过来会让读循环以 WebSocketException 结束，
        // 这里要断言的是「取消可正常退出」这条路径。
        await firstCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstRun).WaitAsync(Timeout);
        firstSocket.Dispose();

        // 第二次连接：同内容必须再次下发
        var secondRun = stream.RunAsync(cts.Token);
        using (var socket = await server.NextWebSocketAsync(cts.Token))
        {
            await ReceiveTextAsync(socket, cts.Token);
            await SendFrameAsync(socket, frame, cts.Token);
            var received = await probe.NextAsync(cts.Token);
            Assert.Equal(103, received.Data.GetInt32());
        }

        await StopAsync(cts, secondRun);
    }

    /// <summary>
    /// 满编选人会话超过单次读取缓冲区，必然走多帧拼装。
    /// 这里用 <c>EndOfMessage = false</c> 显式覆盖拼接路径。
    /// </summary>
    [Fact]
    public async Task Run_AssemblesFragmentedMessage()
    {
        await using var server = FakeLcuServer.Start();
        using var cts = new CancellationTokenSource(Timeout);

        await using var stream = new LcuEventStream(server.BaseAddress, server.AuthorizationHeader);
        using var probe = new EventProbe(stream);
        var run = stream.RunAsync(cts.Token);

        using var socket = await server.NextWebSocketAsync(cts.Token);
        await ReceiveTextAsync(socket, cts.Token);

        var payload = Frame("/lol-champ-select/v1/session", """{"localPlayerCellId":2,"benchChampions":[]}""");
        var split = payload.Length / 2;

        await socket.SendAsync(Encoding.UTF8.GetBytes(payload[..split]), WebSocketMessageType.Text, false, cts.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes(payload[split..]), WebSocketMessageType.Text, true, cts.Token);

        var received = await probe.NextAsync(cts.Token);
        Assert.Equal("/lol-champ-select/v1/session", received.Uri);
        Assert.Equal(2, received.Data.GetProperty("localPlayerCellId").GetInt32());

        await StopAsync(cts, run);
    }

    /// <summary>DELETE 事件的 data 是 null，必须原样下发而不是整条丢弃。</summary>
    [Fact]
    public async Task Run_DeliversNullData()
    {
        await using var server = FakeLcuServer.Start();
        using var cts = new CancellationTokenSource(Timeout);

        await using var stream = new LcuEventStream(server.BaseAddress, server.AuthorizationHeader);
        using var probe = new EventProbe(stream);
        var run = stream.RunAsync(cts.Token);

        using var socket = await server.NextWebSocketAsync(cts.Token);
        await ReceiveTextAsync(socket, cts.Token);

        await SendFrameAsync(socket, Frame("/lol-matchmaking/v1/ready-check", "null", "Delete"), cts.Token);

        var received = await probe.NextAsync(cts.Token);
        Assert.Equal("Delete", received.EventType);
        Assert.Equal(JsonValueKind.Null, received.Data.ValueKind);

        await StopAsync(cts, run);
    }

    /// <summary>非 WAMP 事件帧（欢迎帧、非法 JSON）应被忽略，且不得中断读循环。</summary>
    [Fact]
    public async Task Run_IgnoresNonEventFramesAndContinues()
    {
        await using var server = FakeLcuServer.Start();
        using var cts = new CancellationTokenSource(Timeout);

        await using var stream = new LcuEventStream(server.BaseAddress, server.AuthorizationHeader);
        using var probe = new EventProbe(stream);
        var run = stream.RunAsync(cts.Token);

        using var socket = await server.NextWebSocketAsync(cts.Token);
        await ReceiveTextAsync(socket, cts.Token);

        await SendFrameAsync(socket, """[1,"OnJsonApiEvent",{}]""", cts.Token);   // 非 8 号 opcode
        await SendFrameAsync(socket, "not-json-at-all", cts.Token);              // 非法 JSON
        await SendFrameAsync(socket, """[8,"OnJsonApiEvent",{"uri":"/lol-champ-select/v1/session"}]""", cts.Token); // 缺 data 键但可下发

        var received = await probe.NextAsync(cts.Token);
        Assert.Equal("/lol-champ-select/v1/session", received.Uri);
        Assert.Equal(JsonValueKind.Null, received.Data.ValueKind);

        await StopAsync(cts, run);
    }

    /// <summary>对端主动断开时读循环必须结束，由连接层决定重连，而不是在此阻塞。</summary>
    [Fact]
    public async Task Run_EndsWhenPeerClosesConnection()
    {
        await using var server = FakeLcuServer.Start();
        using var cts = new CancellationTokenSource(Timeout);

        await using var stream = new LcuEventStream(server.BaseAddress, server.AuthorizationHeader);
        var run = stream.RunAsync(cts.Token);

        var socket = await server.NextWebSocketAsync(cts.Token);
        await ReceiveTextAsync(socket, cts.Token);

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);

        await run.WaitAsync(Timeout);
        Assert.False(stream.Events.IsCompleted); // 流本身可复用，不因单次断开而完结
    }

    [Fact]
    public void WatchedUris_AreAllAllowedByCatalog()
    {
        Assert.Equal(5, LcuEventStream.WatchedUris.Count);

        foreach (var uri in LcuEventStream.WatchedUris)
        {
            Assert.True(LcuApiCatalog.IsAllowed(uri), $"订阅清单中的 {uri} 不在路径白名单内");
        }
    }

    [Theory]
    [InlineData("https://127.0.0.1:50000", "wss://127.0.0.1:50000/")]
    [InlineData("http://127.0.0.1:50001/", "ws://127.0.0.1:50001/")]
    public void Endpoint_MapsSchemeAndRootsPath(string baseAddress, string expected)
    {
        var stream = new LcuEventStream(new Uri(baseAddress), "Basic x");
        Assert.Equal(expected, stream.Endpoint.ToString());
    }

    [Fact]
    public void Constructor_RejectsUnsupportedScheme()
    {
        Assert.Throws<ArgumentException>(() => new LcuEventStream(new Uri("ftp://127.0.0.1:1/"), "Basic x"));
        Assert.Throws<ArgumentException>(() => new LcuEventStream(new Uri("http://127.0.0.1:1/"), " "));
    }

    [Fact]
    public void MatchWatchedUri_ReturnsMatchedPrefix()
    {
        Assert.Equal("/lol-gameflow/v1/session", LcuEventStream.MatchWatchedUri("/lol-gameflow/v1/session"));
        Assert.Equal("/lol-champ-select/v1/session", LcuEventStream.MatchWatchedUri("/lol-champ-select/v1/session/something"));
        Assert.Null(LcuEventStream.MatchWatchedUri("/lol-summoner/v1/current-summoner"));
        Assert.Null(LcuEventStream.MatchWatchedUri(null));
    }

    private static string Frame(string uri, string dataJson, string eventType = "Update")
        => $$"""[8, "OnJsonApiEvent", {"uri":"{{uri}}","eventType":"{{eventType}}","data":{{dataJson}}}]""";

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

    /// <summary>取消并等待读循环退出，确保测试结束时没有悬挂任务。</summary>
    private static async Task StopAsync(CancellationTokenSource cts, Task run)
    {
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run).WaitAsync(Timeout);
    }

    /// <summary>把事件流接到无界通道上，使「有/没有事件」的断言不依赖轮询。</summary>
    private sealed class EventProbe : IDisposable
    {
        private readonly Channel<LcuEvent> _channel = Channel.CreateUnbounded<LcuEvent>();
        private readonly IDisposable _subscription;

        public EventProbe(LcuEventStream stream)
        {
            _subscription = stream.Events.Subscribe(evt => _channel.Writer.TryWrite(evt));
        }

        public Task<LcuEvent> NextAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAsync(cancellationToken).AsTask();

        /// <summary>
        /// 非阻塞取一条。通道的 <c>TryRead</c> 用 <c>[MaybeNullWhen(false)]</c> 标注返回值，
        /// 直接透传会给非可空参数产生 CS8601，这里显式收敛。
        /// </summary>
        public bool TryRead(out LcuEvent received)
        {
            if (_channel.Reader.TryRead(out var item) && item is not null)
            {
                received = item;
                return true;
            }

            received = null!;
            return false;
        }

        public void Dispose() => _subscription.Dispose();
    }
}
