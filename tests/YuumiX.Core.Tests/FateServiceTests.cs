using System.Text.Json;
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Lcu;
using YuumiX.Testing.Lcu;
using Xunit;

namespace YuumiX.Core.Tests;

public class FateServiceTests
{
    [Fact]
    public void FateParser_InspectGameFate_ExtractsRelationChampionAndGameCreation()
    {
        string json = """
        {
            "queueId": 450,
            "gameCreation": 1705329000000,
            "participantIdentities": [
                { "participantId": 1, "player": { "puuid": "me-puuid", "summonerId": 1001 } },
                { "participantId": 2, "player": { "puuid": "ally-puuid", "summonerId": 1002 } },
                { "participantId": 6, "player": { "puuid": "enemy-puuid", "summonerId": 2001 } }
            ],
            "participants": [
                { "participantId": 1, "teamId": 100, "championId": 81 },
                { "participantId": 2, "teamId": 100, "championId": 64 },
                { "participantId": 6, "teamId": 200, "championId": 157 }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var detail = doc.RootElement;

        var allyRes = FateParser.InspectGameFate(detail, "ally-puuid", 1001);
        Assert.NotNull(allyRes);
        Assert.Equal("ally", allyRes.Value.Flag);
        Assert.Equal(64, allyRes.Value.ChampionId);
        Assert.Equal(1705329000000, allyRes.Value.GameCreation);

        var enemyRes = FateParser.InspectGameFate(detail, "enemy-puuid", 1001);
        Assert.NotNull(enemyRes);
        Assert.Equal("enemy", enemyRes.Value.Flag);
        Assert.Equal(157, enemyRes.Value.ChampionId);
        Assert.Equal(1705329000000, enemyRes.Value.GameCreation);

        // 自身不判定关系
        Assert.Null(FateParser.InspectGameFate(detail, "me-puuid", 1001));
    }

    [Fact]
    public void FateParser_ArenaQueue1700_UsesSubteamPlacement()
    {
        string json = """
        {
            "queueId": 1700,
            "gameCreation": 1705329000000,
            "participantIdentities": [
                { "participantId": 1, "player": { "puuid": "me-puuid", "summonerId": 1001 } },
                { "participantId": 2, "player": { "puuid": "partner-puuid", "summonerId": 1002 } },
                { "participantId": 3, "player": { "puuid": "enemy-puuid", "summonerId": 2001 } }
            ],
            "participants": [
                { "participantId": 1, "teamId": 100, "championId": 81, "stats": { "subteamPlacement": 1 } },
                { "participantId": 2, "teamId": 100, "championId": 64, "stats": { "subteamPlacement": 1 } },
                { "participantId": 3, "teamId": 100, "championId": 157, "stats": { "subteamPlacement": 2 } }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var detail = doc.RootElement;

        var allyRes = FateParser.InspectGameFate(detail, "partner-puuid", 1001);
        Assert.NotNull(allyRes);
        Assert.Equal("ally", allyRes.Value.Flag);

        var enemyRes = FateParser.InspectGameFate(detail, "enemy-puuid", 1001);
        Assert.NotNull(enemyRes);
        Assert.Equal("enemy", enemyRes.Value.Flag);
    }

    [Fact]
    public async Task FateService_AccumulatesCountsAndPrioritizesFirstHit()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        using LcuHttpTransport transport = new(
            server.BaseAddress,
            server.AuthorizationHeader,
            maxConcurrency: 4,
            requestTimeout: TimeSpan.FromSeconds(10),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LcuHttpTransport>.Instance);

        string game1Json = """
        {
            "queueId": 420,
            "gameCreation": 1000,
            "participantIdentities": [
                { "participantId": 1, "player": { "puuid": "me", "summonerId": 100 } },
                { "participantId": 2, "player": { "puuid": "target", "summonerId": 200 } }
            ],
            "participants": [
                { "participantId": 1, "teamId": 100, "championId": 1 },
                { "participantId": 2, "teamId": 100, "championId": 64 }
            ]
        }
        """;

        string game2Json = """
        {
            "queueId": 420,
            "gameCreation": 500,
            "participantIdentities": [
                { "participantId": 1, "player": { "puuid": "me", "summonerId": 100 } },
                { "participantId": 2, "player": { "puuid": "target", "summonerId": 200 } }
            ],
            "participants": [
                { "participantId": 1, "teamId": 100, "championId": 1 },
                { "participantId": 2, "teamId": 200, "championId": 157 }
            ]
        }
        """;

        server.Handler = req =>
        {
            if (req.Path.EndsWith("101", StringComparison.Ordinal))
            {
                return FakeResponse.Ok(game1Json);
            }
            if (req.Path.EndsWith("102", StringComparison.Ordinal))
            {
                return FakeResponse.Ok(game2Json);
            }
            return FakeResponse.NotFound();
        };

        var champions = new Dictionary<int, string> { [64] = "盲僧", [157] = "亚索" };
        var catalog = new GameDataCatalog(
            new Dictionary<int, string>(),
            new Dictionary<int, string>(),
            new Dictionary<int, string>(),
            champions,
            new Dictionary<int, CherryAugmentDetail>());

        var service = new FateService(() => transport, () => catalog);

        var fate = await service.GetPlayerFateInfoAsync([101, 102], "target", 100);

        Assert.Equal("ally", fate.FateFlag);
        Assert.Equal("盲僧", fate.RecentlyChampionName);
        Assert.Equal(101, fate.GameId);
        Assert.Equal(1000, fate.GameCreation);
        Assert.Equal(1, fate.AllyCount);
        Assert.Equal(1, fate.EnemyCount);
    }
}
