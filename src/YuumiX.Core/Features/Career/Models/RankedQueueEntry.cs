using System.Globalization;

namespace YuumiX.Core.Features.Career.Models;

/// <summary>排位队列标识。</summary>
public static class RankedQueueTypes
{
    public const string SoloDuo = "RANKED_SOLO_5x5";

    public const string Flex = "RANKED_FLEX_SR";
}

/// <summary>
/// 单个排位队列的成绩，对应 <c>/lol-ranked/v1/ranked-stats/{puuid}</c> 的
/// <c>queues[]</c> 条目（只保留界面实际消费的字段）。
///
/// 两条与原实现的差异：
/// <list type="bullet">
///   <item>分部字段命名为 <see cref="Division"/> 而不是沿抄 LCU 的 <c>rank</c>：
///         <c>rank</c> 在战绩语境里已有"排名/名次"含义，同名会让阅读者反复确认上下文。</item>
///   <item>赛季最高 / 上赛季结算做成显式属性，而不是让界面自己去摸 <c>highestTier</c>。
///         原实现把顶层字段与队列条目交叉声明成同一个 TS 类型，实际只赋了 <c>queues</c>，
///         于是"赛季最高"一列恒为 <c>--</c>（字段永远 <c>undefined</c>）。这里在解析阶段
///         就把顶层字段回填进队列条目，界面拿到的值一定可用或确实为空。</item>
/// </list>
/// </summary>
public sealed record RankedQueueEntry
{
    /// <summary>队列标识，如 <c>RANKED_SOLO_5x5</c>。</summary>
    public required string QueueType { get; init; }

    /// <summary>段位标识（<c>IRON</c>…<c>CHALLENGER</c>）；未定级为 <c>NONE</c>。</summary>
    public required string Tier { get; init; }

    /// <summary>分部（<c>I</c>–<c>IV</c>）；大师及以上或未定级为空串 / <c>NA</c>。</summary>
    public required string Division { get; init; }

    /// <summary>胜点。</summary>
    public required int LeaguePoints { get; init; }

    /// <summary>本赛季胜场。</summary>
    public required int Wins { get; init; }

    /// <summary>本赛季负场。</summary>
    public required int Losses { get; init; }

    /// <summary>赛季最高段位标识；LCU 未下发时为 <c>null</c>。</summary>
    public string? HighestTier { get; init; }

    /// <summary>赛季最高分部。</summary>
    public string? HighestDivision { get; init; }

    /// <summary>上赛季结算段位标识。</summary>
    public string? PreviousSeasonTier { get; init; }

    /// <summary>上赛季结算分部。</summary>
    public string? PreviousSeasonDivision { get; init; }

    /// <summary>是否已有真实段位（未定级时为 <c>false</c>）。</summary>
    public bool HasRank => RankTierText.HasRank(Tier);

    /// <summary>本赛季总场次。</summary>
    public int TotalGames => Wins + Losses;

    /// <summary>
    /// 胜率文本（整数百分比）。总场次为 0 时给 <c>--</c>：
    /// 原实现在此处直接除零后输出 <c>NaN%</c>，是靠外层三元表达式才没露出来。
    /// </summary>
    public string WinRateText => TotalGames == 0
        ? "--"
        : ((Wins * 100.0) / TotalGames).ToString("F0", CultureInfo.InvariantCulture) + "%";

    /// <summary>段位文本（如「荣耀黄金 II」，未定级为 <c>--</c>）。</summary>
    public string TierText => RankTierText.Display(Tier, Division);

    /// <summary>赛季最高段位文本。</summary>
    public string HighestTierText => RankTierText.Display(HighestTier, HighestDivision);

    /// <summary>上赛季结算段位文本。</summary>
    public string PreviousSeasonTierText => RankTierText.Display(PreviousSeasonTier, PreviousSeasonDivision);
}
