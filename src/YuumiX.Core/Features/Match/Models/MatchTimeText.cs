using System.Globalization;

namespace YuumiX.Core.Features.Match.Models;

/// <summary>
/// 战绩时间的唯一格式化入口（列表项与详情共用）。
///
/// <b>刻意使用 UTC</b>：原实现 <c>timestamp_to_str</c> / <c>timestamp_to_short_str</c> 基于
/// <c>chrono::DateTime::from_timestamp</c>，得到的就是 UTC 而非本地时间，其单测
/// <c>timestamp_helpers_format_utc</c> 明确断言了这一点（1705329000000 → "2024-01-15 14:30"）。
/// 迁移阶段照搬，以免逐字段回归出现整 8 小时的假差异；"是否改为本地时间"是一次独立的行为变更，
/// 需单独决策，不能夹带在解析器迁移里。
/// </summary>
internal static class MatchTimeText
{
    /// <summary>
    /// 毫秒时间戳 → 指定格式文本；超出可表示范围时返回 <paramref name="fallback"/>。
    /// </summary>
    /// <remarks>
    /// 原实现对无效时间戳（如 <c>u64::MAX</c>）回落到固定文案而非抛异常。
    /// 保留该行为：LCU 偶发下发异常值时，整页战绩不应因一条记录的时间字段而渲染失败。
    /// </remarks>
    public static string FromUnixMilliseconds(long unixMilliseconds, string format, string fallback)
    {
        if (unixMilliseconds < 0 || unixMilliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            return fallback;
        }

        return DateTimeOffset
            .FromUnixTimeMilliseconds(unixMilliseconds)
            .ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>秒数 → <c>mm:ss</c>。超过一小时不进位到小时（3725 秒写作 <c>62:05</c>），与原实现一致。</summary>
    public static string FromSeconds(int totalSeconds) => string.Format(
        CultureInfo.InvariantCulture,
        "{0:00}:{1:00}",
        totalSeconds / 60,
        totalSeconds % 60);
}
