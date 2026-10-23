using Xunit;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.Match.Models;

namespace YuumiX.Core.Tests;

/// <summary>
/// 近期表现聚合的规则用例。
///
/// 这套口径会被生涯页的看板直接显示，因此三条边界必须锁死：
/// 重开局不计入、零死亡写 <c>Perfect</c>、分路占比的分母只算可判定分路的对局。
/// </summary>
public sealed class RecentFormSummaryTests
{
    [Fact]
    public void Compute_AggregatesWinLossAndKda()
    {
        MatchSummary[] matches =
        [
            Match(1, win: true, kills: 10, deaths: 2, assists: 5, position: MatchPosition.Top),
            Match(2, win: false, kills: 3, deaths: 5, assists: 7, position: MatchPosition.Top),
            Match(3, win: true, kills: 6, deaths: 0, assists: 9, position: MatchPosition.Middle),
        ];

        RecentFormSummary summary = RecentFormSummary.Compute(matches);

        Assert.Equal(3, summary.Games);
        Assert.Equal(2, summary.Wins);
        Assert.Equal(1, summary.Losses);
        Assert.Equal(19, summary.Kills);
        Assert.Equal(7, summary.Deaths);
        Assert.Equal(21, summary.Assists);
        Assert.Equal("5.7", summary.KdaText);
    }

    /// <summary>重开局（3 分钟投降）不计入胜负，也不计入英雄与分路统计。</summary>
    [Fact]
    public void Compute_ExcludesRemakes()
    {
        MatchSummary[] matches =
        [
            Match(1, win: true, kills: 8, deaths: 1, assists: 4, position: MatchPosition.Bottom),
            Match(2, win: true, remake: true, kills: 0, deaths: 0, assists: 0, position: MatchPosition.Bottom),
        ];

        RecentFormSummary summary = RecentFormSummary.Compute(matches);

        Assert.Equal(1, summary.Games);
        Assert.Equal(1, summary.Wins);
        Assert.Equal(0, summary.Losses);
        Assert.Equal(8, summary.Kills);
        Assert.Single(summary.TopChampions);
        Assert.Equal(1, Assert.Single(summary.Positions).Games);
    }

    [Fact]
    public void Compute_ReportsPerfectWhenNoDeaths()
    {
        RecentFormSummary summary = RecentFormSummary.Compute(
            [Match(1, win: true, kills: 4, deaths: 0, assists: 11, position: MatchPosition.Utility)]);

        Assert.Equal("Perfect", summary.KdaText);
    }

    /// <summary>英雄按出场次数降序；次数相同时按 ID 升序，保证界面图标位置稳定。</summary>
    [Fact]
    public void Compute_OrdersTopChampionsByGamesThenId()
    {
        MatchSummary[] matches =
        [
            Match(1, championId: 30, win: true),
            Match(2, championId: 30, win: false),
            Match(3, championId: 10, win: true),
            Match(4, championId: 20, win: true),
            Match(5, championId: 20, win: true),
        ];

        RecentFormSummary summary = RecentFormSummary.Compute(matches);

        // 英雄 20 与 30 都是 2 场，按 ID 升序 → 20 在前
        Assert.Equal([20, 30, 10], summary.TopChampions.Select(champion => champion.ChampionId));
        Assert.Equal(2, summary.TopChampions[0].Games);
        Assert.Equal("100%", summary.TopChampions[0].WinRateText);
        Assert.Equal("50%", summary.TopChampions[1].WinRateText);
    }

    [Fact]
    public void Compute_LimitsTopChampions()
    {
        MatchSummary[] matches =
        [
            .. Enumerable.Range(1, 10).Select(index => Match(index, championId: 100 + index)),
        ];

        RecentFormSummary summary = RecentFormSummary.Compute(matches, topChampionCount: 3);

        Assert.Equal(3, summary.TopChampions.Count);
    }

    /// <summary>分路占比的分母只算可判定分路的对局（大乱斗没有分路，不该稀释百分比）。</summary>
    [Fact]
    public void Compute_PositionPercentageUsesOnlyClassifiedGames()
    {
        MatchSummary[] matches =
        [
            Match(1, position: MatchPosition.Top),
            Match(2, position: MatchPosition.Top),
            Match(3, position: MatchPosition.Top),
            Match(4, position: MatchPosition.Jungle),
            Match(5, position: null),
        ];

        RecentFormSummary summary = RecentFormSummary.Compute(matches);

        Assert.Equal(4, summary.PositionGames);
        Assert.Equal(MatchPosition.Top, summary.Positions[0].Position);
        Assert.Equal(75, summary.Positions[0].Percentage);
        Assert.Equal(25, summary.Positions[1].Percentage);
    }

    [Fact]
    public void Compute_ReturnsEmptyForNoMatchesOrOnlyRemakes()
    {
        Assert.True(RecentFormSummary.Compute([]).IsEmpty);
        Assert.True(RecentFormSummary.Compute([Match(1, remake: true)]).IsEmpty);
    }

    private static MatchSummary Match(
        long gameId,
        bool win = true,
        bool remake = false,
        int championId = 1,
        int kills = 0,
        int deaths = 0,
        int assists = 0,
        MatchPosition? position = null) => new()
        {
            GameId = gameId,
            QueueId = 420,
            QueueName = "单双排位",
            MapName = "召唤师峡谷",
            GameCreation = 1_700_000_000_000,
            DurationSeconds = 1_800,
            Win = win,
            Remake = remake,
            ChampionId = championId,
            Spell1Id = 4,
            Spell2Id = 14,
            ChampionLevel = 18,
            Kills = kills,
            Deaths = deaths,
            Assists = assists,
            ItemIds = [],
            RuneId = 8000,
            CreepScore = 0,
            Gold = 0,
            DamageToChampions = 0,
            TotalHeal = 0,
            AugmentIds = [],
            Position = position,
        };
}
