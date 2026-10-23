using System.Text.Json;
using Xunit;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Tests;

/// <summary>
/// SGP 战绩解析（<c>/match-history-query/v1/products/lol/player/{puuid}/SUMMARY</c>）。
///
/// <b>这里没有真实夹具，是在已知缺口下用内联载荷锁定映射规则。</b>
/// 现有 12 份夹具全部来自 LCU，没有 SGP 形状的样本；按"不自己造数据"的约束，
/// 内联载荷只声明"字段名与嵌套形状"，不冒充真实响应。等能捕获到真实 SGP SUMMARY
/// （腾讯国服 + <c>entitlements</c> token）时，应把它作为夹具补进 <c>Fixtures/match/</c>，
/// 届时这组用例降级为规则补充。
///
/// 与 LCU 列表解析的核心差异：参与者**必须按 puuid 匹配**，不能取 <c>participants[0]</c>。
/// </summary>
public sealed class MatchSgpParserTests
{
    private const string TargetPuuid = "target-puuid-0001";
    private const string OtherPuuid = "other-puuid-0002";

    [Fact]
    public void ParseSgpHistory_SelectsParticipantByPuuidNotIndex()
    {
        // 目标玩家排在第二位：若退回 participants[0]，读到的是别人的战绩
        using JsonDocument document = JsonDocument.Parse(
            $$"""
            {
              "games": [
                {
                  "json": {
                    "gameId": 7001,
                    "gameCreation": 1705329000000,
                    "gameDuration": 1530,
                    "queueId": 420,
                    "participants": [
                      {
                        "puuid": "{{OtherPuuid}}",
                        "championId": 1,
                        "spell1Id": 4,
                        "spell2Id": 14,
                        "stats": { "win": false, "kills": 0, "deaths": 9, "assists": 1 }
                      },
                      {
                        "puuid": "{{TargetPuuid}}",
                        "championId": 103,
                        "spell1Id": 4,
                        "spell2Id": 14,
                        "teamPosition": "MIDDLE",
                        "stats": {
                          "win": true, "kills": 10, "deaths": 2, "assists": 5, "champLevel": 18,
                          "item0": 6655, "item1": 0, "item2": 0, "item3": 0, "item4": 0, "item5": 0, "item6": 3340,
                          "perk0": 8214, "totalMinionsKilled": 200, "neutralMinionsKilled": 6,
                          "goldEarned": 15000, "totalDamageDealtToChampions": 30000, "totalHeal": 100,
                          "playerAugment1": 7010
                        }
                      }
                    ]
                  }
                }
              ]
            }
            """);

        MatchSummary summary = Assert.Single(MatchParser.ParseSgpHistory(document.RootElement, TargetPuuid));

        Assert.Equal(7001L, summary.GameId);
        Assert.Equal(103, summary.ChampionId);   // 不是 first() 的 1
        Assert.Equal(10, summary.Kills);         // 不是 first() 的 0
        Assert.Equal("7.50", summary.KdaText);
        Assert.Equal(206, summary.CreepScore);
        Assert.Equal(8214, summary.RuneId);
        Assert.Equal(MatchPosition.Middle, summary.Position); // 来自 teamPosition
        Assert.Equal("排位单双排", summary.QueueName);
        Assert.Equal(new[] { 7010 }, summary.AugmentIds.ToArray());
    }

    /// <summary>SGP 的统计字段可能直接挂在参与者上（没有 <c>stats</c> 对象），此时按参与者读取。</summary>
    [Fact]
    public void ParseSgpHistory_FallsBackToParticipantWhenStatsMissing()
    {
        using JsonDocument document = JsonDocument.Parse(
            $$"""
            {
              "games": [
                {
                  "gameId": 7002,
                  "queueId": 420,
                  "gameCreation": 1705329000000,
                  "gameDuration": 60,
                  "participants": [
                    {
                      "puuid": "{{TargetPuuid}}",
                      "championId": 5,
                      "kills": 3,
                      "deaths": 0,
                      "assists": 4,
                      "win": true,
                      "perks": { "styles": [ { "selections": [ { "perk": 8112 } ] } ] }
                    }
                  ]
                }
              ]
            }
            """);

        MatchSummary summary = Assert.Single(MatchParser.ParseSgpHistory(document.RootElement, TargetPuuid));

        Assert.Equal(3, summary.Kills);
        Assert.Equal("Perfect", summary.KdaText);
        Assert.True(summary.Win);
        Assert.Equal(8112, summary.RuneId); // perk0 缺失 → perks.styles[0].selections[0].perk
        Assert.Equal(60, summary.DurationSeconds);
    }

    /// <summary>外层可能是 <c>{ games: { games: [...] } }</c>，两种形状都要吃。</summary>
    [Fact]
    public void ParseSgpHistory_AcceptsNestedGamesContainer()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "games": {
                "gameCount": 1,
                "games": [
                  { "gameId": 7003, "queueId": 450, "gameCreation": 1, "gameDuration": 600,
                    "participants": [ { "puuid": "target-puuid-0001", "stats": { "win": true } } ] }
                ]
              }
            }
            """);

        MatchSummary summary = Assert.Single(MatchParser.ParseSgpHistory(document.RootElement, TargetPuuid));

        Assert.Equal(7003L, summary.GameId);
        Assert.Equal("极地大乱斗", summary.QueueName);
        Assert.Null(summary.Position); // 大乱斗不推分路
    }

    /// <summary>puuid 匹配不到就整条跳过——宁可少一条，也不能把对手的战绩算成本人的。</summary>
    [Fact]
    public void ParseSgpHistory_SkipsGamesWithoutMatchingParticipant()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "games": [
                { "gameId": 7004, "queueId": 420, "participants": [ { "puuid": "someone-else" } ] }
              ]
            }
            """);

        Assert.Empty(MatchParser.ParseSgpHistory(document.RootElement, TargetPuuid));
    }

    /// <summary>puuid 比较忽略大小写：SGP 与 LCU 的 puuid 大小写并不保证一致。</summary>
    [Fact]
    public void ParseSgpHistory_MatchesPuuidCaseInsensitively()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "games": [
                { "gameId": 7005, "queueId": 420,
                  "participants": [ { "puuid": "TARGET-PUUID-0001", "stats": { "win": true, "kills": 4, "deaths": 1 } } ] }
              ]
            }
            """);

        MatchSummary summary = Assert.Single(MatchParser.ParseSgpHistory(document.RootElement, TargetPuuid));
        Assert.Equal(4, summary.Kills);
    }

    /// <summary><c>selectedPosition</c> 只是选人意向，优先级低于 <c>teamPosition</c>，但高于 timeline。</summary>
    [Fact]
    public void ParseSgpHistory_UsesSelectedPositionBeforeTimeline()
    {
        using JsonDocument document = JsonDocument.Parse(
            $$"""
            {
              "games": [
                { "gameId": 7006, "queueId": 420,
                  "participants": [ {
                    "puuid": "{{TargetPuuid}}",
                    "selectedPosition": "UTILITY",
                    "timeline": { "lane": "BOTTOM", "role": "DUO_CARRY" },
                    "stats": { "win": true }
                  } ] }
              ]
            }
            """);

        MatchSummary summary = Assert.Single(MatchParser.ParseSgpHistory(document.RootElement, TargetPuuid));
        Assert.Equal(MatchPosition.Utility, summary.Position);
    }

    [Fact]
    public void ParseSgpHistory_ReturnsEmptyForEmptyResponse()
    {
        using JsonDocument document = JsonDocument.Parse("""{"games":[]}""");
        Assert.Empty(MatchParser.ParseSgpHistory(document.RootElement, TargetPuuid));

        using JsonDocument empty = JsonDocument.Parse("{}");
        Assert.Empty(MatchParser.ParseSgpHistory(empty.RootElement, TargetPuuid));
    }
}
