using System.Globalization;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 战绩与近期表现统计计算器（纯函数）。
/// 对应旧版 <c>matchStats.ts</c> 与 <c>matchAnalysis.ts</c>。
/// </summary>
public static class MatchStatsCalculator
{
    private static readonly MatchPosition[] PositionKeys =
    [
        MatchPosition.Top,
        MatchPosition.Jungle,
        MatchPosition.Middle,
        MatchPosition.Bottom,
        MatchPosition.Utility,
    ];

    private static readonly Dictionary<MatchPosition, string> PositionNames = new()
    {
        [MatchPosition.Top] = "上路",
        [MatchPosition.Jungle] = "打野",
        [MatchPosition.Middle] = "中路",
        [MatchPosition.Bottom] = "下路",
        [MatchPosition.Utility] = "辅助",
    };

    private static readonly Dictionary<MatchPosition, string> PositionColors = new()
    {
        [MatchPosition.Top] = "#e06b6b",
        [MatchPosition.Jungle] = "#55a879",
        [MatchPosition.Middle] = "#d7a84d",
        [MatchPosition.Bottom] = "#5d8ed6",
        [MatchPosition.Utility] = "#9b79c6",
    };

    /// <summary>
    /// 由对局列表计算胜率/KDA/连胜/位置与常用英雄聚合。
    /// </summary>
    public static (int WinCount, int LossesCount, double WinRate, double AvgKda, StreakInfo? Streak, AggregatedPositions? Positions, IReadOnlyList<AggregatedChampion> TopChampions)
        ComputeMatchStats(IReadOnlyList<MatchSummary>? matches, int topChampionLimit = 5)
    {
        if (matches is null || matches.Count == 0)
        {
            return (0, 0, 0, 0, null, null, []);
        }

        int totalKills = 0;
        int totalDeaths = 0;
        int totalAssists = 0;
        int remakeCount = 0;
        int currentWinCount = 0;
        int currentLossesCount = 0;

        foreach (var m in matches)
        {
            if (m.Remake)
            {
                remakeCount++;
            }
            else
            {
                totalKills += m.Kills;
                totalDeaths += m.Deaths;
                totalAssists += m.Assists;
                if (m.Win)
                {
                    currentWinCount++;
                }
                else
                {
                    currentLossesCount++;
                }
            }
        }

        int validMatches = matches.Count - remakeCount;
        double winRate = validMatches > 0
            ? Math.Round((double)currentWinCount / validMatches * 100)
            : 0;

        double deathsForCalc = totalDeaths == 0 ? 1.0 : totalDeaths;
        double avgKda = (totalKills + totalAssists) / deathsForCalc;

        StreakInfo? streak = null;
        var validStreakMatches = matches.Where(m => !m.Remake).ToList();
        if (validStreakMatches.Count > 0)
        {
            bool firstWin = validStreakMatches[0].Win;
            int count = 0;
            foreach (var m in validStreakMatches)
            {
                if (m.Win == firstWin)
                {
                    count++;
                }
                else
                {
                    break;
                }
            }

            if (count >= 2)
            {
                streak = new StreakInfo(firstWin ? "win" : "loss", count);
            }
        }

        AggregatedPositions? positions = ComputeAggregatedPositions(matches);
        IReadOnlyList<AggregatedChampion> topChampions = ComputeAggregatedChampions(matches, topChampionLimit);

        return (currentWinCount, currentLossesCount, winRate, avgKda, streak, positions, topChampions);
    }

    /// <summary>
    /// 统计近期对局中的位置分布占比。
    /// </summary>
    public static AggregatedPositions? ComputeAggregatedPositions(IReadOnlyList<MatchSummary>? matches)
    {
        if (matches is null || matches.Count == 0)
        {
            return null;
        }

        var counts = new Dictionary<MatchPosition, int>
        {
            [MatchPosition.Top] = 0,
            [MatchPosition.Jungle] = 0,
            [MatchPosition.Middle] = 0,
            [MatchPosition.Bottom] = 0,
            [MatchPosition.Utility] = 0,
        };

        int totalValid = 0;

        foreach (var m in matches)
        {
            if (m.Remake)
            {
                continue;
            }

            MatchPosition? pos = InferMatchPosition(m);
            if (pos.HasValue && counts.ContainsKey(pos.Value))
            {
                counts[pos.Value]++;
                totalValid++;
            }
        }

        if (totalValid == 0)
        {
            return null;
        }

        var percentages = new Dictionary<MatchPosition, int>();
        foreach (var pos in PositionKeys)
        {
            percentages[pos] = (int)Math.Round((double)counts[pos] / totalValid * 100);
        }

        var sortedKeys = PositionKeys.OrderByDescending(k => counts[k]).ToList();

        MatchPosition? primaryPosition = counts[sortedKeys[0]] > 0 ? sortedKeys[0] : null;
        int primaryPercentage = primaryPosition.HasValue ? percentages[primaryPosition.Value] : 0;

        MatchPosition? secondaryPosition = counts[sortedKeys[1]] > 0 ? sortedKeys[1] : null;
        int secondaryPercentage = secondaryPosition.HasValue ? percentages[secondaryPosition.Value] : 0;

        var entries = sortedKeys
            .Where(k => counts[k] > 0)
            .Select(k => new PositionCountEntry(
                k,
                PositionNames[k],
                counts[k],
                percentages[k],
                PositionColors[k]))
            .ToList();

        return new AggregatedPositions(
            counts,
            percentages,
            totalValid,
            primaryPosition,
            primaryPercentage,
            secondaryPosition,
            secondaryPercentage,
            entries);
    }

    /// <summary>
    /// 从 MatchSummary 推导位置（优先已有 Position，缺失时从装备与技能兜底）。
    /// </summary>
    public static MatchPosition? InferMatchPosition(MatchSummary m)
    {
        if (!MatchPositionResolver.IsLanedMode(m.QueueId))
        {
            return null;
        }

        if (m.Position.HasValue)
        {
            return m.Position.Value;
        }

        if (m.RoleBoundItem.HasValue && m.RoleBoundItem.Value > 0)
        {
            MatchPosition? rolePos = MatchPositionResolver.ResolveRoleBoundItem(m.RoleBoundItem.Value);
            if (rolePos.HasValue)
            {
                return rolePos.Value;
            }
        }

        if (m.ItemIds is { Count: > 0 })
        {
            foreach (int id in m.ItemIds)
            {
                MatchPosition? itemPos = MatchPositionResolver.ResolveRoleBoundItem(id);
                if (itemPos.HasValue)
                {
                    return itemPos.Value;
                }
            }
        }

        if (m.Spell1Id == MatchPositionResolver.SmiteSpellId || m.Spell2Id == MatchPositionResolver.SmiteSpellId)
        {
            return MatchPosition.Jungle;
        }

        if (m.ItemIds is { Count: > 0 })
        {
            if (m.ItemIds.Any(MatchPositionResolver.IsSupportItem))
            {
                return MatchPosition.Utility;
            }

            if (m.ItemIds.Any(MatchPositionResolver.IsJungleItem))
            {
                return MatchPosition.Jungle;
            }
        }

        return null;
    }

    /// <summary>
    /// 统计近期对局中的常用英雄。
    /// </summary>
    public static IReadOnlyList<AggregatedChampion> ComputeAggregatedChampions(
        IReadOnlyList<MatchSummary>? matches,
        int limit = 5)
    {
        if (matches is null || matches.Count == 0)
        {
            return [];
        }

        var champMap = new Dictionary<int, ChampionStatsAccumulator>();

        foreach (var m in matches)
        {
            if (m.Remake || m.ChampionId <= 0)
            {
                continue;
            }

            if (!champMap.TryGetValue(m.ChampionId, out var acc))
            {
                acc = new ChampionStatsAccumulator(m.ChampionId);
                champMap[m.ChampionId] = acc;
            }

            acc.Games++;
            if (m.Win)
            {
                acc.Wins++;
            }
            else
            {
                acc.Losses++;
            }

            acc.Kills += m.Kills;
            acc.Deaths += m.Deaths;
            acc.Assists += m.Assists;

            MatchPosition? pos = InferMatchPosition(m);
            if (pos.HasValue)
            {
                acc.Positions[pos.Value]++;
            }
        }

        var list = new List<AggregatedChampion>();
        foreach (var acc in champMap.Values)
        {
            int winRate = acc.Games > 0 ? (int)Math.Round((double)acc.Wins / acc.Games * 100) : 0;
            string kda = acc.Deaths == 0
                ? "Perfect"
                : ((acc.Kills + acc.Assists) / (double)acc.Deaths).ToString("0.0", CultureInfo.InvariantCulture);

            MatchPosition? primaryPos = null;
            int maxCount = 0;
            foreach (var key in PositionKeys)
            {
                if (acc.Positions[key] > maxCount)
                {
                    maxCount = acc.Positions[key];
                    primaryPos = key;
                }
            }

            list.Add(new AggregatedChampion(
                acc.ChampionId,
                null,
                acc.Games,
                acc.Wins,
                acc.Losses,
                winRate,
                acc.Kills,
                acc.Deaths,
                acc.Assists,
                kda,
                primaryPos,
                acc.Positions));
        }

        // 排序：场次多的在前；场次相同则胜率高的在前；胜率相同则 KDA 高的在前
        list.Sort((a, b) =>
        {
            if (b.Games != a.Games)
            {
                return b.Games.CompareTo(a.Games);
            }

            if (b.WinRate != a.WinRate)
            {
                return b.WinRate.CompareTo(a.WinRate);
            }

            double kdaA = a.Kda == "Perfect" ? 999.0 : (double.TryParse(a.Kda, CultureInfo.InvariantCulture, out var vA) ? vA : 0);
            double kdaB = b.Kda == "Perfect" ? 999.0 : (double.TryParse(b.Kda, CultureInfo.InvariantCulture, out var vB) ? vB : 0);
            return kdaB.CompareTo(kdaA);
        });

        return list.Take(limit).ToList();
    }

    private sealed class ChampionStatsAccumulator(int championId)
    {
        public int ChampionId { get; } = championId;
        public int Games { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Assists { get; set; }
        public Dictionary<MatchPosition, int> Positions { get; } = new()
        {
            [MatchPosition.Top] = 0,
            [MatchPosition.Jungle] = 0,
            [MatchPosition.Middle] = 0,
            [MatchPosition.Bottom] = 0,
            [MatchPosition.Utility] = 0,
        };
    }
}
