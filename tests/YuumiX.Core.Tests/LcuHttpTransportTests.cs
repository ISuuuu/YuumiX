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
    public async Task Send_BackoffReleasesPermit_AndCancellationDoesNotLeakIt()
    {
        var handler = new StubHandler((_, attempt, _) => attempt == 1
            ? throw new HttpRequestException("连接被拒绝")
            : Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")));
        using var transport = NewTransport(handler, maxConcurrency: 1);
        using var cancellation = new CancellationTokenSource();

        var retrying = transport.SendAsync(LcuRequest.Get("/system/v1/builds"), cancellation.Token);
        try
        {
            var other = transport.SendAsync(LcuRequest.Get("/system/v1/builds"));
            var response = await other.WaitAsync(TimeSpan.FromMilliseconds(300));
            Assert.True(response.IsSuccess);
            Assert.Equal(2, handler.Calls);
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retrying);
        Assert.True((await transport.SendAsync(LcuRequest.Get("/system/v1/builds"))).IsSuccess);
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

    [Fact]
    public async Task Send_WhenDisposedBeforeSend_ThrowsLcuTransportException()
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}")));
        var transport = NewTransport(handler);
        transport.Dispose();

        var ex = await Assert.ThrowsAsync<LcuTransportException>(
            () => transport.SendAsync(LcuRequest.Get("/system/v1/builds")));

        Assert.Equal("GET", ex.Method);
        Assert.Equal("/system/v1/builds", ex.Path);
        Assert.Contains("已释放", ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Send_WhenDisposedDuringSend_ThrowsLcuTransportException()
    {
        var handler = new StubHandler((_, _, _) => throw new ObjectDisposedException("HttpClient"));
        using var transport = NewTransport(handler);

        var ex = await Assert.ThrowsAsync<LcuTransportException>(
            () => transport.SendAsync(LcuRequest.Get("/system/v1/builds")));

        Assert.Equal("GET", ex.Method);
        Assert.Equal("/system/v1/builds", ex.Path);
        Assert.Contains("已释放", ex.Message);
        Assert.IsType<ObjectDisposedException>(ex.InnerException);
    }

    /// <summary>
    /// 释放时正在等并发许可的请求必须立刻结束。<c>SemaphoreSlim.Dispose</c> 不会唤醒等待者，
    /// 若许可等待不链接释放信号，这里要一直等到 8 秒许可超时才返回。
    /// </summary>
    [Fact]
    public async Task Send_WhenDisposedWhileWaitingForPermit_ThrowsImmediately()
    {
        var permitHeld = new TaskCompletionSource();
        var releaseHolder = new TaskCompletionSource();

        var handler = new StubHandler(async (_, _, cancellationToken) =>
        {
            permitHeld.TrySetResult();
            // 占住唯一许可，直到测试放行或传输层释放
            await releaseHolder.Task.WaitAsync(cancellationToken);
            return JsonResponse(HttpStatusCode.OK, "{}");
        });

        var transport = NewTransport(handler, maxConcurrency: 1);
        try
        {
            var holder = transport.SendAsync(LcuRequest.Get("/lol-summoner/v1/current-summoner"));
            await permitHeld.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // 第二个请求抢不到许可：async 方法会同步执行到第一个挂起点，
            // 所以拿到 Task 时它已经在 WaitAsync 里排队，不需要 sleep 去猜。
            var waiter = transport.SendAsync(LcuRequest.Get("/lol-summoner/v1/current-summoner"));
            Assert.False(waiter.IsCompleted);

            var elapsed = Stopwatch.StartNew();
            transport.Dispose();

            var ex = await Assert.ThrowsAsync<LcuTransportException>(() => waiter)
                .WaitAsync(TimeSpan.FromSeconds(2));
            elapsed.Stop();

            Assert.Contains("已释放", ex.Message);
            Assert.Equal("/lol-summoner/v1/current-summoner", ex.Path);
            // 许可超时是 8 秒；这里必须远早于它结束
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"释放后仍等待了 {elapsed.ElapsedMilliseconds}ms");

            // 已经握住许可的请求同样要立刻收尾，不能等 handler 返回
            releaseHolder.TrySetResult();
            await Assert.ThrowsAsync<LcuTransportException>(() => holder)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseHolder.TrySetResult();
        }
    }

    [Fact]
    public async Task Dispose_WhileHandlerIgnoresCancellation_DoesNotWaitForRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (_, _, _) =>
        {
            started.TrySetResult();
            await finish.Task;
            throw new HttpRequestException("连接已关闭");
        });

        using var transport = NewTransport(handler, maxConcurrency: 1);
        var pending = transport.SendAsync(LcuRequest.Get("/system/v1/builds"));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Run(transport.Dispose).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            finish.TrySetResult();
        }

        var exception = await Assert.ThrowsAsync<LcuTransportException>(() => pending)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("已释放", exception.Message);
    }

    /// <summary>
    /// 复现实测的快速释放节奏：占住唯一许可 → 第二个请求刚排队 → 立刻释放，连跑多轮。
    /// 单轮容易碰巧通过；多轮用于覆盖 Cancel 与 WaitAsync 异步退出的竞争窗口
    /// （曾出现 100 轮里 97 个请求挂住的情况）。
    /// </summary>
    [Fact]
    public async Task Send_RepeatedFastDisposeWhileQueued_NeverStrandsWaiters()
    {
        const int Rounds = 100;

        for (int round = 1; round <= Rounds; round++)
        {
            var permitHeld = new TaskCompletionSource();
            var releaseHolder = new TaskCompletionSource();

            var handler = new StubHandler(async (_, _, cancellationToken) =>
            {
                permitHeld.TrySetResult();
                await releaseHolder.Task.WaitAsync(cancellationToken);
                return JsonResponse(HttpStatusCode.OK, "{}");
            });

            var transport = NewTransport(handler, maxConcurrency: 1);
            try
            {
                var holder = transport.SendAsync(LcuRequest.Get("/lol-summoner/v1/current-summoner"));
                await permitHeld.Task.WaitAsync(TimeSpan.FromSeconds(2));

                var waiter = transport.SendAsync(LcuRequest.Get("/lol-summoner/v1/current-summoner"));
                transport.Dispose();
                releaseHolder.TrySetResult();

                try
                {
                    await Assert.ThrowsAsync<LcuTransportException>(() => waiter)
                        .WaitAsync(TimeSpan.FromSeconds(2));
                    await Assert.ThrowsAsync<LcuTransportException>(() => holder)
                        .WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex)
                {
                    Assert.Fail($"第 {round}/{Rounds} 轮：释放后请求未结束 —— {ex.GetType().Name}: {ex.Message}");
                }
            }
            finally
            {
                releaseHolder.TrySetResult();
            }
        }
    }

    /// <summary>
    /// 释放时正在重试退避的请求必须立刻结束，否则要白等完整个退避才能走到下一次尝试。
    /// 这里把退避拉长到 5 秒制造区分度：断言只要求「2 秒内结束 + 第二次尝试从未发生」，
    /// 不再依赖固定 sleep 或脆弱的耗时阈值去猜执行窗口。
    /// </summary>
    [Fact]
    public async Task Send_WhenDisposedDuringRetryBackoff_ThrowsImmediately()
    {
        var firstAttemptFailed = new TaskCompletionSource();
        var handler = new StubHandler((_, calls, _) =>
        {
            if (calls == 1)
            {
                firstAttemptFailed.TrySetResult();
            }

            throw new HttpRequestException("连接被重置");
        });

        var transport = NewTransport(handler, retryDelay: TimeSpan.FromSeconds(5));
        var pending = transport.SendAsync(LcuRequest.Get("/system/v1/builds"));

        // 明确状态信号：第一次尝试已经失败，请求此刻处在退避窗口内
        await firstAttemptFailed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        transport.Dispose();

        var ex = await Assert.ThrowsAsync<LcuTransportException>(() => pending)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains("已释放", ex.Message);
        // 修复失效时它会在 5 秒退避后发起第二次尝试：Calls 变 2，且上面的 2 秒等待先超时
        Assert.Equal(1, handler.Calls);
    }

    private static LcuHttpTransport NewTransport(
        FakeLcuServer server,
        int maxConcurrency = 5,
        TimeSpan? requestTimeout = null)
        => new(server.BaseAddress, server.AuthorizationHeader, maxConcurrency, requestTimeout, NullLogger<LcuHttpTransport>.Instance);

    private static LcuHttpTransport NewTransport(
        HttpMessageHandler handler,
        int maxConcurrency = 5,
        TimeSpan? requestTimeout = null,
        TimeSpan? retryDelay = null)
        => new(
            new Uri("http://127.0.0.1:1/"),
            "Basic dGVzdA==",
            maxConcurrency,
            requestTimeout,
            NullLogger<LcuHttpTransport>.Instance,
            handler,
            retryDelay);

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
