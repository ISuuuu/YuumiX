using System.Text.Json;
using Xunit;
using YuumiX.Core.Features.SavedPlayers.Models;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Tests;

/// <summary>
/// 最近队友：单局解析 + 跨局聚合（原 <c>fetch_game_teammates</c> 与 <c>get_recent_teammates</c>）。
///
/// 夹具只能覆盖"单人局"（真实抓包每局只有本人一名参与者），所以
/// <see cref="RealFixture_AloneInGame_ProducesNoTeammates"/> 用夹具锁定真实载荷形状，
/// 多人同队/分队/斗魂名次/重开这些分支用内联载荷锁定规则——
/// 后者同样不冒充真实响应，只是把"字段名与判定顺序"钉住。
/// </summary>
public sealed class RecentTeammateParserTests
{
    private const string TargetPuuid = "target-puuid";
    private const string MatePuuid = "mate-puuid";
    private const string EnemyPuuid = "enemy-puuid";

    [Fact]
    public void RealFixture_AloneInGame_ProducesNoTeammates()
    {
        MatchFixture fixture = MatchFixtures.ByName("bottom-support");
        using JsonDocument document = JsonDocument.Parse(fixture.ReadText());
        JsonElement game = document.RootElement.GetProperty("games").GetProperty("games")[0];

        GameTeammates? result = RecentTeammateParser.ParseGame(game, "<redacted>");

        Assert.NotNull(result);
        Assert.Empty(result.Teammates);
        Assert.False(result.Remake);
        Assert.True(result.GameCreation > 0);
    }

    [Fact]
    public void ParseGame_CollectsSameTeamOnly_AndCombinesRiotId()
    {
        using JsonDocument document = JsonDocument.Parse(TwoVersusTwo());

        GameTeammates? result = RecentTeammateParser.ParseGame(document.RootElement, TargetPuuid);

        Assert.NotNull(result);
        Assert.False(result.Remake);
        Assert.Equal(5000L, result.GameCreation);

        TeammatePresence mate = Assert.Single(result.Teammates);
        Assert.Equal(MatePuuid, mate.Puuid);
        Assert.Equal("队友#CN1", mate.Name);
        Assert.Equal(20, mate.ProfileIconId);
        Assert.True(mate.Win);
    }

    [Fact]
    public void ParseGame_MarksRemakeFromTeamEarlySurrendered()
    {
        using JsonDocument document = JsonDocument.Parse(TwoVersusTwo(targetRemake: true));

        GameTeammates? result = RecentTeammateParser.ParseGame(document.RootElement, TargetPuuid);

        Assert.NotNull(result);
        Assert.True(result.Remake);
    }

    /// <summary>
    /// 真实 73 秒重开局的字段组合：<c>gameEndedInEarlySurrender=true</c> 而
    /// <c>teamEarlySurrendered=false</c>（2026-10-23 国服实测）。
    /// 只看后者会把重开局当成"双方皆胜"计入胜负，与该场景的界面约定相反。
    /// </summary>
    [Fact]
    public void ParseGame_MarksRemakeFromGameEndedInEarlySurrender()
    {
        using JsonDocument document = JsonDocument.Parse(TwoVersusTwo(gameEndedInEarlySurrender: true));

        GameTeammates? result = RecentTeammateParser.ParseGame(document.RootElement, TargetPuuid);

        Assert.NotNull(result);
        Assert.True(result.Remake);
    }

    /// <summary>两个字段都为假时不得判成重开——否则正常对局的胜负会被一并抹掉。</summary>
    [Fact]
    public void ParseGame_NotRemakeWhenBothFlagsFalse()
    {
        using JsonDocument document = JsonDocument.Parse(TwoVersusTwo());

        GameTeammates? result = RecentTeammateParser.ParseGame(document.RootElement, TargetPuuid);

        Assert.NotNull(result);
        Assert.False(result.Remake);
    }

    /// <summary>目标玩家不在本局时返回 null，由调用方跳过该局（不产生"空队友"记录）。</summary>
    [Fact]
    public void ParseGame_ReturnsNullWhenTargetAbsent()
    {
        using JsonDocument document = JsonDocument.Parse(TwoVersusTwo());

        Assert.Null(RecentTeammateParser.ParseGame(document.RootElement, "not-in-this-game"));
    }

    [Fact]
    public void ParseGame_ReturnsNullWithoutIdentities()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            { "gameId": 1, "queueId": 420, "participants": [ { "participantId": 1, "teamId": 100, "stats": {} } ] }
            """);

        Assert.Null(RecentTeammateParser.ParseGame(document.RootElement, TargetPuuid));
    }

    /// <summary>斗魂竞技场没有 teamId，同队关系按 <c>subteamPlacement</c>（名次）判定。</summary>
    [Fact]
    public void ParseGame_UsesSubteamPlacementForArena()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "gameId": 2, "queueId": 1700, "gameCreation": 7000,
              "participants": [
                { "participantId": 1, "stats": { "win": true, "subteamPlacement": 2 } },
                { "participantId": 2, "stats": { "win": true, "subteamPlacement": 2 } },
                { "participantId": 3, "stats": { "win": false, "subteamPlacement": 1 } }
              ],
              "participantIdentities": [
                { "participantId": 1, "player": { "puuid": "target-puuid", "gameName": "我" } },
                { "participantId": 2, "player": { "puuid": "mate-puuid", "gameName": "同队" } },
                { "participantId": 3, "player": { "puuid": "enemy-puuid", "gameName": "对手" } }
              ]
            }
            """);

        GameTeammates? result = RecentTeammateParser.ParseGame(document.RootElement, TargetPuuid);

        Assert.NotNull(result);
        Assert.Equal(MatePuuid, Assert.Single(result.Teammates).Puuid);
    }

    /// <summary>全零 puuid 是"匿名玩家"占位值，收集进来只会白占位置。</summary>
    [Fact]
    public void ParseGame_SkipsAnonymousPuuid()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "gameId": 3, "queueId": 420, "gameCreation": 8000,
              "participants": [
                { "participantId": 1, "teamId": 100, "stats": { "win": true } },
                { "participantId": 2, "teamId": 100, "stats": { "win": true } }
              ],
              "participantIdentities": [
                { "participantId": 1, "player": { "puuid": "target-puuid", "gameName": "我" } },
                { "participantId": 2, "player": { "puuid": "00000000-0000-0000-0000-000000000000", "gameName": "匿名" } }
              ]
            }
            """);

        GameTeammates? result = RecentTeammateParser.ParseGame(document.RootElement, TargetPuuid);

        Assert.NotNull(result);
        Assert.Empty(result.Teammates);
    }

    [Fact]
    public void Aggregate_CountsGamesAndSkipsWinLossOnRemake()
    {
        var games = new[]
        {
            new GameTeammates(false, 1000, [new TeammatePresence("队友#CN1", MatePuuid, 20, true)]),
            new GameTeammates(true, 3000, [new TeammatePresence("队友#CN1", MatePuuid, 20, false)]),
            new GameTeammates(false, 2000, [new TeammatePresence("队友#CN1", MatePuuid, 20, false)]),
        };

        RecentTeammatesResult result = RecentTeammateParser.Aggregate(TargetPuuid, games);

        RecentTeammate mate = Assert.Single(result.Summoners);
        Assert.Equal(3, mate.Total);      // 重开局也计入同队次数
        Assert.Equal(1, mate.Wins);
        Assert.Equal(1, mate.Losses);     // 重开局不计负
        Assert.Equal(3000L, mate.LastPlayTime); // 取最大值，与入参顺序无关
        Assert.Equal(50, mate.WinRate);
        Assert.Null(mate.Tag);
    }

    /// <summary>标记过的玩家优先显示，即使同队次数更少。</summary>
    [Fact]
    public void Aggregate_SortsTaggedFirstThenByTotal()
    {
        var games = new[]
        {
            new GameTeammates(false, 1000, [
                new TeammatePresence("常客", "puuid-a", 1, true),
                new TeammatePresence("常客", "puuid-a", 1, true),
                new TeammatePresence("路人", "puuid-b", 2, true),
            ]),
            new GameTeammates(false, 2000, [new TeammatePresence("常客", "puuid-a", 1, false)]),
        };

        var tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["puuid-b"] = "好友" };

        RecentTeammatesResult result = RecentTeammateParser.Aggregate(TargetPuuid, games, tags);

        Assert.Equal(new[] { "puuid-b", "puuid-a" }, result.Summoners.Select(s => s.Puuid).ToArray());
        Assert.Equal("好友", result.Summoners[0].Tag);
        Assert.Equal(3, result.Summoners[1].Total);
    }

    [Fact]
    public void Aggregate_TruncatesToFiveAndTieBreaksByName()
    {
        var presences = Enumerable
            .Range(1, 7)
            .Select(index => new TeammatePresence($"玩家{index}", $"puuid-{index}", index, true))
            .ToArray();

        RecentTeammatesResult result = RecentTeammateParser.Aggregate(
            TargetPuuid,
            [new GameTeammates(false, 1000, presences)]);

        Assert.Equal(RecentTeammateParser.MaxCount, result.Summoners.Count);

        // 同为 1 次同队时按名字升序，保证输出稳定（原实现依赖 HashMap 顺序，每次可能不同）
        Assert.Equal(
            Enumerable.Range(1, 5).Select(index => $"玩家{index}").ToArray(),
            result.Summoners.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Aggregate_ReturnsEmptyForNoGames()
    {
        RecentTeammatesResult result = RecentTeammateParser.Aggregate(TargetPuuid, []);

        Assert.Equal(TargetPuuid, result.Puuid);
        Assert.Empty(result.Summoners);
    }

    /// <summary>
    /// 2v2 载荷：目标玩家与一名队友同队，一名对手在另一队。
    /// <paramref name="targetRemake"/> 对应 <c>teamEarlySurrendered</c>，
    /// <paramref name="gameEndedInEarlySurrender"/> 对应真实重开局的另一个标志位——
    /// 两者都是 <c>false</c> 时才是正常对局。
    /// </summary>
    private static string TwoVersusTwo(bool targetRemake = false, bool gameEndedInEarlySurrender = false) =>
        $$"""
        {
          "gameId": 9,
          "queueId": 420,
          "gameCreation": 5000,
          "participants": [
            { "participantId": 1, "teamId": 100, "stats": { "win": true, "teamEarlySurrendered": {{(targetRemake ? "true" : "false")}}, "gameEndedInEarlySurrender": {{(gameEndedInEarlySurrender ? "true" : "false")}} } },
            { "participantId": 2, "teamId": 100, "stats": { "win": true, "teamEarlySurrendered": false, "gameEndedInEarlySurrender": {{(gameEndedInEarlySurrender ? "true" : "false")}} } },
            { "participantId": 3, "teamId": 200, "stats": { "win": false } }
          ],
          "participantIdentities": [
            { "participantId": 1, "player": { "puuid": "{{TargetPuuid}}", "gameName": "我", "tagLine": "CN1", "profileIcon": 10 } },
            { "participantId": 2, "player": { "puuid": "{{MatePuuid}}", "gameName": "队友", "tagLine": "CN1", "profileIcon": 20 } },
            { "participantId": 3, "player": { "puuid": "{{EnemyPuuid}}", "gameName": "对手", "profileIcon": 30 } }
          ]
        }
        """;
}
