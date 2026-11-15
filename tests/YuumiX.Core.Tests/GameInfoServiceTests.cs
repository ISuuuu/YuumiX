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

    [Fact]
    public async Task RefreshStateAsync_WhenTransportNull_ResetsStateAndClearsError()
    {
        var config = new AppConfig();
        var summonerService = new SummonerService(() => null);
        var matchService = new MatchService(() => null);
        var careerService = new CareerService(() => null);
        var masteryService = new MasteryService(() => null);
        var fateService = new FateService(() => null);

        var service = new GameInfoService(
            () => null,
            summonerService,
            matchService,
            careerService,
            masteryService,
            fateService,
            config);

        await service.RefreshStateAsync();

        Assert.Equal(GamePhase.None, service.CurrentState.Phase);
        Assert.False(service.CurrentState.ShouldShowContent);
        Assert.Null(service.CurrentState.Error);
    }

    [Fact]
    public async Task LoadPlayerDataAsync_WhenGameSwitchesMidFlight_DiscardsStaleWriteBack()
    {
        var (service, server) = CreateTestService();
        await using (server)
        using (service)
        {
            using var oldStarted = new ManualResetEventSlim(false);
            using var allowOldFinish = new ManualResetEventSlim(false);
            int currentGame = 1;

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
                    return currentGame == 1
                        ? FakeResponse.Ok("""{"gameData":{"gameId":1001,"queue":{"id":430,"gameMode":"CLASSIC"},"teamOne":[{"summonerId":100,"puuid":"p-me","displayName":"Me","cellId":0}],"teamTwo":[{"summonerId":201,"puuid":"p-old","displayName":"OldEnemy","cellId":5}]}}""")
                        : FakeResponse.Ok("""{"gameData":{"gameId":1002,"queue":{"id":430,"gameMode":"CLASSIC"},"teamOne":[{"summonerId":100,"puuid":"p-me","displayName":"Me","cellId":0}],"teamTwo":[{"summonerId":202,"puuid":"p-new","displayName":"NewEnemy","cellId":5}]}}""");
                }
                if (req.Path.EndsWith("/lol-summoner/v2/summoners/puuid/p-old", StringComparison.Ordinal))
                {
                    oldStarted.Set();
                    allowOldFinish.Wait(TimeSpan.FromSeconds(5));
                    return FakeResponse.Ok("""{"accountId":2,"summonerId":201,"puuid":"p-old","displayName":"OldEnemy","gameName":"OldEnemy","tagLine":"1","summonerLevel":50,"profileIconId":2,"percentCompleteForNextLevel":0,"xpSinceLastLevel":0,"xpUntilNextLevel":0}""");
                }
                if (req.Path.EndsWith("/lol-summoner/v2/summoners/puuid/p-me", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("""{"accountId":1,"summonerId":100,"puuid":"p-me","displayName":"Me","gameName":"Me","tagLine":"1","summonerLevel":50,"profileIconId":1,"percentCompleteForNextLevel":0,"xpSinceLastLevel":0,"xpUntilNextLevel":0}""");
                }
                if (req.Path.EndsWith("/lol-summoner/v2/summoners/puuid/p-new", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("""{"accountId":3,"summonerId":202,"puuid":"p-new","displayName":"NewEnemy","gameName":"NewEnemy","tagLine":"2","summonerLevel":50,"profileIconId":3,"percentCompleteForNextLevel":0,"xpSinceLastLevel":0,"xpUntilNextLevel":0}""");
                }
                if (req.Path.Contains("matches", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("""{"games":{"games":[]}}""");
                }
                if (req.Path.Contains("ranked-stats", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("""{"queues":[]}""");
                }
                if (req.Path.Contains("champion-mastery", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("[]");
                }
                return FakeResponse.NotFound();
            };

            // 进入第 1 局并触发旧玩家慢加载
            await service.LoadFromGameflowSessionAsync();
            var oldLoadTask = service.LoadPlayerDataAsync(5, 201, "p-old");
            Assert.True(oldStarted.Wait(TimeSpan.FromSeconds(5)));

            // 在旧加载未完成时切换到第 2 局
            currentGame = 2;
            await service.LoadFromGameflowSessionAsync();
            var newPlayer = await service.LoadPlayerDataAsync(5, 202, "p-new");
            Assert.NotNull(newPlayer);

            // 放行第 1 局的晚到响应
            allowOldFinish.Set();
            var oldResult = await oldLoadTask;

            Assert.Null(oldResult);
            Assert.False(service.CurrentState.PlayerData.ContainsKey("p-old"));
            Assert.True(service.CurrentState.PlayerData.ContainsKey("p-new"));
        }
    }

    [Fact]
    public async Task LoadFromGameflowSessionAsync_SameGameAllNonBotLoaded_ShortCircuitsWithoutReloadingPlayers()
    {
        var (service, server) = CreateTestService();
        await using (server)
        using (service)
        {
            int sessionCalls = 0;
            int summonerCalls = 0;
            int matchCalls = 0;

            server.Handler = req =>
            {
                if (req.Path.EndsWith("current-summoner", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("""{"accountId":1,"summonerId":100,"puuid":"p-me","displayName":"Me","gameName":"Me","tagLine":"1","summonerLevel":50,"profileIconId":1,"percentCompleteForNextLevel":0,"xpSinceLastLevel":0,"xpUntilNextLevel":0}""");
                }
                if (req.Path.EndsWith("session", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref sessionCalls);
                    return FakeResponse.Ok("""{"gameData":{"gameId":2001,"queue":{"id":430,"gameMode":"CLASSIC"},"teamOne":[{"summonerId":100,"puuid":"p-me","displayName":"Me","cellId":0}],"teamTwo":[{"summonerId":202,"puuid":"p-enemy","displayName":"Enemy","cellId":5}]}}""");
                }
                if (req.Path.EndsWith("/lol-summoner/v2/summoners/puuid/p-me", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref summonerCalls);
                    return FakeResponse.Ok("""{"accountId":1,"summonerId":100,"puuid":"p-me","displayName":"Me","gameName":"Me","tagLine":"1","summonerLevel":50,"profileIconId":1,"percentCompleteForNextLevel":0,"xpSinceLastLevel":0,"xpUntilNextLevel":0}""");
                }
                if (req.Path.EndsWith("/lol-summoner/v2/summoners/puuid/p-enemy", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref summonerCalls);
                    return FakeResponse.Ok("""{"accountId":2,"summonerId":202,"puuid":"p-enemy","displayName":"Enemy","gameName":"Enemy","tagLine":"2","summonerLevel":50,"profileIconId":2,"percentCompleteForNextLevel":0,"xpSinceLastLevel":0,"xpUntilNextLevel":0}""");
                }
                if (req.Path.Contains("matches", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref matchCalls);
                    return FakeResponse.Ok("""{"games":{"games":[]}}""");
                }
                if (req.Path.Contains("ranked-stats", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("""{"queues":[]}""");
                }
                if (req.Path.Contains("champion-mastery", StringComparison.Ordinal))
                {
                    return FakeResponse.Ok("[]");
                }
                return FakeResponse.NotFound();
            };

            await service.LoadFromGameflowSessionAsync();

            // 等待首轮后台 LoadAllPlayersAsync 完成
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var pd = service.CurrentState.PlayerData;
                if (pd.TryGetValue("p-me", out var me) && !me.Loading &&
                    pd.TryGetValue("p-enemy", out var enemy) && !enemy.Loading)
                {
                    break;
                }
                await Task.Delay(20);
            }

            Assert.True(service.CurrentState.PlayerData.TryGetValue("p-me", out var loadedMe) && !loadedMe.Loading);
            Assert.True(service.CurrentState.PlayerData.TryGetValue("p-enemy", out var loadedEnemy) && !loadedEnemy.Loading);

            int sessionCallsAfterFirst = Volatile.Read(ref sessionCalls);
            int summonerCallsAfterFirst = Volatile.Read(ref summonerCalls);
            int matchCallsAfterFirst = Volatile.Read(ref matchCalls);
            Assert.True(summonerCallsAfterFirst >= 2);
            Assert.True(matchCallsAfterFirst >= 2);

            // 同局再次触发 LoadFromGameflowSessionAsync 应短路复用，不再重新拉取玩家数据
            await service.LoadFromGameflowSessionAsync();
            await Task.Delay(50);

            Assert.Equal(sessionCallsAfterFirst + 1, Volatile.Read(ref sessionCalls));
            Assert.Equal(summonerCallsAfterFirst, Volatile.Read(ref summonerCalls));
            Assert.Equal(matchCallsAfterFirst, Volatile.Read(ref matchCalls));
            Assert.Single(service.CurrentState.MyTeam);
            Assert.Single(service.CurrentState.TheirTeam);
        }
    }
}
