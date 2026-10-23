using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Lcu;
using YuumiX.Testing.Lcu;

namespace YuumiX.Core.Tests;

/// <summary>
/// 生涯服务的离线验收（真套接字 + 真传输层）。
///
/// 重点在"什么该缓存、什么不该缓存"：段位是稳定事实，可以缓存到 TTL；
/// 而"客户端没连上"是暂时状态，缓存它会让客户端启动后仍然显示 <c>--</c>。
/// 因此成功（含未定级空结果）进缓存，失败不进。
/// </summary>
public sealed class CareerServiceTests
{
    private const string Puuid = "11111111-2222-3333-4444-555555555555";

    private const string Payload =
        """
        {
          "queues": [
            {
              "queueType": "RANKED_SOLO_5x5",
              "tier": "PLATINUM",
              "rank": "I",
              "leaguePoints": 88,
              "wins": 41,
              "losses": 39
            }
          ],
          "highestTier": "DIAMOND",
          "highestRank": "IV",
          "previousSeasonEndTier": "GOLD",
          "previousSeasonEndRank": "II"
        }
        """;

    [Fact]
    public async Task GetRankedStatsAsync_ParsesPayloadAndHitsExpectedPath()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok(Payload);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        RankedStats stats = await service.GetRankedStatsAsync(Puuid);

        RankedQueueEntry solo = Assert.IsType<RankedQueueEntry>(stats.SoloDuo);
        Assert.Equal("华贵铂金 I", solo.TierText);
        Assert.Equal("51%", solo.WinRateText);
        Assert.Equal("璀璨钻石 IV", solo.HighestTierText);

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal($"/lol-ranked/v1/ranked-stats/{Puuid}", request.Path);
        Assert.Equal(server.AuthorizationHeader, request.Authorization);
    }

    /// <summary>puuid 参与 URL 拼装时做转义，避免特殊字符把路径打坏。</summary>
    [Fact]
    public async Task GetRankedStatsAsync_EscapesPuuidInPath()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok(Payload);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        await service.GetRankedStatsAsync("a b/c");

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal("/lol-ranked/v1/ranked-stats/a%20b%2Fc", request.Path);
    }

    [Fact]
    public async Task GetRankedStatsAsync_CachesUntilInvalidated()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok(Payload);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        await service.GetRankedStatsAsync(Puuid);
        await service.GetRankedStatsAsync(Puuid);
        Assert.Single(server.Requests);

        service.InvalidateCache();
        await service.GetRankedStatsAsync(Puuid);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task GetRankedStatsAsync_ForceRefreshBypassesCache()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok(Payload);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        await service.GetRankedStatsAsync(Puuid);
        await service.GetRankedStatsAsync(Puuid, forceRefresh: true);

        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task GetRankedStatsAsync_ZeroTtlDisablesCache()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok(Payload);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport, cacheTtl: TimeSpan.Zero);

        await service.GetRankedStatsAsync(Puuid);
        await service.GetRankedStatsAsync(Puuid);

        Assert.Equal(2, server.Requests.Count);
    }

    /// <summary>未定级账号（<c>queues: []</c>）是稳定事实，应当缓存，避免每次进页面都重拉。</summary>
    [Fact]
    public async Task GetRankedStatsAsync_CachesEmptyResultForUnrankedAccount()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok("""{ "queues": [] }""");
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        RankedStats first = await service.GetRankedStatsAsync(Puuid);
        RankedStats second = await service.GetRankedStatsAsync(Puuid);

        Assert.Empty(first.Queues);
        Assert.Empty(second.Queues);
        Assert.Single(server.Requests);
    }

    /// <summary>失败不得进缓存：客户端刚启动时 404 很常见，缓存下来就连上之后还是 <c>--</c>。</summary>
    [Fact]
    public async Task GetRankedStatsAsync_DoesNotCacheFailure()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.NotFound();
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        RankedStats first = await service.GetRankedStatsAsync(Puuid);
        RankedStats second = await service.GetRankedStatsAsync(Puuid);

        Assert.Empty(first.Queues);
        Assert.Empty(second.Queues);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task GetRankedStatsAsync_ReturnsEmptyWithoutConnection()
    {
        var service = new CareerService(() => null);

        RankedStats stats = await service.GetRankedStatsAsync(Puuid);

        Assert.Empty(stats.Queues);
    }

    /// <summary>载荷被截断时不得抛异常，给空结果让界面显示 <c>--</c>。</summary>
    [Fact]
    public async Task GetRankedStatsAsync_ReturnsEmptyOnBrokenJson()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok("""{ "queues": [ { "queueType": """);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        RankedStats stats = await service.GetRankedStatsAsync(Puuid);

        Assert.Empty(stats.Queues);
    }

    /// <summary>刷新按钮连点两次只应打一次本机 GET。</summary>
    [Fact]
    public async Task GetRankedStatsAsync_ConcurrentIdenticalRequestsShareOneCall()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok(Payload);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        Task<RankedStats> first = service.GetRankedStatsAsync(Puuid);
        Task<RankedStats> second = service.GetRankedStatsAsync(Puuid);
        RankedStats[] results = await Task.WhenAll(first, second);

        Assert.Single(server.Requests);
        Assert.Equal(results[0].Queues.Count, results[1].Queues.Count);
    }

    /// <summary>不同 puuid 各自缓存，不得互相串用。</summary>
    [Fact]
    public async Task GetRankedStatsAsync_CachesPerPuuid()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = request => FakeResponse.Ok(request.Path.EndsWith("alpha", StringComparison.Ordinal)
            ? Payload
            : """{ "queues": [ { "queueType": "RANKED_SOLO_5x5", "tier": "IRON", "rank": "IV", "wins": 0, "losses": 1 } ] }""");
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        RankedStats alpha = await service.GetRankedStatsAsync("alpha");
        RankedStats beta = await service.GetRankedStatsAsync("beta");
        RankedStats alphaAgain = await service.GetRankedStatsAsync("alpha");

        Assert.Equal("华贵铂金 I", alpha.SoloDuo!.TierText);
        Assert.Equal("坚韧黑铁 IV", beta.SoloDuo!.TierText);
        Assert.Same(alpha, alphaAgain);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task GetLootOverviewAsync_ReturnsSampleWithoutConnection()
    {
        var service = new CareerService(() => null);
        PlayerLootOverview loot = await service.GetLootOverviewAsync();

        Assert.NotEmpty(loot.OpenableItems);
        Assert.NotEmpty(loot.InventoryItems);
        Assert.True(loot.TotalKeys > 0);
    }

    [Fact]
    public async Task GetMasteryOverviewAsync_ReturnsSampleWithoutConnection()
    {
        var service = new CareerService(() => null);
        MasteryOverview mastery = await service.GetMasteryOverviewAsync();

        Assert.NotEmpty(mastery.Champions);
        Assert.True(mastery.TotalChampions > 0);
        Assert.NotNull(mastery.MaxLevelHero);
    }

    private static LcuHttpTransport NewTransport(FakeLcuServer server) => new(
        server.BaseAddress,
        server.AuthorizationHeader,
        maxConcurrency: 4,
        requestTimeout: TimeSpan.FromSeconds(10),
        NullLogger<LcuHttpTransport>.Instance);
}
