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

    [Fact]
    public async Task GetRecentTeammatesAsync_AggregatesTeammates()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = request =>
        {
            if (request.Path == "/lol-match-history/v1/games/100")
            {
                return FakeResponse.Ok("""
                {
                  "gameId": 100, "queueId": 420, "gameCreation": 1000,
                  "participants": [
                    { "participantId": 1, "teamId": 100, "stats": { "win": true } },
                    { "participantId": 2, "teamId": 100, "stats": { "win": true } },
                    { "participantId": 3, "teamId": 200, "stats": { "win": false } }
                  ],
                  "participantIdentities": [
                    { "participantId": 1, "player": { "puuid": "my-puuid", "gameName": "Me" } },
                    { "participantId": 2, "player": { "puuid": "mate-puuid", "gameName": "Mate", "profileIcon": 50 } },
                    { "participantId": 3, "player": { "puuid": "enemy-puuid", "gameName": "Enemy", "profileIcon": 10 } }
                  ]
                }
                """);
            }
            return FakeResponse.NotFound();
        };

        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        var result = await service.GetRecentTeammatesAsync("my-puuid", [100]);

        Assert.Equal("my-puuid", result.Puuid);
        var mate = Assert.Single(result.Summoners);
        Assert.Equal("mate-puuid", mate.Puuid);
        Assert.Equal("Mate", mate.Name);
        Assert.Equal(50, mate.ProfileIconId);
        Assert.Equal(1, mate.Total);
        Assert.Equal(1, mate.Wins);
    }

    [Fact]
    public async Task OpenLootAsync_SendsCraftRequestAndParsesRewards()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = request =>
        {
            if (request.Path == "/lol-loot/v1/recipes/CHEST_hextech_OPEN/craft")
            {
                return FakeResponse.Ok("""
                {
                  "added": [
                    { "playerLoot": { "itemDesc": "至臻 灵魂莲华 提莫" } }
                  ]
                }
                """);
            }
            return FakeResponse.NotFound();
        };

        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        var result = await service.OpenLootAsync("CHEST_hextech_OPEN", ["CHEST_hextech", "MATERIAL_key"], 1);

        Assert.True(result.Success);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal("至臻 灵魂莲华 提莫", Assert.Single(result.Rewards));
    }

    [Fact]
    public async Task ForgeKeyAsync_SendsCraftRequest()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = request =>
        {
            if (request.Path == "/lol-loot/v1/recipes/MATERIAL_key_fragment_FORGE/craft")
            {
                return FakeResponse.Ok("{}");
            }
            return FakeResponse.NotFound();
        };

        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        var result = await service.ForgeKeyAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.SucceededCount);
    }

    [Fact]
    public async Task DisenchantLootsAsync_FindsRecipeAndCrafts()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = request =>
        {
            if (request.Path == "/lol-loot/v1/recipes/initial-item/CHAMPION_RENTAL_1")
            {
                return FakeResponse.Ok("""
                [
                  { "recipeName": "CHAMPION_RENTAL_disenchant" }
                ]
                """);
            }
            if (request.Path.StartsWith("/lol-loot/v1/recipes/CHAMPION_RENTAL_disenchant/craft", StringComparison.Ordinal))
            {
                return FakeResponse.Ok("{}");
            }
            return FakeResponse.NotFound();
        };

        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        var result = await service.DisenchantLootsAsync([("CHAMPION_RENTAL_1", 2)]);

        Assert.True(result.Success);
        Assert.Equal(1, result.SucceededCount);
    }

    private static LcuHttpTransport NewTransport(FakeLcuServer server) => new(
        server.BaseAddress,
        server.AuthorizationHeader,
        maxConcurrency: 4,
        requestTimeout: TimeSpan.FromSeconds(10),
        NullLogger<LcuHttpTransport>.Instance);
}
