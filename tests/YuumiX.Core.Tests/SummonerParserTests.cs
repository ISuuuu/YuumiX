using System.Text.Json;
using Xunit;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Tests;

/// <summary>
/// 召唤师解析回归（原 <c>summoner.rs</c> 的 <c>LcuSummoner::to_display</c>）。
///
/// 原项目没有为 <c>/lol-summoner/v1/current-summoner</c> 留夹具（真实响应含真实 Riot ID，
/// 不便入库），因此这里用最小化的内联载荷，逐条锁定"哪些字段缺失必须失败、哪些可以降级"。
/// 这类用例不冒充夹具回归：它断言的是规则，不是真实数据。
/// </summary>
public sealed class SummonerParserTests
{
    [Fact]
    public void ParseSummoner_MapsAllFields()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "accountId": 2000000001,
              "displayName": "测试玩家",
              "gameName": "测试玩家",
              "tagLine": "HN1",
              "percentCompleteForNextLevel": 42,
              "privacy": "PUBLIC",
              "profileIconId": 5213,
              "puuid": "11111111-2222-3333-4444-555555555555",
              "summonerId": 1234567890123,
              "summonerLevel": 512,
              "xpSinceLastLevel": 1000,
              "xpUntilNextLevel": 5000
            }
            """);

        SummonerProfile? profile = SummonerParser.ParseSummoner(document.RootElement);

        Assert.NotNull(profile);
        Assert.Equal(2000000001L, profile.AccountId);
        Assert.Equal("测试玩家", profile.DisplayName);
        Assert.Equal("测试玩家", profile.GameName);
        Assert.Equal("HN1", profile.TagLine);
        Assert.Equal("11111111-2222-3333-4444-555555555555", profile.Puuid);
        Assert.Equal(1234567890123L, profile.SummonerId);
        Assert.Equal(512, profile.SummonerLevel);
        Assert.Equal(5213, profile.ProfileIconId);
        Assert.Equal(42, profile.PercentCompleteForNextLevel);
        Assert.Equal(1000L, profile.XpSinceLastLevel);
        Assert.Equal(5000L, profile.XpUntilNextLevel);
        Assert.Equal("PUBLIC", profile.Privacy);
        Assert.Equal("测试玩家#HN1", profile.RiotId);
    }

    /// <summary>
    /// 国服真实载荷：<c>displayName</c> 是空串，只有 <c>gameName</c> 有值。
    /// 早先按"displayName 非空"判定会把这类账号判成数据不完整，整条链路拿不到 puuid
    /// （2026-10-23 联机验证实测，已被本用例锁定）。
    /// </summary>
    [Fact]
    public void ParseSummoner_AcceptsEmptyDisplayNameWhenGameNamePresent()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "accountId": 4001472220,
              "displayName": "",
              "gameName": "测试玩家",
              "internalName": "",
              "nameChangeFlag": false,
              "percentCompleteForNextLevel": 75,
              "privacy": "PUBLIC",
              "profileIconId": 5213,
              "puuid": "11111111-1111-1111-1111-111111111111",
              "rerollPoints": { "currentPoints": 370, "maxRolls": 2, "numberOfRolls": 1, "pointsCostToRoll": 250, "pointsToReroll": 130 },
              "summonerId": 4001472220,
              "summonerLevel": 83,
              "tagLine": "76363",
              "unnamed": false,
              "xpSinceLastLevel": 2308,
              "xpUntilNextLevel": 3072
            }
            """);

        SummonerProfile? profile = SummonerParser.ParseSummoner(document.RootElement);

        Assert.NotNull(profile);
        Assert.Equal(string.Empty, profile.DisplayName);
        Assert.Equal("测试玩家", profile.GameName);
        Assert.Equal("测试玩家#76363", profile.RiotId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", profile.Puuid);
        Assert.Equal(4001472220L, profile.SummonerId);
        Assert.Equal(83, profile.SummonerLevel);
        Assert.Equal(75, profile.PercentCompleteForNextLevel);
        Assert.Equal(2308L, profile.XpSinceLastLevel);
        Assert.Equal(3072L, profile.XpUntilNextLevel);
    }

    /// <summary>两个名字字段都为空：界面会显示空白名字，按"数据不完整"处理。</summary>
    [Fact]
    public void ParseSummoner_ReturnsNullWhenBothNamesEmpty()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "accountId": 1,
              "displayName": "",
              "gameName": "",
              "puuid": "p",
              "summonerId": 2
            }
            """);

        Assert.Null(SummonerParser.ParseSummoner(document.RootElement));
    }

    /// <summary>关键字段任一缺失即返回 null：伪造一个 ID 为 0 的对象会让后续按 puuid 的查询静默打空。</summary>
    /// <remarks>
    /// <c>displayName</c> 那一条的含义是"名字来源整体缺失"——载荷里没有 <c>gameName</c>，
    /// 再拿掉 <c>displayName</c> 就同时为空，与 <see cref="ParseSummoner_ReturnsNullWhenBothNamesEmpty"/> 等价。
    /// </remarks>
    [Theory]
    [InlineData("accountId")]
    [InlineData("displayName")]
    [InlineData("puuid")]
    [InlineData("summonerId")]
    public void ParseSummoner_ReturnsNullWhenKeyFieldMissing(string missing)
    {
        var payload = new Dictionary<string, object>
        {
            ["accountId"] = 1L,
            ["displayName"] = "测试",
            ["puuid"] = "p",
            ["summonerId"] = 2L,
        };
        payload.Remove(missing);

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(payload));

        Assert.Null(SummonerParser.ParseSummoner(document.RootElement));
    }

    /// <summary>可选字段缺失时降级为 0 / 空串，不得抛异常；tagLine 为空时 RiotId 不能补出 "名字#"。 </summary>
    [Fact]
    public void ParseSummoner_DegradesOptionalFields()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "accountId": 1,
              "displayName": "旧版名字",
              "puuid": "p",
              "summonerId": 2
            }
            """);

        SummonerProfile? profile = SummonerParser.ParseSummoner(document.RootElement);

        Assert.NotNull(profile);
        Assert.Equal(string.Empty, profile.TagLine);
        // 老账号没有 gameName，用 displayName 兜底，否则 RiotId 会拼出空名字
        Assert.Equal("旧版名字", profile.GameName);
        Assert.Equal(0, profile.SummonerLevel);
        Assert.Equal(0, profile.ProfileIconId);
        Assert.Null(profile.Privacy);
        Assert.Equal("旧版名字", profile.RiotId);
    }

    /// <summary>非对象载荷（LCU 偶发返回空响应体）返回 null，不抛异常。</summary>
    [Fact]
    public void ParseSummoner_ReturnsNullForNonObject()
    {
        using JsonDocument document = JsonDocument.Parse("null");
        Assert.Null(SummonerParser.ParseSummoner(document.RootElement));
    }
}
