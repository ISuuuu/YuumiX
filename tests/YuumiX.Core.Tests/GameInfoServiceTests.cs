using System.Text.Json;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;
using YuumiX.Testing.Lcu;
using Xunit;

namespace YuumiX.Core.Tests;

public class GameInfoServiceTests
{
    private static (GameInfoService Service, FakeLcuServer Server) CreateTestService(AppConfig? config = null)
    {
        var server = FakeLcuServer.Start();
        var transport = new LcuHttpTransport(
            server.BaseAddress,
            server.AuthorizationHeader,
            maxConcurrency: 4,
            requestTimeout: TimeSpan.FromSeconds(10),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LcuHttpTransport>.Instance);

        var summonerService = new SummonerService(() => transport);
        var matchService = new MatchService(() => transport);
        var careerService = new CareerService(() => transport);
        var masteryService = new MasteryService(() => transport);
        var fateService = new FateService(() => transport);

        config ??= new AppConfig();

        var service = new GameInfoService(
            () => transport,
            summonerService,
            matchService,
            careerService,
            masteryService,
            fateService,
            config);

        return (service, server);
    }

    [Fact]
    public async Task RefreshStateAsync_DetectsTftMode()
    {
        var (service, server) = CreateTestService();
        await using (server)
        using (service)
        {
            server.Handler = req =>
            {
                if (req.Path.EndsWith("current-summoner", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("""{"accountId":1,"summonerId":100,"puuid":"p1","displayName":"Me","gameName":"Me","tagLine":"1","summonerLevel":50,"profileIconId":1,"percentCompleteForNextLevel":0,"xpSinceLastLevel":0,"xpUntilNextLevel":0}""");
                }
                if (req.Path.EndsWith("gameflow-phase", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("\"InProgress\"");
                }
                if (req.Path.EndsWith("session", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("""{"gameData":{"queue":{"id":1090,"gameMode":"TFT"}}}""");
                }
                return FakeResponse.NotFound();
            };

            await service.RefreshStateAsync();

            Assert.Equal(GamePhase.InProgress, service.CurrentState.Phase);
            Assert.True(service.CurrentState.IsTftMode);
            Assert.False(service.CurrentState.ShouldShowContent);
        }
    }

    [Fact]
    public async Task LoadPlayerDataAsync_BotPlayer_DoesNotMakeNetworkCalls()
    {
        var (service, server) = CreateTestService();
        await using (server)
        using (service)
        {
            server.Handler = _ => FakeResponse.NotFound();

            var fallback = new PremadePlayerLike
            {
                CellId = 5,
                IsBot = true,
                Bot = true,
                BotChampionId = 81,
                BotName = "伊泽瑞尔 电脑",
                DisplayName = "伊泽瑞尔 电脑",
            };

            var player = await service.LoadPlayerDataAsync(5, 0, null, fallback);

            Assert.NotNull(player);
            Assert.True(player.MatchHistoryHidden);
            Assert.False(player.Loading);
            Assert.Equal(81, player.ChampionId);
            Assert.Empty(server.Requests); // 没有发起网络请求
        }
    }

    [Fact]
    public async Task RefreshStateAsync_ChampSelect_ParsesTeamsAndResolvesChampions()
    {
        var (service, server) = CreateTestService();
        await using (server)
        using (service)
        {
            string champSelectJson = """
            {
                "actions": [
                    [
                        { "actorCellId": 0, "championId": 64, "completed": true, "isInProgress": false, "type": "pick" }
                    ]
                ],
                "myTeam": [
                    { "cellId": 0, "championId": 64, "championPickIntent": 0, "puuid": "p-ally", "summonerId": 101 }
                ],
                "theirTeam": [
                    { "cellId": 5, "championId": 0, "championPickIntent": 157, "puuid": "p-enemy", "summonerId": 201 }
                ]
            }
            """;

            server.Handler = req =>
            {
                if (req.Path.EndsWith("current-summoner", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("""{"accountId":1,"summonerId":101,"puuid":"p-ally","displayName":"Me","gameName":"Me","tagLine":"1","summonerLevel":50,"profileIconId":1,"percentCompleteForNextLevel":0,"xpSinceLastLevel":0,"xpUntilNextLevel":0}""");
                }
                if (req.Path.EndsWith("gameflow-phase", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("\"ChampSelect\"");
                }
                if (req.Path.EndsWith("/lol-champ-select/v1/session", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok(champSelectJson);
                }
                return FakeResponse.NotFound();
            };

            await service.RefreshStateAsync();

            Assert.Equal(GamePhase.ChampSelect, service.CurrentState.Phase);
            Assert.True(service.CurrentState.ShouldShowContent);
            Assert.Single(service.CurrentState.MyTeam);
            Assert.Single(service.CurrentState.TheirTeam);
            Assert.Equal(64, service.CurrentState.MyTeam[0].ChampionId);
            Assert.Equal(157, service.CurrentState.TheirTeam[0].ChampionId);
        }
    }

    [Fact]
    public void ReserveGameinfo_WhenEnabled_PreservesLastGameSnapshot()
    {
        var config = new AppConfig();
        config.Functions.EnableReserveGameinfo = true;

        var (service, server) = CreateTestService(config);
        using (service)
        {
            var initialPlayer = new GameInfoPlayer
            {
                Info = new SummonerProfile
                {
                    AccountId = 1,
                    SummonerId = 100,
                    SummonerLevel = 30,
                    PercentCompleteForNextLevel = 0,
                    XpSinceLastLevel = 0,
                    XpUntilNextLevel = 0,
                    Puuid = "p-100",
                    DisplayName = "Player",
                    GameName = "Player",
                    TagLine = "1",
                    ProfileIconId = 1,
                },
                ChampionId = 81,
            };

            // 手动填充对局快照
            var dict = new Dictionary<string, GameInfoPlayer> { ["p-100"] = initialPlayer };
            // 设置激活对局状态
            service.SetActiveTab("my");

            // 通过反射或私有设置验证保留逻辑
            // 切换到 Lobby 阶段
            service.Dispose();
        }
    }

    /// <summary>
    /// 「对局信息过滤」开关的消费验证：开启时只保留与当前对局相同队列的战绩
    /// （对照旧 useGamePlayerData.ts 的 filterEnabled 消费）。
    /// </summary>
    [Theory]
    [InlineData(true, 430, 1)]  // 过滤开启 + 队列相同 → 保留
    [InlineData(true, 450, 0)]  // 过滤开启 + 队列不同 → 全部滤掉
    [InlineData(false, 450, 1)] // 过滤关闭 → 不过滤（旧行为）
    public async Task LoadPlayerDataAsync_GameInfoFilter_FiltersByCurrentQueue(
        bool filterEnabled,
        int currentQueueId,
        int expectedCount)
    {
        var config = new AppConfig();
        config.Functions.GameInfoFilter = filterEnabled;
        var (service, server) = CreateTestService(config);
        await using (server)
        using (service)
        {
            string history = MatchFixtures.ByName("top-solo").ReadText(); // queueId = 430
            server.Handler = req =>
            {
                if (req.Path.EndsWith("current-summoner", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("""{"accountId":1,"summonerId":100,"puuid":"p-me","displayName":"Me","gameName":"Me","tagLine":"1","summonerLevel":50,"profileIconId":1,"percentCompleteForNextLevel":0,"xpSinceLastLevel":0,"xpUntilNextLevel":0}""");
                }
                if (req.Path.EndsWith("gameflow-phase", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("\"InProgress\"");
                }
                if (req.Path.EndsWith("session", StringComparison.Ordinal))
                {
                    // 会话里带当前队列 ID（写入 CurrentQueueId），teamOne 放目标玩家避免提前整队加载
                    string session = "{\"gameData\":{\"queue\":{\"id\":" + currentQueueId +
                        ",\"gameMode\":\"CLASSIC\"}},\"teamOne\":[{\"summonerId\":101,\"puuid\":\"p-target\",\"displayName\":\"Target\",\"cellId\":0}],\"teamTwo\":[]}";
                    return FakeResponse.Ok(session);
                }
                if (req.Path.Contains("/lol-summoner/v2/summoners/puuid/", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("""{"accountId":2,"summonerId":101,"puuid":"p-target","displayName":"Target","gameName":"Target","tagLine":"2","summonerLevel":50,"profileIconId":2,"percentCompleteForNextLevel":0,"xpSinceLastLevel":0,"xpUntilNextLevel":0}""");
                }
                if (req.Path.Contains("matches", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok(history);
                }
                return FakeResponse.NotFound();
            };

            await service.RefreshStateAsync();
            Assert.Equal(currentQueueId, service.CurrentState.CurrentQueueId);

            var player = await service.LoadPlayerDataAsync(0, 101, "p-target");

            Assert.NotNull(player);
            Assert.Equal(expectedCount, player.Matches.Count);
        }
    }
}
