using Xunit;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Tests;

public sealed class MatchFailureRegressionTests
{
    private sealed class Transport(Func<LcuRequest, CancellationToken, Task<LcuResponse>> handler) : ILcuTransport
    {
        public Task<LcuResponse> SendAsync(LcuRequest request, CancellationToken cancellationToken = default) => handler(request, cancellationToken);
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_FailurePublishesFailedAndImmediatelyRetries(bool throws)
    {
        int calls = 0;
        using var transport = new Transport((request, _) =>
        {
            if (++calls == 1)
            {
                return throws
                    ? Task.FromException<LcuResponse>(new LcuTransportException("offline", "GET", request.Path))
                    : Task.FromResult(new LcuResponse(503, ""));
            }
            return Task.FromResult(new LcuResponse(200, """{"games":{"gameCount":0,"games":[]}}"""));
        });
        var service = new MatchService(() => transport);
        var states = new List<MatchLoadState>();
        using var subscription = service.Status.Subscribe(status => states.Add(status.State));

        var failed = await service.GetMatchHistoryAsync("player", 0, 19);
        Assert.Empty(failed.Games);
        Assert.False(failed.Succeeded);
        Assert.Equal(MatchLoadState.Failed, states[^1]);
        var retried = await service.GetMatchHistoryAsync("player", 0, 19);
        Assert.Empty(retried.Games);
        Assert.True(retried.Succeeded);
        Assert.Equal(MatchLoadState.Loaded, states[^1]);
        await service.GetMatchHistoryAsync("player", 0, 19);
        Assert.Equal(2, calls);
        Assert.Equal(MatchLoadState.Loaded, states[^1]);
    }

    [Fact]
    public async Task History_CallerCancellationPropagatesAndCanRetry()
    {
        int calls = 0;
        using var cts = new CancellationTokenSource();
        using var transport = new Transport((_, ct) =>
        {
            if (++calls == 1)
            {
                cts.Cancel();
                return Task.FromCanceled<LcuResponse>(ct);
            }
            return Task.FromResult(new LcuResponse(200, """{"games":{"gameCount":0,"games":[]}}"""));
        });
        var service = new MatchService(() => transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetMatchHistoryAsync("player", 0, 19, cts.Token));
        Assert.Empty((await service.GetMatchHistoryAsync("player", 0, 19)).Games);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Detail_TransportFailurePublishesFailedAndCanRetry()
    {
        int calls = 0;
        using var transport = new Transport((request, _) =>
        {
            calls++;
            return Task.FromException<LcuResponse>(new LcuTransportException("offline", "GET", request.Path));
        });
        var service = new MatchService(() => transport);
        var states = new List<MatchLoadState>();
        using var subscription = service.Status.Subscribe(status => states.Add(status.State));
        Assert.Null(await service.GetMatchDetailAsync(42));
        Assert.Equal(MatchLoadState.Failed, states[^1]);
        Assert.Null(await service.GetMatchDetailAsync(42));
        Assert.Equal(2, calls);
    }
}
