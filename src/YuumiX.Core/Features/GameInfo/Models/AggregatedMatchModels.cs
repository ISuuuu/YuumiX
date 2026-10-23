using YuumiX.Core.Features.Match.Models;

namespace YuumiX.Core.Features.GameInfo.Models;

/// <summary>
/// 单个分路统计条目。
/// </summary>
public sealed record PositionCountEntry(
    MatchPosition Position,
    string Name,
    int Count,
    int Percentage,
    string Color);

/// <summary>
/// 近期对局分路统计汇总。
/// </summary>
public sealed record AggregatedPositions(
    IReadOnlyDictionary<MatchPosition, int> Counts,
    IReadOnlyDictionary<MatchPosition, int> Percentages,
    int TotalValid,
    MatchPosition? PrimaryPosition,
    int PrimaryPercentage,
    MatchPosition? SecondaryPosition,
    int SecondaryPercentage,
    IReadOnlyList<PositionCountEntry> Entries);

/// <summary>
/// 近期常用英雄统计。
/// </summary>
public sealed record AggregatedChampion(
    int ChampionId,
    string? ChampionName,
    int Games,
    int Wins,
    int Losses,
    int WinRate,
    int Kills,
    int Deaths,
    int Assists,
    string Kda,
    MatchPosition? PrimaryPosition,
    IReadOnlyDictionary<MatchPosition, int> Positions);
