using System.Globalization;

namespace YuumiX.Core.Features.Match.Models;

/// <summary>
/// 战绩时间的唯一格式化入口（列表项与详情共用）。
///
/// <b>按本地时区显示。</b>原实现用 <c>chrono::DateTime::from_timestamp</c> 直接格式化，
/// 得到的是 UTC，其单测 <c>timestamp_helpers_format_utc</c> 还把这个偏差固化成断言
/// （1705329000000 → "2024-01-15 14:30"）。后果是 GMT+8 用户 22:30 打的对局在界面上显示成 14:30，
/// 相差整整 8 小时。
///
/// 迁移期曾照搬 UTC 一版（为了与旧版逐字对齐），现已改为本地时区：用户面"几点打的"必须与自己的时区一致，
/// 这属于原实现的事实性缺陷，不是需要保留的兼容行为。
/// <b>代价</b>：与旧版做视觉比对时，时间字段必然相差一个时区偏移，不能当作回归失败。
///
/// 为了让这条规则可被确定性断言，本类型提供显式传 <see cref="TimeZoneInfo"/> 的重载；
/// 生产路径固定走 <see cref="TimeZoneInfo.Local"/>，测试据此固定到 UTC / UTC+8 / UTC-5 三个时区。
/// </summary>
internal static class MatchTimeText
{
    /// <summary>
    /// 毫秒时间戳 → 指定格式文本（本地时区）；超出可表示范围时返回 <paramref name="fallback"/>。
    /// </summary>
    public static string FromUnixMilliseconds(long unixMilliseconds, string format, string fallback) =>
        FromUnixMilliseconds(unixMilliseconds, format, fallback, TimeZoneInfo.Local);

    /// <summary>
    /// 毫秒时间戳 → 指定格式文本（指定时区）。
    /// </summary>
    /// <remarks>
    /// 原实现对无效时间戳（如 <c>u64::MAX</c>）回落到固定文案而非抛异常。
    /// 保留该行为：LCU 偶发下发异常值时，整页战绩不应因一条记录的时间字段而渲染失败。
    /// </remarks>
    public static string FromUnixMilliseconds(
        long unixMilliseconds,
        string format,
        string fallback,
        TimeZoneInfo timeZone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentNullException.ThrowIfNull(timeZone);

        if (unixMilliseconds < 0 || unixMilliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            return fallback;
        }

        return TimeZoneInfo
            .ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds), timeZone)
            .ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>秒数 → <c>mm:ss</c>。超过一小时不进位到小时（3725 秒写作 <c>62:05</c>），与原实现一致。</summary>
    public static string FromSeconds(int totalSeconds) => string.Format(
        CultureInfo.InvariantCulture,
        "{0:00}:{1:00}",
        totalSeconds / 60,
        totalSeconds % 60);
}
