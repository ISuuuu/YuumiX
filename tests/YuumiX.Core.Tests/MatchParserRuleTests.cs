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

    /// <summary>
    /// 整条判定链的优先级：<c>roleBoundItem</c> 压过一切；无可靠信号时宁可返回 <c>null</c> 也不猜。
    /// </summary>
    [Fact]
    public void Resolve_PrioritizesRoleBoundItemAndRefusesToGuess()
    {
        // 分路任务装备是上路奖励 → 上单（旧实现会被 lane=JUNGLE 带偏，这正是去掉 timeline 的原因）
        Assert.Equal(
            MatchPosition.Top,
            MatchPositionResolver.Resolve(430, 1220, [0, 0, 0, 0, 0, 0, 0], 4, 12));

        // 字段为 0、没出任何带分路语义的装备、也没有 SGP 位置字段 → 不猜，返回 null
        Assert.Null(MatchPositionResolver.Resolve(430, 0, [0, 0, 0, 0, 0, 0, 0], 4, 12));

        // roleBoundItem 为靴子 → 直接判下路（下路任务奖励就是靴子升级）
        Assert.Equal(
            MatchPosition.Bottom,
            MatchPositionResolver.Resolve(430, 3006, [0, 0, 0, 0, 0, 0, 0], 4, 12));

        // 惩戒 → 打野
        Assert.Equal(
            MatchPosition.Jungle,
            MatchPositionResolver.Resolve(
                430, null, [0, 0, 0, 0, 0, 0, 0], MatchPositionResolver.SmiteSpellId, 4));

        // 辅助装先于打野装判定
        Assert.Equal(
            MatchPosition.Utility,
            MatchPositionResolver.Resolve(430, 2055, [3871, 1101, 0, 0, 0, 0, 0], 4, 12));

        // 大乱斗：无论载荷给什么都不推分路
        Assert.Null(
            MatchPositionResolver.Resolve(450, 1220, [3871, 0, 0, 0, 0, 0, 0], 11, 12));
    }

    /// <summary>
    /// SGP 的 individualPosition 必须压过 teamPosition，且仍低于分路任务装备。
    ///
    /// 两条断言都来自国服实测（2026-10-29，430 盲选）：
    /// <c>gid=11420374915</c> 是下路 AD（CS 86），官方 teamPosition 给 UTILITY（判成辅助），
    /// 同局 individualPosition 才是 BOTTOM；<c>gid=11390689646</c> 是辅助（辅助装 3865），
    /// 分路任务装备 1203 与 individualPosition 一致，teamPosition 却给 TOP。
    /// </summary>
    [Fact]
    public void Resolve_PrefersIndividualPositionOverTeamPosition()
    {
        // 字段与装备栏都给不出分路（roleBoundItem=0）→ 落到 individualPosition
        Assert.Equal(
            MatchPosition.Bottom,
            MatchPositionResolver.Resolve(
                430,
                0,
                [1056, 2031, 3087, 3147, 0, 0, 3364],
                4,
                14,
                individualPosition: "BOTTOM",
                teamPosition: "UTILITY"));

        // 分路任务装备（辅助任务 1203）仍是最高优先级，压过 individualPosition 与 teamPosition
        Assert.Equal(
            MatchPosition.Utility,
            MatchPositionResolver.Resolve(
                430,
                1203,
                [3865, 2003, 0, 0, 0, 0, 3340],
                4,
                14,
                individualPosition: "BOTTOM",
                teamPosition: "BOTTOM"));
    }

    /// <summary>
    /// <c>roleBoundItem</c> 的靴子 / 控制守卫分支：与 12xx 任务道具同档，压过全部位置字段。
    /// 取值集合依据 50 局实测（同队五人字段值互不重复、恰好覆盖五路）。
    /// </summary>
    [Theory]
    [InlineData(1001)] // 鞋子
    [InlineData(2422)] // 有点神奇之鞋
    [InlineData(3006)] // 狂战士胫甲
    [InlineData(3009)] // 轻灵之靴
    [InlineData(3020)] // 法师之靴
    [InlineData(3047)] // 铁板靴
    [InlineData(3111)] // 水银之靴
    [InlineData(3158)] // 明朗之靴
    [InlineData(3172)] // 炮铜胫甲（3006 升级版）
    [InlineData(3175)] // 灵能使之靴（3020 升级版）
    public void ResolveRoleBoundPosition_MapsBootsToBottom(int itemId)
    {
        Assert.True(MatchPositionResolver.IsBootsItem(itemId));
        Assert.Equal(MatchPosition.Bottom, MatchPositionResolver.ResolveRoleBoundPosition(itemId));
    }

    /// <summary>2055 控制守卫是辅助任务的绑定物；在装备栏里则不具备分路含义。</summary>
    [Fact]
    public void ResolveRoleBoundPosition_MapsControlWardToUtility()
    {
        Assert.Equal(MatchPosition.Utility, MatchPositionResolver.ResolveRoleBoundPosition(2055));

        // 装备栏里买了真眼不构成辅助证据：该玩家应按分路任务装备判上单
        Assert.Equal(
            MatchPosition.Top,
            MatchPositionResolver.Resolve(430, 1220, [2055, 1001, 0, 0, 0, 0, 3340], 4, 12));
    }

    /// <summary>
    /// 靴子规则<b>不得</b>泄漏到装备栏扫描：否则任何穿鞋的玩家都会被判成下路。
    /// 同时验证字段缺失、只剩一双鞋时不会拿 timeline 硬猜。
    /// </summary>
    [Theory]
    [InlineData(3006)]
    [InlineData(3158)]
    [InlineData(3172)]
    public void Resolve_DoesNotTreatBootsInInventoryAsBottom(int bootsId)
    {
        // 装备栏只有一双鞋、无任务道具、无位置字段 → 判不出分路，返回 null（不得是 Bottom）
        Assert.Null(MatchPositionResolver.Resolve(430, 0, [bootsId, 1055, 3078, 0, 0, 0, 3340], 4, 12));

        // 该装备在"任务道具"映射里必须仍然无解
        Assert.Null(MatchPositionResolver.ResolveRoleBoundItem(bootsId));
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
