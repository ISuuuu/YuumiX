using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YuumiX.Core.Lcu;
using YuumiX.Testing.Lcu;

namespace YuumiX.Core.Tests;

/// <summary>
/// LCU HTTP 传输：鉴权注入、白名单、重试语义、并发限流、超时与取消。
/// 正常路径走真套接字（<see cref="FakeLcuServer"/>），故障路径走可控的消息处理器。
/// </summary>
public sealed class LcuHttpTransportTests
{
    [Fact]
    public async Task Send_Get_InjectsAuthorizationAndReturnsBody()
    {
        await using var server = FakeLcuServer.Start("secret-token");
        server.Handler = _ => FakeResponse.Ok("""{"patchline":"live"}""");

        using var transport = NewTransport(server);

        var response = await transport.SendAsync(LcuRequest.Get(LcuApiCatalog.ReadinessPath));

        Assert.True(response.IsSuccess);
        Assert.Equal("""{"patchline":"live"}""", response.Body);

        var recorded = Assert.Single(server.Requests);
        Assert.Equal("GET", recorded.Method);
        Assert.Equal("/system/v1/builds", recorded.Path);
        Assert.Equal(server.AuthorizationHeader, recorded.Authorization);
    }

    [Fact]
    public async Task Send_Post_SendsJsonBodyWithContentType()
    {
        await using var server = FakeLcuServer.Start();
        using var transport = NewTransport(server);

        await transport.SendAsync(LcuRequest.Post("/lol-lobby/v2/lobby", """{"queueId":430}"""));

        var recorded = Assert.Single(server.Requests);
        Assert.Equal("POST", recorded.Method);
        Assert.Equal("""{"queueId":430}""", recorded.Body);
        Assert.Contains("application/json", recorded.ContentType);
    }

    /// <summary>404 是 LCU 的业务常态，必须作为响应返回而不是异常。</summary>
    [Fact]
    public async Task Send_NotFound_IsReturnedAsResponse()
    {
        await using var server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.NotFound();

        using var transport = NewTransport(server);

        var response = await transport.SendAsync(LcuRequest.Get("/lol-champ-select/v1/session"));

        Assert.False(response.IsSuccess);
        Assert.True(response.IsNotFound);
        Assert.Single(server.Requests); // 404 不重试
    }

    [Fact]
    public async Task Send_RejectsPathOutsideWhitelist()
    {
        await using var server = FakeLcuServer.Start();
        using var transport = NewTransport(server);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.SendAsync(LcuRequest.Get("/internal/admin/shutdown")));

        Assert.Empty(server.Requests);
    }

    /// <summary>网络异常重试：前两次失败、第三次成功，最终应返回成功结果。</summary>
    [Fact]
    public async Task Send_RetriesTransientFailures()
    {
        var handler = new StubHandler((_, attempt, _) => attempt < 3
            ? throw new HttpRequestException("连接被拒绝")
            : Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")));

        using var transport = NewTransport(handler);

        var response = await transport.SendAsync(LcuRequest.Get("/system/v1/builds"));

        Assert.True(response.IsSuccess);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Send_AfterExhaustingRetries_ThrowsTransportException()
    {
        var handler = new StubHandler((_, _, _) => throw new HttpRequestException("连接被拒绝"));
        using var transport = NewTransport(handler);

        var error = await Assert.ThrowsAsync<LcuTransportException>(
            () => transport.SendAsync(LcuRequest.Get("/system/v1/builds")));

        Assert.Equal(LcuHttpTransport.MaxRetries, handler.Calls);
        Assert.Equal("/system/v1/builds", error.Path);
        Assert.NotNull(error.InnerException);
    }

    /// <summary>
    /// 并发上限必须生效：超过 <c>maxConcurrency</c> 的请求要排队，
    /// 否则选人阶段的写操作会被一堆战绩拉取挤爆。
    /// </summary>
    [Fact]
    public async Task Send_RespectsConcurrencyLimit()
    {
        var current = 0;
        var peak = 0;

        var handler = new StubHandler(async (_, _, _) =>
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref peak, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref current);
            return JsonResponse(HttpStatusCode.OK, "{}");
        });

        using var transport = NewTransport(handler, maxConcurrency: 2);

        var requests = Enumerable.Range(0, 6)
            .Select(_ => transport.SendAsync(LcuRequest.Get("/lol-summoner/v1/current-summoner")));

        await Task.WhenAll(requests);

        Assert.Equal(2, peak);
        Assert.Equal(6, handler.Calls);
    }

    /// <summary>单次请求超时应当被当作传输异常处理（重试），而不是无限挂起。</summary>
    [Fact]
    public async Task Send_RequestTimeout_IsRetriedThenThrows()
    {
        var handler = new StubHandler(async (_, _, cancellationToken) =>
        {
            // 必须把令牌交给 Delay：真实 SocketsHttpHandler 会因令牌取消而中止，
            // 假处理器若不尊重令牌，就等于在测试一个不会超时的实现。
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return JsonResponse(HttpStatusCode.OK, "{}");
        });

        using var transport = NewTransport(handler, requestTimeout: TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAsync<LcuTransportException>(
            () => transport.SendAsync(LcuRequest.Get("/system/v1/builds")));
        Assert.Equal(LcuHttpTransport.MaxRetries, handler.Calls);
    }

    /// <summary>调用方取消（关窗）必须立即退出，不能被重试逻辑拖住。</summary>
    [Fact]
    public async Task Send_CallerCancellation_PropagatesWithoutRetry()
    {
        var entered = new TaskCompletionSource();
        var handler = new StubHandler(async (_, _, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return JsonResponse(HttpStatusCode.OK, "{}");
        });

        using var transport = NewTransport(handler);
        using var cancellation = new CancellationTokenSource();

        var pending = transport.SendAsync(LcuRequest.Get("/system/v1/builds"), cancellation.Token);
        await entered.Task;
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Constructor_RejectsOutOfRangeConcurrency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LcuHttpTransport(new Uri("http://127.0.0.1:1/"), "Basic x", maxConcurrency: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LcuHttpTransport(new Uri("http://127.0.0.1:1/"), "Basic x", maxConcurrency: 33));
    }

    private static LcuHttpTransport NewTransport(
        FakeLcuServer server,
        int maxConcurrency = 5,
        TimeSpan? requestTimeout = null)
        => new(server.BaseAddress, server.AuthorizationHeader, maxConcurrency, requestTimeout, NullLogger<LcuHttpTransport>.Instance);

    private static LcuHttpTransport NewTransport(
        HttpMessageHandler handler,
        int maxConcurrency = 5,
        TimeSpan? requestTimeout = null)
        => new(new Uri("http://127.0.0.1:1/"), "Basic dGVzdA==", maxConcurrency, requestTimeout, NullLogger<LcuHttpTransport>.Instance, handler);

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static void InterlockedMax(ref int target, int value)
    {
        int snapshot;
        do
        {
            snapshot = Volatile.Read(ref target);
            if (value <= snapshot)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, snapshot) != snapshot);
    }

    /// <summary>按调用次数编程响应/异常的处理器。用于确定性地复现故障序列。</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, Interlocked.Increment(ref _calls), cancellationToken);
    }
}
