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

    /// <summary>四个关键字段任一缺失即返回 null：伪造一个 ID 为 0 的对象会让后续按 puuid 的查询静默打空。</summary>
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
