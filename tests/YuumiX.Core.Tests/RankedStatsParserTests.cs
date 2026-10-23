using System.Text.Json;
using Xunit;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Tests;

/// <summary>
/// 排位总览解析的规则用例。
///
/// 重点锁两件事：一是"顶层字段兜底"——原实现只读队列级字段，导致「赛季最高」
/// 与「上赛季结算」两列恒为 <c>--</c>；二是格式化只在模型上做一次，
/// 胜率与段位文案必须与界面期望逐字一致。
/// </summary>
public sealed class RankedStatsParserTests
{
    private const string FullPayload =
        """
        {
          "queues": [
            {
              "queueType": "RANKED_SOLO_5x5",
              "tier": "GOLD",
              "rank": "II",
              "leaguePoints": 47,
              "wins": 30,
              "losses": 25,
              "highestTier": "PLATINUM",
              "highestRank": "IV",
              "previousSeasonEndTier": "SILVER",
              "previousSeasonEndRank": "I"
            },
            {
              "queueType": "RANKED_FLEX_SR",
              "tier": "NONE",
              "rank": "",
              "leaguePoints": 0,
              "wins": 0,
              "losses": 0
            }
          ]
        }
        """;

    [Fact]
    public void ParseRankedStats_MapsQueuesAndFormatsOnModel()
    {
        RankedStats stats = Parse(FullPayload);

        RankedQueueEntry solo = Assert.IsType<RankedQueueEntry>(stats.SoloDuo);
        Assert.Equal(47, solo.LeaguePoints);
        Assert.Equal(55, solo.TotalGames);
        Assert.Equal("55%", solo.WinRateText);
        Assert.Equal("荣耀黄金 II", solo.TierText);
        Assert.Equal("华贵铂金 IV", solo.HighestTierText);
        Assert.Equal("不屈白银 I", solo.PreviousSeasonTierText);
        Assert.True(solo.HasRank);

        RankedQueueEntry flex = Assert.IsType<RankedQueueEntry>(stats.Flex);
        Assert.Equal("--", flex.TierText);
        Assert.Equal("--", flex.WinRateText);
        Assert.Equal(0, flex.TotalGames);

        // 未定级队列没有赛季最高/上赛季字段，模型给空文本而不是英文原值
        Assert.False(flex.HasRank);
        Assert.Equal("--", flex.HighestTierText);
        Assert.Equal("--", flex.PreviousSeasonTierText);
    }

    /// <summary>
    /// 赛季最高只出现在响应顶层时，必须回填到队列条目——这是原实现丢掉的那一份数据。
    /// </summary>
    [Fact]
    public void ParseRankedStats_FallsBackToTopLevelSeasonFields()
    {
        const string payload =
            """
            {
              "queues": [
                { "queueType": "RANKED_SOLO_5x5", "tier": "DIAMOND", "rank": "III", "wins": 10, "losses": 2 }
              ],
              "highestTier": "MASTER",
              "highestRank": "NA",
              "previousSeasonEndTier": "EMERALD",
              "previousSeasonEndRank": "II"
            }
            """;

        RankedQueueEntry solo = Assert.IsType<RankedQueueEntry>(Parse(payload).SoloDuo);

        Assert.Equal("璀璨钻石 III", solo.TierText);
        // 大师及以上没有分部，不能补出「超凡大师 」的尾空格
        Assert.Equal("超凡大师", solo.HighestTierText);
        Assert.Equal("流光翡翠 II", solo.PreviousSeasonTierText);
    }

    /// <summary>队列级字段优先于顶层：两者同时存在时不得被顶层覆盖。</summary>
    [Fact]
    public void ParseRankedStats_PrefersQueueLevelSeasonFields()
    {
        const string payload =
            """
            {
              "queues": [
                {
                  "queueType": "RANKED_SOLO_5x5",
                  "tier": "GOLD", "rank": "I", "wins": 3, "losses": 3,
                  "highestTier": "PLATINUM", "highestRank": "II"
                }
              ],
              "highestTier": "CHALLENGER",
              "highestRank": "I"
            }
            """;

        RankedQueueEntry solo = Assert.IsType<RankedQueueEntry>(Parse(payload).SoloDuo);

        Assert.Equal("华贵铂金 II", solo.HighestTierText);
        Assert.Equal("50%", solo.WinRateText);
    }

    /// <summary>未定级账号就是 <c>queues: []</c>，必须给空结果而不是抛异常。</summary>
    [Theory]
    [InlineData("""{ "queues": [] }""")]
    [InlineData("""{ "queues": null }""")]
    [InlineData("""{ }""")]
    [InlineData("[]")]
    public void ParseRankedStats_ReturnsEmptyForNoQueues(string payload)
    {
        RankedStats stats = Parse(payload);

        Assert.Empty(stats.Queues);
        Assert.Null(stats.SoloDuo);
        Assert.Null(stats.Flex);
    }

    /// <summary>缺 <c>queueType</c> 的条目归不了位，跳过；其余条目照常解析。</summary>
    [Fact]
    public void ParseRankedStats_SkipsEntriesWithoutQueueType()
    {
        const string payload =
            """
            {
              "queues": [
                { "tier": "GOLD", "rank": "I", "wins": 1, "losses": 1 },
                { "queueType": "RANKED_SOLO_5x5", "tier": "SILVER", "rank": "II", "wins": 4, "losses": 1 }
              ]
            }
            """;

        RankedStats stats = Parse(payload);

        RankedQueueEntry entry = Assert.Single(stats.Queues);
        Assert.Equal(RankedQueueTypes.SoloDuo, entry.QueueType);
        Assert.Equal("不屈白银 II", entry.TierText);
        Assert.Equal("80%", entry.WinRateText);
    }

    /// <summary>字段缺失时降级为空串 / 0，不得让整份载荷解析失败。</summary>
    [Fact]
    public void ParseRankedStats_DegradesMissingFields()
    {
        const string payload =
            """
            { "queues": [ { "queueType": "RANKED_FLEX_SR" } ] }
            """;

        RankedQueueEntry entry = Assert.IsType<RankedQueueEntry>(Parse(payload).Flex);

        Assert.Equal("NONE", entry.Tier);
        Assert.Equal(string.Empty, entry.Division);
        Assert.Equal(0, entry.LeaguePoints);
        Assert.Equal("--", entry.TierText);
    }

    /// <summary>
    /// 国服 2026 赛季的真实形状（2026-10-23 实测）：单双排队列标识是
    /// <c>JADE_RANKED_SOLO_5x5</c>，分部字段叫 <c>division</c>，未定级队列的 tier 是空串。
    ///
    /// 这条用例锁定两个曾经的缺陷：只认 <c>rank</c> 会让分部丢失，
    /// 只认 <c>RANKED_SOLO_5x5</c> 会让整张排位表变成「0 场 / --」。
    /// </summary>
    [Fact]
    public void ParseRankedStats_ReadsCnJadeQueueAndDivisionField()
    {
        const string payload =
            """
            {
              "queues": [
                {
                  "queueType": "JADE_RANKED_SOLO_5x5",
                  "tier": "WOOD",
                  "division": "III",
                  "leaguePoints": 20,
                  "wins": 1,
                  "losses": 0,
                  "highestTier": "WOOD",
                  "highestDivision": "III",
                  "previousSeasonEndTier": "",
                  "previousSeasonEndDivision": "NA",
                  "isProvisional": false
                },
                {
                  "queueType": "RANKED_SOLO_5x5",
                  "tier": "",
                  "division": "NA",
                  "leaguePoints": 0,
                  "wins": 0,
                  "losses": 0,
                  "isProvisional": true
                },
                {
                  "queueType": "RANKED_FLEX_SR",
                  "tier": "",
                  "division": "NA",
                  "leaguePoints": 0,
                  "wins": 0,
                  "losses": 0,
                  "isProvisional": true
                }
              ]
            }
            """;

        RankedStats stats = Parse(payload);
        RankedQueueEntry solo = Assert.IsType<RankedQueueEntry>(stats.SoloDuo);

        // 精确标识（RANKED_SOLO_5x5）没有成绩，应回退到同族的 JADE 队列
        Assert.Equal("JADE_RANKED_SOLO_5x5", solo.QueueType);
        Assert.Equal(1, solo.TotalGames);
        Assert.Equal("100%", solo.WinRateText);
        Assert.Equal(20, solo.LeaguePoints);

        // 未知段位（WOOD）原样保留，可诊断优先于猜测中文名
        Assert.Equal("WOOD III", solo.TierText);
        Assert.Equal("WOOD III", solo.HighestTierText);
        Assert.Equal("--", solo.PreviousSeasonTierText);

        // 灵活排位未定级：tier 为空串，按"无段位"渲染
        RankedQueueEntry flex = Assert.IsType<RankedQueueEntry>(stats.Flex);
        Assert.False(flex.HasRank);
        Assert.Equal("--", flex.TierText);
    }

    /// <summary>正式队列自己有成绩时优先用它，不被同族的其它队列抢占。</summary>
    [Fact]
    public void ParseRankedStats_PrefersExactQueueWhenItHasRecord()
    {
        const string payload =
            """
            {
              "queues": [
                { "queueType": "JADE_RANKED_SOLO_5x5", "tier": "SILVER", "division": "I", "wins": 9, "losses": 1 },
                { "queueType": "RANKED_SOLO_5x5", "tier": "GOLD", "division": "IV", "wins": 2, "losses": 1 }
              ]
            }
            """;

        RankedQueueEntry solo = Assert.IsType<RankedQueueEntry>(Parse(payload).SoloDuo);

        Assert.Equal("RANKED_SOLO_5x5", solo.QueueType);
        Assert.Equal("荣耀黄金 IV", solo.TierText);
    }

    private static RankedStats Parse(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return RankedStatsParser.ParseRankedStats(document.RootElement);
    }
}
