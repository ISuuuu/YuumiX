using Xunit;
using YuumiX.Core.Features.Match.Models;

namespace YuumiX.Core.Tests;

/// <summary>
/// 战绩契约模型的回归：格式化规则与分路映射。
///
/// 这一组用例的期望值全部照抄原实现 <c>match_parser.rs</c> 的单测
/// （<c>secs_to_str_formats_minutes_and_seconds</c>、<c>timestamp_helpers_format_utc</c>），
/// 不是重新设计的规则。契约一旦与这些数字脱钩，解析器分支和服务分支就会各自"看起来对"，
/// 只有在界面拼到一起时才暴露为整页错位。
/// </summary>
public sealed class MatchContractTests
{
    private static MatchSummary Summary(
        long gameCreation = 1_705_329_000_000,
        int duration = 1530,
        int kills = 10,
        int deaths = 4,
        int assists = 4) => new()
        {
            GameId = 1,
            QueueId = 430,
            QueueName = "匹配模式",
            MapName = "召唤师峡谷",
            GameCreation = gameCreation,
            DurationSeconds = duration,
            Win = true,
            Remake = false,
            ChampionId = 103,
            Spell1Id = 4,
            Spell2Id = 14,
            ChampionLevel = 18,
            Kills = kills,
            Deaths = deaths,
            Assists = assists,
            ItemIds = [],
            RuneId = 8214,
            CreepScore = 0,
            Gold = 0,
            DamageToChampions = 0,
            TotalHeal = 0,
            AugmentIds = [],
        };

    [Fact]
    public void KdaText_ZeroDeaths_IsPerfectInsteadOfInfinity()
    {
        // 原实现：deaths == 0 → "Perfect"；直接做除法会得到 inf 并渲染成 "∞"。
        Assert.Equal("Perfect", Summary(kills: 3, deaths: 0, assists: 9).KdaText);
    }

    [Fact]
    public void KdaText_TwoDecimalPlaces_InvariantCulture()
    {
        // (10 + 4) / 4 = 3.5 → "3.50"。小数点必须是 '.'，不受系统区域设置影响。
        Assert.Equal("3.50", Summary(kills: 10, deaths: 4, assists: 4).KdaText);
        Assert.Equal("0.50", Summary(kills: 0, deaths: 4, assists: 2).KdaText);
    }

    [Theory]
    [InlineData(1530, "25:30")]
    [InlineData(3725, "62:05")] // 不进位到小时，这是原实现的既有行为
    [InlineData(59, "00:59")]
    [InlineData(73, "01:13")] // 三分钟重开的真实时长形态
    [InlineData(0, "00:00")]
    public void DurationText_MatchesOriginalFormat(int seconds, string expected)
    {
        Assert.Equal(expected, Summary(duration: seconds).DurationText);
    }

    [Fact]
    public void TimeText_UsesUtc_AsOriginalImplementation()
    {
        // 原实现 timestamp_helpers_format_utc 的断言值，本契约照搬。
        var summary = Summary(gameCreation: 1_705_329_000_000);

        Assert.Equal("2024-01-15 14:30", summary.TimeText);
        Assert.Equal("01-15 14:30", summary.ShortTimeText);
    }

    [Fact]
    public void TimeText_FallsBackWhenTimestampOutOfRange()
    {
        // LCU 偶发异常值时整页不应崩：原实现回落到固定文案。
        var summary = Summary(gameCreation: long.MaxValue);

        Assert.Equal("1970-01-01 00:00", summary.TimeText);
        Assert.Equal("01-01 00:00", summary.ShortTimeText);
    }

    [Theory]
    [InlineData(MatchPosition.Top, "TOP")]
    [InlineData(MatchPosition.Jungle, "JUNGLE")]
    [InlineData(MatchPosition.Middle, "MIDDLE")]
    [InlineData(MatchPosition.Bottom, "BOTTOM")]
    [InlineData(MatchPosition.Utility, "UTILITY")]
    public void PositionText_RoundTrips(MatchPosition position, string expected)
    {
        Assert.Equal(expected, position.ToLcuName());
        Assert.Equal(position, MatchPositionText.TryParseLcuName(expected));
    }

    [Theory]
    [InlineData("MID", MatchPosition.Middle)]
    [InlineData(" mid ", MatchPosition.Middle)]
    [InlineData("BOT", MatchPosition.Bottom)]
    [InlineData("SUPPORT", MatchPosition.Utility)]
    public void PositionText_AcceptsObservedAliases(string raw, MatchPosition expected)
    {
        Assert.Equal(expected, MatchPositionText.TryParseLcuName(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NONE")] // 自定义对局与娱乐模式的真实取值
    [InlineData("CARRY")] // timeline.role 的取值之一，不能直接当分路
    public void PositionText_ReturnsNullWhenUndeterminable(string? raw)
    {
        Assert.Null(MatchPositionText.TryParseLcuName(raw));
    }

    [Fact]
    public void HistoryPage_EmptyIsUsableWithoutNullChecks()
    {
        Assert.Empty(MatchHistoryPage.Empty.Games);
        Assert.False(MatchHistoryPage.Empty.HasMore);
        Assert.Null(MatchHistoryPage.Empty.TotalCount);
    }
}
