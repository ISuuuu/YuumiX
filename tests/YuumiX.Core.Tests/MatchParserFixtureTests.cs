using System.Collections;
using System.Globalization;
using System.Text.Json;
using Xunit;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Tests;

/// <summary>
/// 真实夹具的逐字段回归。
///
/// 期望值**原样取自原项目 <c>match_parser.rs</c> 的 <c>REAL_EXPECT</c> 表**
/// （该表由真实抓包跑出，并在原仓库的 <c>real_fixtures_produce_expected_display</c> 中锁定），
/// 本文件只做两件事：搬运，以及把时间字段从 UTC 换算到本地时区。
///
/// 换算的必要性：原实现输出 UTC（见 <c>MatchTimeText</c> 的说明），本项目已改为本地时区。
/// 若照抄 UTC 字面量做断言，在任何非 UTC 机器上都会红；若把断言改成"和本地时间一致"，
/// 又等于丢掉原表这个事实来源。故保留 UTC 字面量，比较时统一换算——
/// 这样"原项目认为这局是几点"与"我们显示成几点"两个信息都不会丢。
/// </summary>
public sealed class MatchParserFixtureTests
{
    /// <summary>原项目 <c>REAL_EXPECT</c> 的一条记录。</summary>
    private sealed record RealExpectation(
        string File,
        int QueueId,
        bool Win,
        bool Remake,
        string UtcTime,
        string Duration,
        string Kda,
        int CreepScore,
        int Gold,
        int Damage,
        int Heal,
        int ChampionLevel,
        int ChampionId,
        int? RoleBoundItem,
        MatchPosition? Position,
        int[] ItemIds);

    private static readonly RealExpectation[] RealExpectations =
    [
        new(
            "real/top-solo", 430, true, false, "2026-10-21 10:34", "28:42", "1.40",
            218, 11827, 14104, 2636, 17, 17, 1220, MatchPosition.Top,
            [1056, 3115, 3006, 3087, 6653, 1028, 3364]),

        new(
            "real/mid-solo", 430, true, false, "2026-10-21 14:03", "30:05", "7.00",
            244, 15590, 29975, 4646, 17, 17, 1206, MatchPosition.Middle,
            [1056, 3157, 3115, 3172, 6653, 3087, 3364]),

        // LCU 报 lane=JUNGLE，但 roleBoundItem=1220 且无惩戒，实际为上单。
        new(
            "real/jungle-none", 430, true, false, "2026-10-20 14:03", "33:00", "2.29",
            263, 16626, 36782, 3498, 18, 17, 1220, MatchPosition.Top,
            [3115, 2420, 3087, 3006, 6653, 2510, 3364]),

        // roleBoundItem=3006（非分路任务道具）→ 必须降级到 timeline 才得到 BOTTOM。
        new(
            "real/bottom-carry", 430, false, false, "2026-10-20 13:17", "41:28", "1.31",
            239, 19369, 30158, 9401, 17, 18, 3006, MatchPosition.Bottom,
            [6676, 3026, 3031, 3072, 6675, 3035, 3363]),

        // roleBoundItem=2055（控制守卫）→ 装备栏含 3871 辅助装 → UTILITY。
        new(
            "real/bottom-support", 430, true, false, "2026-10-16 13:21", "27:49", "1.67",
            47, 8150, 17563, 3599, 13, 63, 2055, MatchPosition.Utility,
            [3871, 3020, 2031, 2503, 6653, 0, 3364]),

        // lane=NONE / role=DUO，只靠 role 兜底会误判 BOTTOM；靠 1220 纠正为上单。
        new(
            "real/none-duo", 430, false, false, "2026-10-19 13:39", "19:21", "2.00",
            159, 7786, 16792, 1941, 14, 17, 1220, MatchPosition.Top,
            [3006, 3087, 1026, 1056, 1043, 3108, 3364]),

        new(
            "real/remake", 430, true, true, "2026-10-21 13:33", "01:13", "Perfect",
            4, 596, 41, 0, 1, 63, 1201, MatchPosition.Middle,
            [1056, 2003, 0, 0, 0, 0, 3340]),

        // 队列 3100 不在分路白名单 → 即使 roleBoundItem 为 0、timeline 给了 SUPPORT 也不推分路。
        new(
            "real/custom-3100", 3100, false, false, "2026-10-19 14:51", "08:07", "2.50",
            24, 3304, 6821, 1541, 6, 805, 0, null,
            [1001, 2031, 1052, 1056, 3057, 0, 3340]),
    ];

    [Fact]
    public void RealFixtures_MatchOriginalExpectationTable()
    {
        Assert.Equal(MatchFixtures.ExpectedRealCount, RealExpectations.Length);

        foreach (RealExpectation expected in RealExpectations)
        {
            MatchFixture fixture = MatchFixtures.All.Single(f => f.File == expected.File + ".json");
            MatchSummary summary = ParseSingle(fixture);
            string label = fixture.Label;
            MatchQueueInfo queue = MatchQueueCatalog.Resolve(expected.QueueId);

            Expect(label, "queueId", expected.QueueId, summary.QueueId);
            Expect(label, "queueName", queue.Name, summary.QueueName);
            Expect(label, "mapName", queue.MapName, summary.MapName);
            Expect(label, "win", expected.Win, summary.Win);
            Expect(label, "remake", expected.Remake, summary.Remake);
            Expect(label, "time", LocalOf(expected.UtcTime), summary.TimeText);
            Expect(label, "duration", expected.Duration, summary.DurationText);
            Expect(label, "kda", expected.Kda, summary.KdaText);
            Expect(label, "cs", expected.CreepScore, summary.CreepScore);
            Expect(label, "gold", expected.Gold, summary.Gold);
            Expect(label, "damage", expected.Damage, summary.DamageToChampions);
            Expect(label, "heal", expected.Heal, summary.TotalHeal);
            Expect(label, "championLevel", expected.ChampionLevel, summary.ChampionLevel);
            Expect(label, "championId", expected.ChampionId, summary.ChampionId);
            Expect(label, "roleBoundItem", expected.RoleBoundItem, summary.RoleBoundItem);
            Expect(label, "position", expected.Position, summary.Position);
            Expect(label, "itemIds", expected.ItemIds, summary.ItemIds.ToArray());
        }
    }

    /// <summary>真实载荷只返回本人的参与者，这是列表解析依赖 <c>participants[0]</c> 的前提。</summary>
    [Fact]
    public void RealFixtures_ContainOnlyQueriedParticipant()
    {
        foreach (MatchFixture fixture in MatchFixtures.All.Where(f => !f.IsSynthetic))
        {
            using JsonDocument document = JsonDocument.Parse(fixture.ReadText());
            JsonElement game = FirstGame(document.RootElement);

            Expect(fixture.Label, "participants 数量", 1, game.GetProperty("participants").GetArrayLength());
        }
    }

    [Fact]
    public void SyntheticRanked420_ParsesFullLcuPayload()
    {
        MatchFixture fixture = MatchFixtures.ByName("ranked-420");
        MatchSummary summary = ParseSingle(fixture);
        string label = fixture.Label;

        Expect(label, "queueName", "排位单双排", summary.QueueName);
        Expect(label, "mapName", "召唤师峡谷", summary.MapName);
        Expect(label, "time", LocalOf("2024-01-15 14:30"), summary.TimeText);
        Expect(label, "duration", "25:30", summary.DurationText);
        Expect(label, "win", true, summary.Win);
        Expect(label, "remake", false, summary.Remake);
        Expect(label, "kda", "5.33", summary.KdaText);
        Expect(label, "cs", 206, summary.CreepScore);
        Expect(label, "roleBoundItem", 1204, summary.RoleBoundItem);
        Expect(label, "position", MatchPosition.Jungle, summary.Position);
        Expect(label, "itemIds", new[] { 6692, 3006, 6675, 6676, 1038, 3364, 3340 }, summary.ItemIds.ToArray());
    }

    [Fact]
    public void SyntheticAram450_HasNoPositionAndCollectsAugments()
    {
        MatchFixture fixture = MatchFixtures.ByName("aram-450");
        MatchSummary summary = ParseSingle(fixture);
        string label = fixture.Label;

        Expect(label, "queueName", "极地大乱斗", summary.QueueName);
        Expect(label, "mapName", "嚎哭深渊", summary.MapName);
        Expect(label, "duration", "18:08", summary.DurationText);
        Expect(label, "win", false, summary.Win);
        Expect(label, "kda", "Perfect", summary.KdaText);
        Expect(label, "cs", 63, summary.CreepScore);
        Expect(label, "position", null, summary.Position);

        // augments 数组与 playerAugment1..5 合并去重后的顺序
        Expect(label, "augmentIds", new[] { 7010, 7018, 7027, 7022 }, summary.AugmentIds.ToArray());
    }

    [Fact]
    public void SyntheticArena1700_ExposesSubteamPlacement()
    {
        MatchFixture fixture = MatchFixtures.ByName("arena-1700");
        MatchSummary summary = ParseSingle(fixture);

        Expect(fixture.Label, "queueName", "斗魂竞技场", summary.QueueName);
        Expect(fixture.Label, "placement", 2, summary.Placement);
        Expect(fixture.Label, "position", null, summary.Position);
    }

    /// <summary>稀疏载荷：只有必填字段，其余全部缺失，必须降级为 0 / null 而不是抛异常。</summary>
    [Fact]
    public void SyntheticMinimal420_DegradesGracefully()
    {
        MatchFixture fixture = MatchFixtures.ByName("minimal-420");
        MatchSummary summary = ParseSingle(fixture);
        string label = fixture.Label;

        Expect(label, "cs", 0, summary.CreepScore);
        Expect(label, "gold", 0, summary.Gold);
        Expect(label, "damage", 0, summary.DamageToChampions);
        Expect(label, "heal", 0, summary.TotalHeal);
        Expect(label, "kda", "2.00", summary.KdaText);
        Expect(label, "remake", false, summary.Remake);
        Expect(label, "placement", null, summary.Placement);
        Expect(label, "position", null, summary.Position);
        Expect(label, "augmentIds", Array.Empty<int>(), summary.AugmentIds.ToArray());

        // mapId 缺失时地图名回落队列惯常地图（420 → 召唤师峡谷）
        Expect(label, "mapName", "召唤师峡谷", summary.MapName);
    }

    /// <summary>重开局的 <c>subteamPlacement</c> 载荷值是 0，必须视为"无名次"而不是"第 0 名"。</summary>
    [Fact]
    public void Remake_ZeroPlacementIsTreatedAsAbsent()
    {
        MatchFixture fixture = MatchFixtures.ByName("remake");
        MatchSummary summary = ParseSingle(fixture);

        Expect(fixture.Label, "placement", null, summary.Placement);
        Expect(fixture.Label, "remake", true, summary.Remake);
        Expect(fixture.Label, "kda", "Perfect", summary.KdaText);
    }

    /// <summary>空参与者不得引发越界，返回 null 由调用方过滤。</summary>
    [Fact]
    public void ParseSummary_ReturnsNullWithoutParticipants()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"gameId":1,"queueId":430,"participants":[]}""");

        Assert.Null(MatchParser.ParseSummary(document.RootElement));
    }

    /// <summary>整个响应缺 <c>games.games</c> 时返回空列表，而不是抛异常。</summary>
    [Fact]
    public void ParseHistory_ToleratesMissingContainer()
    {
        using JsonDocument document = JsonDocument.Parse("""{"games":{}}""");
        Assert.Empty(MatchParser.ParseHistory(document.RootElement));

        using JsonDocument empty = JsonDocument.Parse("{}");
        Assert.Empty(MatchParser.ParseHistory(empty.RootElement));
        Assert.Null(MatchParser.ParseGameCount(empty.RootElement));
    }

    /// <summary>分页元数据来自 <c>games.gameCount</c>，与服务层的 <c>HasMore</c> 判定配套。</summary>
    [Fact]
    public void ParseGameCount_ReadsGameCountFromPayload()
    {
        MatchFixture fixture = MatchFixtures.ByName("top-solo");
        using JsonDocument document = JsonDocument.Parse(fixture.ReadText());

        Assert.Equal(1, MatchParser.ParseGameCount(document.RootElement));
    }

    private static MatchSummary ParseSingle(MatchFixture fixture)
    {
        using JsonDocument document = JsonDocument.Parse(fixture.ReadText());
        return Assert.Single(MatchParser.ParseHistory(document.RootElement));
    }

    private static JsonElement FirstGame(JsonElement response) =>
        response.GetProperty("games").GetProperty("games")[0];

    /// <summary>UTC 字面量 → 本地时区同格式文本（本项目的显示口径）。</summary>
    private static string LocalOf(string utcText) => ToUtc(utcText)
        .ToLocalTime()
        .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static DateTime ToUtc(string utcText) => DateTime.SpecifyKind(
        DateTime.ParseExact(utcText, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateTimeKind.Utc);

    /// <summary>
    /// 带夹具名的断言。xUnit 的 <c>Assert.Equal</c> 不能附加自定义消息，
    /// 而循环里 8×18 个字段一旦红，必须能直接点名"哪个夹具的哪个字段"，否则只能靠二分定位。
    /// </summary>
    private static void Expect(string label, string field, object? expected, object? actual) =>
        Assert.True(Same(expected, actual), $"{label}: {field} 期望 {Format(expected)}，实际 {Format(actual)}");

    /// <summary>比较时把数组按元素序列比较；其余按值比较（字符串不走序列分支）。</summary>
    private static bool Same(object? expected, object? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        if (expected is string || actual is string)
        {
            return Equals(expected, actual);
        }

        if (expected is IEnumerable expectedSequence && actual is IEnumerable actualSequence)
        {
            return expectedSequence.Cast<object>().SequenceEqual(actualSequence.Cast<object>());
        }

        return Equals(expected, actual);
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        IEnumerable sequence => "[" + string.Join(", ", sequence.Cast<object>()) + "]",
        _ => value.ToString() ?? "null",
    };
}
