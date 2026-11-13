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

    /// <summary>D1：未连接返回 <c>null</c>（界面给失败提示），而不是样例数据。</summary>
    [Fact]
    public async Task GetLootOverviewAsync_ReturnsNullWithoutConnection()
    {
        var service = new CareerService(() => null);
        PlayerLootOverview? loot = await service.GetLootOverviewAsync();

        Assert.Null(loot);
    }

    /// <summary>D1：读取失败同样返回 <c>null</c>，绝不静默回退样例数据（演示数据会以假乱真）。</summary>
    [Fact]
    public async Task GetLootOverviewAsync_ReturnsNullOnFailure()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.NotFound();
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        PlayerLootOverview? loot = await service.GetLootOverviewAsync();

        Assert.Null(loot);
    }

    /// <summary>空账号（载荷为空数组）是成功结果：返回全空概览而不是 <c>null</c>，界面给普通空态。</summary>
    [Fact]
    public async Task GetLootOverviewAsync_ReturnsEmptyOverviewForFreshAccount()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok("[]");
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        PlayerLootOverview? loot = await service.GetLootOverviewAsync();

        Assert.NotNull(loot);
        Assert.Empty(loot!.OpenableItems);
        Assert.Empty(loot.InventoryItems);
        Assert.Equal(0, loot.TotalKeys);
    }

    /// <summary>
    /// 配方与钥匙配对一律取客户端定义（A2）：配方名来自 <c>recipes/initial-item</c>，
    /// 是否需要钥匙看配方槽位数量，而不是猜 lootId。
    /// 实机反例：CHEST_128 这类里程碑宝箱免钥匙，CHEST_champion_mastery 反而要海克斯钥匙——
    /// 按 lootId 猜会多塞或少塞 MATERIAL_key，craft 直接返回 500。
    /// </summary>
    [Fact]
    public async Task GetLootOverviewAsync_ResolvesRecipeAndKeyFromClientSlots()
    {
        const string Loot = """
        [
          { "lootId": "CHEST_128", "itemDesc": "英雄魔法引擎", "count": 3, "displayCategories": "CHEST" },
          { "lootId": "CHEST_champion_mastery", "itemDesc": "成就传送门", "count": 2, "displayCategories": "CHEST" },
          { "lootId": "CHEST_masterwork_premium", "itemDesc": "杰作宝箱", "count": 2, "displayCategories": "CHEST" },
          { "lootId": "MATERIAL_key", "itemDesc": "海克斯科技钥匙", "count": 4, "displayCategories": "MATERIAL" },
          { "lootId": "MATERIAL_key_premium", "itemDesc": "杰作钥匙", "count": 1, "displayCategories": "MATERIAL" }
        ]
        """;

        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = request => request.Path switch
        {
            "/lol-loot/v1/recipes/initial-item/CHEST_128" => FakeResponse.Ok("""
            [ { "recipeName": "CHEST_128_OPEN", "type": "OPEN",
                "slots": [ { "lootIds": [ "CHEST_128" ], "quantity": 1, "slotNumber": 0 } ] } ]
            """),
            "/lol-loot/v1/recipes/initial-item/CHEST_champion_mastery" => FakeResponse.Ok("""
            [ { "recipeName": "CHEST_champion_mastery_OPEN", "type": "OPEN",
                "slots": [
                  { "lootIds": [ "CHEST_champion_mastery" ], "quantity": 1, "slotNumber": 0 },
                  { "lootIds": [ "MATERIAL_key" ], "quantity": 1, "slotNumber": 1 } ] } ]
            """),
            "/lol-loot/v1/recipes/initial-item/CHEST_masterwork_premium" => FakeResponse.Ok("""
            [ { "recipeName": "CHEST_masterwork_premium_OPEN", "type": "OPEN",
                "slots": [
                  { "lootIds": [ "CHEST_masterwork_premium" ], "quantity": 1, "slotNumber": 0 },
                  { "lootIds": [ "MATERIAL_key_premium" ], "quantity": 1, "slotNumber": 1 } ] } ]
            """),
            "/lol-loot/v1/player-loot" => FakeResponse.Ok(Loot),
            _ => FakeResponse.NotFound()
        };
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        PlayerLootOverview? loot = await service.GetLootOverviewAsync();
        Assert.NotNull(loot);

        OpenableLootItem milestone = Assert.Single(loot!.OpenableItems, item => item.LootId == "CHEST_128");
        Assert.Equal("CHEST_128_OPEN", milestone.RecipeName);
        Assert.False(milestone.NeedKey);
        Assert.Null(milestone.KeyLootId);
        Assert.Equal(0, milestone.KeyCount);

        OpenableLootItem mastery = Assert.Single(loot.OpenableItems, item => item.LootId == "CHEST_champion_mastery");
        Assert.Equal("CHEST_champion_mastery_OPEN", mastery.RecipeName);
        Assert.True(mastery.NeedKey);
        Assert.Equal("MATERIAL_key", mastery.KeyLootId);
        Assert.Equal(4, mastery.KeyCount);

        OpenableLootItem masterwork = Assert.Single(loot.OpenableItems, item => item.LootId == "CHEST_masterwork_premium");
        Assert.Equal("MATERIAL_key_premium", masterwork.KeyLootId);
        Assert.Equal(1, masterwork.KeyCount);

        Assert.Equal(5, loot.TotalKeys);
    }

    /// <summary>拿不到配方的可开启项不进列表：猜配方只会换来一次必然失败的 craft。</summary>
    [Fact]
    public async Task GetLootOverviewAsync_SkipsOpenableWithoutRecipe()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = request => request.Path == "/lol-loot/v1/player-loot"
            ? FakeResponse.Ok("""
            [ { "lootId": "CHEST_unknown", "itemDesc": "未知宝箱", "count": 1, "displayCategories": "CHEST" } ]
            """)
            : FakeResponse.NotFound();
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        PlayerLootOverview? loot = await service.GetLootOverviewAsync();

        Assert.NotNull(loot);
        Assert.Empty(loot!.OpenableItems);
    }

    /// <summary>D3 数据基础：库存解析必须带出 lootType / upgradeEssenceValue / parentItemStatus，
    /// 否则永恒星碑筛选与升级前置校验（缺英雄名单、精粹费用）都无法工作。</summary>
    [Fact]
    public async Task GetLootOverviewAsync_ParsesUpgradeAndStatstoneFields()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok("""
        [
          {
            "lootId": "SKIN_RENTAL_266001",
            "itemDesc": "仲裁圣骑 亚托克斯",
            "displayCategories": "SKIN",
            "lootType": "SKIN",
            "count": 1,
            "value": 6900,
            "disenchantValue": 1050,
            "itemStatus": "NOT_OWNED",
            "parentItemStatus": "NOT_OWNED",
            "upgradeRecipeName": "SKIN_RENTAL_266001_upgrade",
            "upgradeEssenceValue": 5850
          },
          {
            "lootId": "STATSTONE_SHARD_1",
            "itemDesc": "永恒星碑碎片",
            "displayCategories": "MATERIAL",
            "lootType": "STATSTONE",
            "count": 2,
            "itemStatus": "NONE"
          }
        ]
        """);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        PlayerLootOverview? loot = await service.GetLootOverviewAsync();
        Assert.NotNull(loot);

        LootInventoryItem skin = Assert.Single(loot!.InventoryItems, item => item.LootId == "SKIN_RENTAL_266001");
        Assert.Equal(5850, skin.UpgradeEssenceCost);
        Assert.Equal("SKIN_RENTAL_266001_upgrade", skin.UpgradeRecipeName);
        Assert.Equal("NOT_OWNED", skin.ParentItemStatus);

        LootInventoryItem statstone = Assert.Single(loot.InventoryItems, item => item.LootId == "STATSTONE_SHARD_1");
        Assert.True(statstone.IsStatstone);
    }

    /// <summary>
    /// 战利品是生涯页最重的请求（整份库存列表），TTL 内必须命中缓存：
    /// 否则每次进页、切页签、切回页面都要真打一遍全量 GET。
    /// </summary>
    [Fact]
    public async Task GetLootOverviewAsync_HitsCacheWithinTtl()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok("[]");
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        PlayerLootOverview? first = await service.GetLootOverviewAsync();
        PlayerLootOverview? second = await service.GetLootOverviewAsync();

        Assert.Same(first, second);
        Assert.Single(server.Requests);
    }

    /// <summary>
    /// 刷新按钮与战利品操作后的 OnRefresh 都带 forceRefresh：必须绕过并覆盖缓存，
    /// 否则开箱/分解完还显示旧库存。
    /// </summary>
    [Fact]
    public async Task GetLootOverviewAsync_ForceRefreshBypassesCache()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok("[]");
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        await service.GetLootOverviewAsync();
        await service.GetLootOverviewAsync(forceRefresh: true);

        Assert.Equal(2, server.Requests.Count);
    }

    /// <summary>
    /// 失败不进缓存：未连接/HTTP 失败是暂时状态，缓存它会让客户端连上之后
    /// 重试也拿不到数据，界面一直停在失败提示上。
    /// </summary>
    [Fact]
    public async Task GetLootOverviewAsync_DoesNotCacheFailure()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.NotFound();
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        Assert.Null(await service.GetLootOverviewAsync());

        server.Handler = _ => FakeResponse.Ok("[]");
        Assert.NotNull(await service.GetLootOverviewAsync());

        Assert.Equal(2, server.Requests.Count);
    }

    /// <summary>D1：熟练度读取失败返回 <c>null</c>，绝不静默回退样例数据。</summary>
    [Fact]
    public async Task GetMasteryOverviewAsync_ReturnsNullWithoutConnection()
    {
        var service = new CareerService(() => null);
        MasteryOverview? mastery = await service.GetMasteryOverviewAsync();

        Assert.Null(mastery);
    }

    /// <summary>空账号（载荷为空数组）是成功结果：返回全空概览而不是 <c>null</c>。</summary>
    [Fact]
    public async Task GetMasteryOverviewAsync_ReturnsEmptyOverviewForFreshAccount()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok("[]");
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        MasteryOverview? mastery = await service.GetMasteryOverviewAsync();

        Assert.NotNull(mastery);
        Assert.Empty(mastery!.Champions);
        Assert.Equal(0, mastery.TotalChampions);
    }

    /// <summary>
    /// 熟练度是全英雄记录（几十到上百条），与战利品同一套缓存语义：
    /// TTL 内命中缓存，forceRefresh 绕过。
    /// </summary>
    [Fact]
    public async Task GetMasteryOverviewAsync_HitsCacheWithinTtlAndHonorsForceRefresh()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok("[]");
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        MasteryOverview? first = await service.GetMasteryOverviewAsync();
        MasteryOverview? second = await service.GetMasteryOverviewAsync();
        MasteryOverview? refreshed = await service.GetMasteryOverviewAsync(forceRefresh: true);

        Assert.Same(first, second);
        Assert.NotNull(refreshed);
        Assert.Equal(2, server.Requests.Count);
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

    /// <summary>钥匙碎片合成走数量弹窗（旧版 batchOpenLoots 语义）：固定 FORGE 配方 + repeatCount。</summary>
    [Fact]
    public async Task OpenLootAsync_ForgesKeysWithFixedRecipeAndRepeatCount()
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

        var result = await service.OpenLootAsync("MATERIAL_key_fragment_FORGE", ["MATERIAL_key_fragment"], 3);

        Assert.True(result.Success);
        Assert.Equal(3, result.SucceededCount);
        Assert.Equal(3, server.Requests.Count(r => r.Path.Contains("/craft")));
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

    /// <summary>
    /// 重铸（3合1）：入参已由调用方按同类切成 3 的倍数，每 3 个一组原样送出——
    /// 同类的不同碎片可以混组（客户端重随配方的槽位是同类道具池），
    /// 按 lootId 才成组会要求同一件碎片有 3 个，皮肤碎片几乎永远凑不齐。
    /// </summary>
    [Fact]
    public async Task RerollLootsAsync_SendsOneCraftPerGroupOfThree()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = request =>
        {
            if (request.Path.StartsWith("/lol-loot/v1/recipes/initial-item/", StringComparison.Ordinal))
            {
                return FakeResponse.Ok("""[ { "recipeName": "SKIN_reroll", "type": "REROLL" } ]""");
            }
            if (request.Path.StartsWith("/lol-loot/v1/recipes/SKIN_reroll/craft", StringComparison.Ordinal))
            {
                return FakeResponse.Ok("""{ "added": [ { "playerLoot": { "itemDesc": "永久皮肤" } } ] }""");
            }
            return FakeResponse.NotFound();
        };

        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        var result = await service.RerollLootsAsync(
        [
            "SKIN_RENTAL_1", "SKIN_RENTAL_2", "SKIN_RENTAL_3",
            "SKIN_RENTAL_4", "SKIN_RENTAL_5", "SKIN_RENTAL_6",
        ]);

        Assert.True(result.Success);
        Assert.Equal(2, result.SucceededCount);

        var crafts = server.Requests.Where(r => r.Path.Contains("/craft")).ToList();
        Assert.Equal(2, crafts.Count);
        Assert.Equal("""["SKIN_RENTAL_1","SKIN_RENTAL_2","SKIN_RENTAL_3"]""", crafts[0].Body);
        Assert.Equal("""["SKIN_RENTAL_4","SKIN_RENTAL_5","SKIN_RENTAL_6"]""", crafts[1].Body);
    }

    /// <summary>
    /// 重铸配方按客户端 type 匹配：表情的真实配方叫 EMOTE_reroll（type=REROLL），
    /// 旧实现按「EMOTE 前缀 → forge 关键字」猜，永远找不到配方。
    /// </summary>
    [Fact]
    public async Task RerollLootsAsync_FindsRecipeByTypeForEmotes()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = request =>
        {
            if (request.Path.StartsWith("/lol-loot/v1/recipes/initial-item/", StringComparison.Ordinal))
            {
                return FakeResponse.Ok("""
                [
                  { "recipeName": "EMOTE_disenchant", "type": "DISENCHANT" },
                  { "recipeName": "EMOTE_reroll", "type": "REROLL" }
                ]
                """);
            }
            if (request.Path.StartsWith("/lol-loot/v1/recipes/EMOTE_reroll/craft", StringComparison.Ordinal))
            {
                return FakeResponse.Ok("{}");
            }
            return FakeResponse.NotFound();
        };

        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        var result = await service.RerollLootsAsync(["EMOTE_1", "EMOTE_1", "EMOTE_1"]);

        Assert.True(result.Success);
        Assert.Equal(1, result.SucceededCount);
    }

    /// <summary>
    /// 智能一键开启：钥匙按类型分别记账——账号只有普通钥匙时，
    /// 杰作宝箱不应被排进批次（否则会发出一次必然失败的 craft）。
    /// </summary>
    [Fact]
    public async Task SmartOpenAllLootsAsync_RespectsPerKeyBudget()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = request =>
        {
            if (request.Path == "/lol-loot/v1/player-loot")
            {
                return FakeResponse.Ok("""
                [
                  { "lootId": "CHEST_masterwork_premium", "itemDesc": "杰作宝箱", "count": 1, "displayCategories": "CHEST" },
                  { "lootId": "MATERIAL_key", "itemDesc": "海克斯科技钥匙", "count": 5, "displayCategories": "MATERIAL" }
                ]
                """);
            }
            if (request.Path.StartsWith("/lol-loot/v1/recipes/initial-item/CHEST_masterwork_premium", StringComparison.Ordinal))
            {
                return FakeResponse.Ok("""
                [ { "recipeName": "CHEST_masterwork_premium_OPEN", "type": "OPEN",
                    "slots": [
                      { "lootIds": [ "CHEST_masterwork_premium" ], "quantity": 1, "slotNumber": 0 },
                      { "lootIds": [ "MATERIAL_key_premium" ], "quantity": 1, "slotNumber": 1 } ] } ]
                """);
            }
            if (request.Path.Contains("/craft", StringComparison.Ordinal))
            {
                return FakeResponse.Ok("{}");
            }
            return FakeResponse.NotFound();
        };

        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        var result = await service.SmartOpenAllLootsAsync();

        Assert.True(result.Success);
        Assert.Equal(0, result.SucceededCount);
        Assert.DoesNotContain(server.Requests, r => r.Path.Contains("/craft", StringComparison.Ordinal));
    }

    /// <summary>长度不是 3 的倍数时给失败结果（界面据此提示"无法重随"），不打任何请求。</summary>
    [Fact]
    public async Task RerollLootsAsync_FailsWhenNoGroupAvailable()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        using LcuHttpTransport transport = NewTransport(server);
        var service = new CareerService(() => transport);

        var result = await service.RerollLootsAsync(["CHAMPION_RENTAL_1", "CHAMPION_RENTAL_2"]);

        Assert.False(result.Success);
        Assert.Empty(server.Requests);
    }

    private static LcuHttpTransport NewTransport(FakeLcuServer server) => new(
        server.BaseAddress,
        server.AuthorizationHeader,
        maxConcurrency: 4,
        requestTimeout: TimeSpan.FromSeconds(10),
        NullLogger<LcuHttpTransport>.Instance);
}
