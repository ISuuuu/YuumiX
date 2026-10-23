using System.Text.Json;
using Xunit;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Tests;

/// <summary>
/// 解析器内部规则的单元回归，逐条对应原项目 <c>match_parser.rs</c> 的 <c>mod tests</c>。
///
/// 与夹具回归的分工：夹具回答"真实载荷算得对不对"，这里回答"规则本身对不对"。
/// 两者都要，因为夹具只覆盖 12 种组合，而分路判定有 5 个 lane × 5 个 role × 装备分支。
/// </summary>
public sealed class MatchParserRuleTests
{
    [Fact]
    public void DedupeAugments_FiltersZeroAndDuplicatesPreservingOrder()
    {
        Assert.Equal(new[] { 3, 1, 2 }, MatchAugments.Dedupe([3, 0, 1, 3, 2, 1]));
        Assert.Empty(MatchAugments.Dedupe([]));
    }

    [Fact]
    public void DedupeAugments_TruncatesToFive()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, MatchAugments.Dedupe([1, 2, 3, 4, 5, 6, 7]));
    }

    /// <summary>真实载荷没有 <c>augments</c> 数组，只有 <c>playerAugment1..5</c>；合成载荷两者都有，需合并去重。</summary>
    [Fact]
    public void ExtractAugments_MergesArrayAndSlots()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "augments": [7010, 0],
              "playerAugment1": 7018,
              "playerAugment2": 7027,
              "playerAugment3": 7010,
              "playerAugment4": 0,
              "playerAugment5": 7022
            }
            """);

        Assert.Equal(new[] { 7010, 7018, 7027, 7022 }, MatchAugments.Extract(document.RootElement));
    }

    /// <summary>详情接口把强化同时挂在 stats 与 participant 上，两处都要读且不重复。</summary>
    [Fact]
    public void ExtractAugments_ReadsBothStatsAndParticipant()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "stats": { "augments": [7010], "playerAugment1": 7018 },
              "participant": { "playerAugment1": 7018, "playerAugment2": 7027 }
            }
            """);

        Assert.Equal(
            new[] { 7010, 7018, 7027 },
            MatchAugments.Extract(
                document.RootElement.GetProperty("stats"),
                document.RootElement.GetProperty("participant")));
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(420, true)]
    [InlineData(430, true)]
    [InlineData(440, true)]
    [InlineData(490, true)]
    [InlineData(1300, true)]
    [InlineData(1900, true)]
    [InlineData(450, false)]  // 极地大乱斗
    [InlineData(2400, false)] // 海克斯大乱斗
    [InlineData(1700, false)] // 斗魂竞技场
    [InlineData(3100, false)] // 自定义对局，原实现明确排除
    [InlineData(0, false)]
    public void IsLanedMode_MatchesWhitelist(int queueId, bool expected)
    {
        Assert.Equal(expected, MatchPositionResolver.IsLanedMode(queueId));
    }

    [Theory]
    [InlineData(1090, MatchPosition.Top)]
    [InlineData(1200, MatchPosition.Top)]
    [InlineData(1220, MatchPosition.Top)]
    [InlineData(1221, MatchPosition.Top)]
    [InlineData(1091, MatchPosition.Middle)]
    [InlineData(1201, MatchPosition.Middle)]
    [InlineData(1206, MatchPosition.Middle)]
    [InlineData(1092, MatchPosition.Bottom)]
    [InlineData(1202, MatchPosition.Bottom)]
    [InlineData(1207, MatchPosition.Bottom)]
    [InlineData(1093, MatchPosition.Utility)]
    [InlineData(1203, MatchPosition.Utility)]
    [InlineData(1208, MatchPosition.Utility)]
    [InlineData(1094, MatchPosition.Jungle)]
    [InlineData(1204, MatchPosition.Jungle)]
    [InlineData(1205, MatchPosition.Jungle)]
    [InlineData(1209, MatchPosition.Jungle)]
    [InlineData(0, null)]
    [InlineData(9999, null)]
    public void ResolveRoleBoundItem_MapsAllClasses(int itemId, MatchPosition? expected)
    {
        Assert.Equal(expected, MatchPositionResolver.ResolveRoleBoundItem(itemId));
    }

    [Theory]
    [InlineData("TOP", "SOLO", MatchPosition.Top)]
    [InlineData("JUNGLE", "NONE", MatchPosition.Jungle)]
    [InlineData("MIDDLE", "SOLO", MatchPosition.Middle)]
    [InlineData("MID", "SOLO", MatchPosition.Middle)]
    [InlineData("BOTTOM", "DUO_CARRY", MatchPosition.Bottom)]
    [InlineData("BOTTOM", "DUO_SUPPORT", MatchPosition.Utility)]
    [InlineData("BOT", "SUPPORT", MatchPosition.Utility)]
    // 关键场景：排位/匹配里大量辅助玩家 lane 为 NONE、role 为 SUPPORT
    [InlineData("NONE", "SUPPORT", MatchPosition.Utility)]
    [InlineData("", "SUPPORT", MatchPosition.Utility)]
    [InlineData("NONE", "DUO_SUPPORT", MatchPosition.Utility)]
    [InlineData("NONE", "DUO_CARRY", MatchPosition.Bottom)]
    [InlineData("NONE", "NONE", null)]
    [InlineData("", "", null)]
    [InlineData(null, null, null)]
    public void ResolveFromTimeline_MapsLanesAndRoles(string? lane, string? role, MatchPosition? expected)
    {
        Assert.Equal(expected, MatchPositionResolver.ResolveFromTimeline(lane, role));
    }

    /// <summary>整条判定链的优先级：roleBoundItem 压过 timeline，且娱乐模式恒为 null。</summary>
    [Fact]
    public void Resolve_PrioritizesRoleBoundItemOverTimeline()
    {
        // lane 说打野、role 说 SOLO，但分路任务装备是上路奖励 → 上单
        Assert.Equal(
            MatchPosition.Top,
            MatchPositionResolver.Resolve(430, 1220, [0, 0, 0, 0, 0, 0, 0], 4, 12, "JUNGLE", "NONE"));

        // 无分路任务装备、无惩戒、无辅助装 → 只能信 timeline
        Assert.Equal(
            MatchPosition.Bottom,
            MatchPositionResolver.Resolve(430, 3006, [3006, 0, 0, 0, 0, 0, 0], 4, 12, "BOTTOM", "DUO_CARRY"));

        // 惩戒优先于 timeline
        Assert.Equal(
            MatchPosition.Jungle,
            MatchPositionResolver.Resolve(
                430, null, [0, 0, 0, 0, 0, 0, 0], MatchPositionResolver.SmiteSpellId, 4, "TOP", "SOLO"));

        // 辅助装先于打野装判定
        Assert.Equal(
            MatchPosition.Utility,
            MatchPositionResolver.Resolve(430, 2055, [3871, 1101, 0, 0, 0, 0, 0], 4, 12, "NONE", "NONE"));

        // 大乱斗：无论载荷给什么都不推分路
        Assert.Null(
            MatchPositionResolver.Resolve(450, 1220, [3871, 0, 0, 0, 0, 0, 0], 11, 12, "BOTTOM", "SUPPORT"));
    }

    [Theory]
    [InlineData(450, "aram")]
    [InlineData(2400, "aram")]
    [InlineData(2450, "aram")]
    [InlineData(1700, "arena")]
    [InlineData(1710, "arena")]
    [InlineData(1300, "nexus_blitz")]
    [InlineData(900, "urf")]
    [InlineData(1900, "urf")]
    [InlineData(420, "ranked")]
    [InlineData(-1, "ranked")]
    public void QueueCatalog_ToOpggMode(int queueId, string expected)
    {
        Assert.Equal(expected, MatchQueueCatalog.ToOpggMode(queueId));
    }

    [Theory]
    [InlineData(3100, "自定义模式")]
    [InlineData(99999, "自定义模式")]
    [InlineData(430, "匹配模式")]
    [InlineData(2450, "经典海斗")]
    public void QueueCatalog_ResolveName(int queueId, string expected)
    {
        Assert.Equal(expected, MatchQueueCatalog.Resolve(queueId).Name);
    }

    [Theory]
    [InlineData(11, "召唤师峡谷")]
    [InlineData(12, "嚎哭深渊")]
    [InlineData(21, "极限闪击")]
    [InlineData(30, "斗魂竞技场")]
    [InlineData(0, "未知地图")]
    public void QueueCatalog_ResolveMapName(int mapId, string expected)
    {
        Assert.Equal(expected, MatchQueueCatalog.ResolveMapName(mapId));
    }
}
