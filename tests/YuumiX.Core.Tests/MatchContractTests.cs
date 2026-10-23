using System.Globalization;
using Xunit;
using YuumiX.Core.Features.Match.Models;

namespace YuumiX.Core.Tests;

/// <summary>
/// 战绩契约模型的回归：格式化规则与分路映射。
///
/// 期望值来自原实现 <c>match_parser.rs</c> 的单测
/// （<c>secs_to_str_formats_minutes_and_seconds</c>、<c>timestamp_helpers_format_utc</c>），
/// 唯一例外是时间字段——原实现固定输出 UTC，本契约改为本地时区（见
/// <see cref="MatchTimeText"/> 的说明），因此这里用固定时区断言换算结果，不沿用 UTC 期望值。
/// </summary>
public sealed class MatchContractTests
{
    /// <summary>2024-01-15 14:30 UTC，与原实现单测同一个时间戳。</summary>
    private const long SampleTimestamp = 1_705_329_000_000;

    private static readonly TimeZoneInfo PlusEight =
        TimeZoneInfo.CreateCustomTimeZone("Test+08", TimeSpan.FromHours(8), "UTC+08", "UTC+08");

    private static readonly TimeZoneInfo MinusFive =
        TimeZoneInfo.CreateCustomTimeZone("Test-05", TimeSpan.FromHours(-5), "UTC-05", "UTC-05");

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
    public void TimeText_FollowsTimeZoneInsteadOfAlwaysUtc()
    {
        // 1_705_329_000_000 = 2024-01-15 14:30 UTC。
        // 旧版原样显示 UTC（GMT+8 用户看到的"22:30 的对局"会写成 14:30），本版按传入时区换算。
        Assert.Equal("2024-01-15 14:30", Format(TimeZoneInfo.Utc));
        Assert.Equal("2024-01-15 22:30", Format(PlusEight));
        Assert.Equal("2024-01-15 09:30", Format(MinusFive));

        static string Format(TimeZoneInfo zone) =>
            MatchTimeText.FromUnixMilliseconds(SampleTimestamp, "yyyy-MM-dd HH:mm", "FALLBACK", zone);
    }

    [Fact]
    public void ShortTimeText_ShiftsDateAcrossTimeZoneBoundary()
    {
        // 1_705_348_800_000 = 2024-01-15 20:00 UTC，在 UTC+8 已是次日 04:00：
        // 短格式里的日期必须跟着时区走，否则列表会出现"昨天的局排到今天"。
        Assert.Equal(
            "01-16 04:00",
            MatchTimeText.FromUnixMilliseconds(1_705_348_800_000, "MM-dd HH:mm", "FALLBACK", PlusEight));
    }

    [Fact]
    public void SummaryTimeText_UsesMachineLocalZone()
    {
        var summary = Summary(gameCreation: SampleTimestamp);
        var local = DateTimeOffset.FromUnixTimeMilliseconds(SampleTimestamp).ToLocalTime();

        Assert.Equal(local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), summary.TimeText);
        Assert.Equal(local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), summary.ShortTimeText);
    }

    [Fact]
    public void TimeText_FallsBackWhenTimestampOutOfRange()
    {
        // LCU 偶发异常值时整页不应崩：原实现回落到固定文案，与时区无关。
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
