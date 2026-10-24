using System.Net;
using System.Text;
using Xunit;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Tests;

public sealed class LiveClientServiceTests
{
    private sealed class MockHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Responder(request));
        }
    }

    [Fact]
    public async Task LiveClientService_GetPlayerList_ParsesValidJson()
    {
        var handler = new MockHttpHandler
        {
            Responder = req =>
            {
                if (req.RequestUri?.AbsolutePath == "/liveclientdata/playerlist")
                {
                    string json = """
                    [
                        {
                            "summonerName": "AllyPlayer",
                            "championName": "Ahri",
                            "team": "ORDER",
                            "isBot": false
                        },
                        {
                            "summonerName": "EnemyPlayer",
                            "championName": "Yasuo",
                            "team": "CHAOS",
                            "isBot": false
                        }
                    ]
                    """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            },
        };

        using var service = new LiveClientService(handler);
        var players = await service.GetPlayerListAsync();

        Assert.Equal(2, players.Count);
        Assert.Equal("AllyPlayer", players[0].SummonerName);
        Assert.Equal("Ahri", players[0].ChampionName);
        Assert.Equal("ORDER", players[0].Team);
        Assert.Equal("EnemyPlayer", players[1].SummonerName);
        Assert.Equal("Yasuo", players[1].ChampionName);
        Assert.Equal("CHAOS", players[1].Team);
    }

    [Fact]
    public async Task LiveClientService_GetActivePlayerName_ExtractsAndTrims()
    {
        var handler = new MockHttpHandler
        {
            Responder = req =>
            {
                if (req.RequestUri?.AbsolutePath == "/liveclientdata/activeplayername")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("\"  TestPlayer#CN1  \"", Encoding.UTF8, "application/json"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            },
        };

        using var service = new LiveClientService(handler);
        string? name = await service.GetActivePlayerNameAsync();
        Assert.Equal("TestPlayer#CN1", name);
    }

    [Fact]
    public async Task LiveClientService_WhenNetworkFails_ReturnsSafeFallback()
    {
        var handler = new MockHttpHandler
        {
            Responder = _ => throw new HttpRequestException("Port 2999 not ready"),
        };

        using var service = new LiveClientService(handler);
        var players = await service.GetPlayerListAsync();
        Assert.Empty(players);

        string? activeName = await service.GetActivePlayerNameAsync();
        Assert.Null(activeName);
    }

    private sealed class MockLiveClientService : ILiveClientService
    {
        public IReadOnlyList<LiveClientPlayer> Players { get; set; } = [];
        public string? ActivePlayerName { get; set; }

        public Task<IReadOnlyList<LiveClientPlayer>> GetPlayerListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Players);

        public Task<string?> GetActivePlayerNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ActivePlayerName);
    }

    [Fact]
    public async Task GameInfoService_PollLiveClientChampions_BackfillsEnemyDesensitizedPlayer()
    {
        var mockLiveClient = new MockLiveClientService
        {
            Players =
            [
                new()
                {
                    SummonerName = "MyAlly",
                    ChampionName = "Ahri",
                    Team = "ORDER",
                },
                new()
                {
                    SummonerName = "RealEnemy#123",
                    ChampionName = "MonkeyKing",
                    Team = "CHAOS",
                },
            ],
            ActivePlayerName = "MyAlly",
        };

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
            config,
            liveClientService: mockLiveClient);

        // 设置当前对局状态：红蓝两队，敌方第一人脱敏未选英雄
        var initialPlayer = new GameInfoPlayer
        {
            Info = new SummonerProfile
            {
                AccountId = 1,
                SummonerId = 101,
                DisplayName = "玩家1",
                GameName = "玩家1",
                TagLine = string.Empty,
                Puuid = string.Empty,
                SummonerLevel = 0,
                ProfileIconId = 29,
                PercentCompleteForNextLevel = 0,
                XpSinceLastLevel = 0,
                XpUntilNextLevel = 0,
            },
            ChampionId = 0,
        };

        var dict = new Dictionary<string, GameInfoPlayer> { ["5"] = initialPlayer };

        var allyPlayer = new PremadePlayerLike
        {
            CellId = 0,
            DisplayName = "MyAlly",
            GameName = "MyAlly",
            ChampionId = 103, // Ahri
        };

        var enemyPlayer = new PremadePlayerLike
        {
            CellId = 5,
            DisplayName = "玩家1",
            GameName = "玩家1",
            ChampionId = 0,
        };

        // 通过测试接口注入模拟对局状态
        service.SetStateForTest(s => s with
        {
            MyTeam = [allyPlayer],
            TheirTeam = [enemyPlayer],
            PlayerData = dict,
            MapSide = "blue", // ORDER
        });

        // 模拟触发单次轮询
        bool updated = await service.PollLiveClientChampionsOnceForTestAsync();

        // 验证：成功推导敌方 MonkeyKing -> 62，并更新了槽位与数据
        Assert.True(updated);
        Assert.Equal(62, service.CurrentState.TheirTeam[0].ChampionId);
        Assert.Equal(62, service.CurrentState.PlayerData["5"].ChampionId);

        service.Dispose();
    }
}
