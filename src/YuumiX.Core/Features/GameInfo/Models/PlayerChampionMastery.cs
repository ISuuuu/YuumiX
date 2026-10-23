using System.Globalization;

namespace YuumiX.Core.Features.GameInfo.Models;

/// <summary>
/// 玩家单英雄熟练度，对应 LCU <c>/lol-champion-mastery/v1/{puuid}/champion-mastery</c>。
/// </summary>
public sealed record PlayerChampionMastery(
    int ChampionId,
    int ChampionLevel,
    long ChampionPoints,
    string? HighestGrade = null,
    long ChampionPointsSinceLastLevel = 0,
    long ChampionPointsUntilNextLevel = 0,
    int TokensEarned = 0);

/// <summary>
/// 当前选用英雄的熟练度分析详情。
/// </summary>
public sealed record ChampionMasteryDetail(
    int Level,
    long Points,
    string FormattedPoints,
    string? HighestGrade,
    bool IsTopChampion,
    bool IsPracticing,
    IReadOnlyList<PlayerChampionMastery> TopChampions)
{
    public static string FormatPoints(long points)
    {
        if (points >= 1_000_000)
        {
            return (points / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
        }

        if (points >= 10_000)
        {
            return (points / 10_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "w";
        }

        if (points >= 1_000)
        {
            return (points / 1_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        }

        return points.ToString(CultureInfo.InvariantCulture);
    }

    public static ChampionMasteryDetail? Create(
        IReadOnlyList<PlayerChampionMastery>? masteries,
        int targetChampId)
    {
        if (masteries is null || masteries.Count == 0 || targetChampId <= 0)
        {
            return null;
        }

        IReadOnlyList<PlayerChampionMastery> top3 = masteries.Take(3).ToList();
        PlayerChampionMastery? current = masteries.FirstOrDefault(m => m.ChampionId == targetChampId);

        int level = current?.ChampionLevel ?? 0;
        long points = current?.ChampionPoints ?? 0;

        bool isTopChampion = (top3.Any(t => t.ChampionId == targetChampId) && points >= 10_000) ||
                             points >= 100_000;
        bool isPracticing = !isTopChampion && (level <= 1 || points < 5000);

        return new ChampionMasteryDetail(
            level,
            points,
            FormatPoints(points),
            current?.HighestGrade,
            isTopChampion,
            isPracticing,
            top3);
    }
}
