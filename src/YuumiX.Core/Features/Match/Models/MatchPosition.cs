namespace YuumiX.Core.Features.Match.Models;

/// <summary>
/// 归一化后的 5v5 分路。
///
/// 原实现（<c>match_parser.rs</c> 的 <c>position</c>）用
/// <c>"TOP" | "JUNGLE" | "MIDDLE" | "BOTTOM" | "UTILITY"</c> 五个字符串表示，
/// 这里改成枚举：取值集合是封闭的，字符串会让拼写错误一路编译通过，
/// 直到界面上显示不出分路图标才被发现。
/// 需要与原实现逐字比对时用 <see cref="MatchPositionText.ToLcuName"/>。
/// </summary>
public enum MatchPosition
{
    /// <summary>上单。</summary>
    Top,

    /// <summary>打野。</summary>
    Jungle,

    /// <summary>中单。</summary>
    Middle,

    /// <summary>下路（射手位）。</summary>
    Bottom,

    /// <summary>辅助。</summary>
    Utility,
}

/// <summary>
/// <see cref="MatchPosition"/> 与原实现字符串形态之间的唯一转换入口。
///
/// 之所以放进契约而不是留给解析器：解析器产出它、界面比对它、夹具回归断言它，
/// 三处若各写一份映射表，最容易出现的分歧恰好是 <c>MIDDLE</c> 与 <c>MID</c> 这类别名。
/// </summary>
public static class MatchPositionText
{
    /// <summary>转换为原实现使用的字符串（大写，无别名）。</summary>
    public static string ToLcuName(this MatchPosition position) => position switch
    {
        MatchPosition.Top => "TOP",
        MatchPosition.Jungle => "JUNGLE",
        MatchPosition.Middle => "MIDDLE",
        MatchPosition.Bottom => "BOTTOM",
        MatchPosition.Utility => "UTILITY",
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, "未知分路"),
    };

    /// <summary>
    /// 解析 LCU / SGP 下发的分路字符串。无法判定时返回 <c>null</c>——
    /// 与"未知"在语义上必须区分开：娱乐模式本就没有分路，退回默认值会伪造数据。
    /// </summary>
    /// <remarks>
    /// 接受 <c>MID</c> / <c>BOT</c> / <c>SUPPORT</c> 三个实际观察到的别名，
    /// 依据是原项目 <c>normalize_sgp_position</c> 与 <c>resolve_position</c> 的取值集合。
    /// </remarks>
    public static MatchPosition? TryParseLcuName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "TOP" => MatchPosition.Top,
            "JUNGLE" => MatchPosition.Jungle,
            "MIDDLE" or "MID" => MatchPosition.Middle,
            "BOTTOM" or "BOT" => MatchPosition.Bottom,
            "UTILITY" or "SUPPORT" => MatchPosition.Utility,
            _ => null,
        };
    }
}
