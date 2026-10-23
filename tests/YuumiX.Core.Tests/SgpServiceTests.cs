using System.Net;
using System.Text.Json;
using Xunit;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Lcu.Sgp;

namespace YuumiX.Core.Tests;

public sealed class SgpServiceTests
{
    private const string TestPuuid = "11111111-2222-3333-4444-555555555555";

    [Theory]
    [InlineData("hn1", true)]
    [InlineData("HN1", true)]
    [InlineData("bgp2", true)]
    [InlineData("tj100", true)]
    [InlineData("na1", false)]
    [InlineData("kr", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupported_ChecksTencentServers(string? server, bool expected)
    {
        var service = new SgpService(() => null);
        Assert.Equal(expected, service.IsSupported(server));
    }

    [Theory]
    [InlineData("hn1", "https://hn1-k8s-sgp.lol.qq.com:21019")]
    [InlineData("bgp2", "https://bgp2-k8s-sgp.lol.qq.com:21019")]
    [InlineData("tj100", "https://tj100-sgp.lol.qq.com:21019")]
    [InlineData("CQ100", "https://cq100-sgp.lol.qq.com:21019")]
    public void BuildSgpBaseUrl_ResolvesCorrectDomains(string server, string expectedUrl)
    {
        Assert.Equal(expectedUrl, SgpService.BuildSgpBaseUrl(server));
    }

    [Fact]
    public async Task GetAccessTokenAsync_CachesTokenWithinDuration()
    {
        int lcuCallCount = 0;
        var transport = new StubLcuTransport(req =>
        {
            if (req.Path == "/entitlements/v1/token")
            {
                lcuCallCount++;
                return new LcuResponse(200, """{"accessToken":"test-access-token-123"}""");
            }
            return new LcuResponse(404, "");
        });

        using var service = new SgpService(() => transport);

        string? token1 = await service.GetAccessTokenAsync();
        string? token2 = await service.GetAccessTokenAsync();

        Assert.Equal("test-access-token-123", token1);
        Assert.Equal("test-access-token-123", token2);
        Assert.Equal(1, lcuCallCount);
    }

    [Fact]
    public async Task GetMatchHistoryAsync_RetriesOn401WithRefreshedToken()
    {
        int tokenFetchCount = 0;
        var transport = new StubLcuTransport(req =>
        {
            if (req.Path == "/entitlements/v1/token")
            {
                tokenFetchCount++;
                return new LcuResponse(200, $"{{\"accessToken\":\"token-v{tokenFetchCount}\"}}");
            }
            return new LcuResponse(404, "");
        });

        int sgpRequestCount = 0;
        string? lastBearerToken = null;
        var mockHttpHandler = new DelegateHttpMessageHandler(req =>
        {
            sgpRequestCount++;
            lastBearerToken = req.Headers.Authorization?.Parameter;
            if (sgpRequestCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            // 返回一份最小合法 SGP 战绩响应
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                        "games": [
                            {
                                "json": {
                                    "gameId": 12345678,
                                    "queueId": 420,
                                    "gameCreation": 1700000000000,
                                    "gameDuration": 1500,
                                    "participants": [
                                        {
                                            "puuid": "{{TestPuuid}}",
                                            "championId": 1,
                                            "stats": {
                                                "win": true,
                                                "kills": 5,
                                                "deaths": 2,
                                                "assists": 8,
                                                "item0": 0, "item1": 0, "item2": 0, "item3": 0, "item4": 0, "item5": 0, "item6": 0
                                            }
                                        }
                                    ]
                                }
                            }
                        ]
                    }
                    """)
            };
        });

        using var service = new SgpService(
            () => transport,
            credentialsProvider: () => new LcuCredentials(50000, "riot-token", 1234, "hn1"),
            sgpHandler: mockHttpHandler);

        IReadOnlyList<MatchSummary> matches = await service.GetMatchHistoryAsync("hn1", TestPuuid, 20, 10);

        Assert.Equal(2, sgpRequestCount);
        Assert.Equal(2, tokenFetchCount);
        Assert.Equal("token-v2", lastBearerToken);
        MatchSummary single = Assert.Single(matches);
        Assert.Equal(12345678, single.GameId);
        Assert.True(single.Win);
    }

    private sealed class StubLcuTransport : ILcuTransport
    {
        private readonly Func<LcuRequest, LcuResponse> _handler;

        public StubLcuTransport(Func<LcuRequest, LcuResponse> handler) => _handler = handler;

        public Task<LcuResponse> SendAsync(LcuRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_handler(request));

        public void Dispose() { }
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
