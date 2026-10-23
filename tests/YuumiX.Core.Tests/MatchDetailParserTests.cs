using System.Text.Json;
using Xunit;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Tests;

/// <summary>
/// 详情解析回归。
///
/// 详情载荷（<c>/lol-match-history/v1/games/{gameId}</c>）与列表载荷结构兼容，因此直接复用同一批夹具：
/// 真实夹具只含本人 1 名参与者（matches 接口的行为），所以这里能验证的是
/// **身份段左连接、按 teamId 分队、队伍目标数、查询玩家胜负**这条链路，
/// 而不是 10v10 渲染（那需要另一批详情夹具，等界面分支真正落地时再补）。
///
/// 另加一条跨解析器不变量：同一局从列表口径和详情口径读出来的关键字段必须一致——
/// 两边各写一套读取逻辑最容易出现"列表显示 8 杀、详情显示 9 杀"这种无法复现的错位。
/// </summary>
public sealed class MatchDetailParserTests
{
    [Fact]
    public void Ranked420_JoinsIdentityAndTeamObjectives()
    {
        MatchFixture fixture = MatchFixtures.ByName("ranked-420");
        using JsonDocument document = JsonDocument.Parse(fixture.ReadText());
        JsonElement game = FirstGame(document.RootElement);

        const string puuid = "11111111-2222-3333-4444-555555555555";
        MatchDetail? detail = MatchParser.ParseDetail(game, puuid);

        Assert.NotNull(detail);
        Assert.Equal(4839207510L, detail.GameId);
        Assert.Equal(420, detail.QueueId);
        Assert.Equal(11, detail.MapId);
        Assert.Equal("排位单双排", detail.QueueName);
        Assert.Equal("召唤师峡谷", detail.MapName);
        Assert.Equal(25, detail.DurationSeconds / 60);

        MatchDetailTeam blue = detail.Blue;
        Assert.Equal(MatchParser.BlueTeamId, blue.TeamId);
        MatchDetailPlayer player = Assert.Single(blue.Players);
        Assert.Equal(MatchParser.BlueTeamId, player.TeamId);
        Assert.Equal("TestSummoner", player.Name); // 无 tagLine 时不补 '#'
        Assert.Equal(puuid, player.Puuid);
        Assert.Equal(1234567890123L, player.SummonerId);
        Assert.Equal(7, player.ItemIds.Count);
        Assert.Equal(3340, player.ItemIds[6]); // 第 7 格是守卫

        // 队伍目标数来自 teams[]，与队员列表无关
        Assert.Equal(7, blue.TowerKills);
        Assert.Equal(2, blue.InhibitorKills);
        Assert.Equal(3, blue.DragonKills);
        Assert.Equal(0, blue.BaronKills);

        // 载荷里没有 teamId=200 的队伍，红方仍必须存在（空队），否则界面要写空判断
        Assert.Equal(MatchParser.RedTeamId, detail.Red.TeamId);
        Assert.Empty(detail.Red.Players);
        Assert.False(detail.Red.Win);
        Assert.Equal(0, detail.Red.Kills);

        Assert.True(detail.QueriedPlayerWin);
    }

    [Fact]
    public void TopSolo_CombinesGameNameAndTagLine()
    {
        MatchFixture fixture = MatchFixtures.ByName("top-solo");
        using JsonDocument document = JsonDocument.Parse(fixture.ReadText());
        JsonElement game = FirstGame(document.RootElement);

        MatchDetail? detail = MatchParser.ParseDetail(game, "<redacted>");

        Assert.NotNull(detail);
        MatchDetailPlayer player = Assert.Single(detail.Blue.Players);

        // 夹具里 gameName 与 tagLine 都被脱敏为 <redacted>，拼接规则仍是 gameName#tagLine
        Assert.Equal("<redacted>#<redacted>", player.Name);
        Assert.Equal(10, detail.Blue.TowerKills);
        Assert.Equal(1, detail.Blue.RiftHeraldKills);
        Assert.True(detail.QueriedPlayerWin);
    }

    [Fact]
    public void Minimal420_FallsBackToUnknownPlayerAndQueueMap()
    {
        MatchFixture fixture = MatchFixtures.ByName("minimal-420");
        using JsonDocument document = JsonDocument.Parse(fixture.ReadText());
        JsonElement game = FirstGame(document.RootElement);

        MatchDetail? detail = MatchParser.ParseDetail(game, "not-in-this-game");

        Assert.NotNull(detail);

        // 稀疏载荷连 teamId 都没有，按 teamId != 100 归入红方——与原实现
        // `p.teamId === 100 ? blue : red` 的分队口径一致，不做额外猜测。
        MatchDetailPlayer player = Assert.Single(detail.Red.Players);
        Assert.Empty(detail.Blue.Players);

        // 无 participantIdentities → 显示"未知"，puuid 为空（界面据此禁用"查看玩家"入口）
        Assert.Equal("未知", player.Name);
        Assert.Equal(string.Empty, player.Puuid);
        Assert.Equal(0L, player.SummonerId);

        // mapId 缺失 → 回落队列惯常地图；teams 缺失 → 全部为 0
        Assert.Equal(0, detail.MapId);
        Assert.Equal("召唤师峡谷", detail.MapName);
        Assert.Equal(0, detail.Blue.TowerKills);
        Assert.Equal(0, detail.Blue.DragonKills);

        // puuid 不在本局 → 胜负未知，必须是 null 而不是 false
        Assert.Null(detail.QueriedPlayerWin);
    }

    /// <summary>唯一可确定的口径：列表与详情对同一局的关键字段不得分歧。</summary>
    [Fact]
    public void DetailAndSummary_AgreeOnKeyFields()
    {
        foreach (MatchFixture fixture in MatchFixtures.All)
        {
            using JsonDocument document = JsonDocument.Parse(fixture.ReadText());
            JsonElement game = FirstGame(document.RootElement);

            MatchSummary summary = Assert.Single(MatchParser.ParseHistory(document.RootElement));
            MatchDetail? detail = MatchParser.ParseDetail(game);
            Assert.NotNull(detail);

            MatchDetailPlayer player = Assert.Single(detail.Blue.Players.Concat(detail.Red.Players));

            Assert.Equal(summary.GameId, detail.GameId);
            Assert.Equal(summary.QueueId, detail.QueueId);
            Assert.Equal(summary.GameCreation, detail.GameCreation);
            Assert.Equal(summary.DurationSeconds, detail.DurationSeconds);
            Assert.Equal(summary.Kills, player.Kills);
            Assert.Equal(summary.Deaths, player.Deaths);
            Assert.Equal(summary.Assists, player.Assists);
            Assert.Equal(summary.CreepScore, player.CreepScore);
            Assert.Equal(summary.Gold, player.Gold);
            Assert.Equal(summary.ItemIds, player.ItemIds);
            Assert.Equal(summary.ChampionId, player.ChampionId);
            Assert.Equal(summary.ChampionLevel, player.ChampionLevel);
            Assert.Equal(summary.RuneId, player.RuneId);
            Assert.Equal(summary.Win, player.Win);
        }
    }

    [Fact]
    public void ParseDetail_ReturnsNullWithoutParticipants()
    {
        using JsonDocument document = JsonDocument.Parse("""{"gameId":1,"queueId":430,"participants":[]}""");
        Assert.Null(MatchParser.ParseDetail(document.RootElement));
    }

    private static JsonElement FirstGame(JsonElement response) =>
        response.GetProperty("games").GetProperty("games")[0];
}
