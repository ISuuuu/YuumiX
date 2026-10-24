using Microsoft.UI.Reactor;

namespace YuumiX.App.Pages.Opgg;

/// <summary>
/// OP.GG 视觉规范常量与配色表（严格对齐 requirements-opgg.md 与旧版 Vue 实现）。
/// </summary>
public static class OpggVisuals
{
    public const string OpggBrandColor = "#636ff9";
    public const string ActiveTabGreen = "#2fbc5d";
    public const string ActiveTabGreenHover = "#25a44e";
    public const string WinGreen = "#2fbc5d";
    public const string LossRed = "#fa5252";
    public const string GoldRuneBorder = "#e5a93b";
    public const string GreenShardBorder = "#51cf66";

    /// <summary>
    /// Tier 盾牌底色徽章表。
    /// </summary>
    public static string GetTierBadgeColor(string? tier) => (tier ?? "").Trim() switch
    {
        "0" or "1" => "#5B8DB8", // T1 蓝
        "2" => "#5BA8A3",         // T2 青
        "3" => "#B89B52",         // T3 金
        "4" or "5" => "#7D8185", // T4 灰
        _ => "#7D8185"
    };

    /// <summary>
    /// Tier 卡片底色（根据深浅色模式与 Tier 等级匹配）。
    /// </summary>
    public static string GetTierCardBackground(string? tier, bool isDark)
    {
        var t = (tier ?? "").Trim();
        if (isDark)
        {
            return t switch
            {
                "0" or "1" => "rgba(59, 130, 246, 0.12)",
                "2" => "rgba(20, 184, 166, 0.12)",
                "3" => "rgba(234, 179, 8, 0.08)",
                "4" or "5" => "rgba(148, 163, 184, 0.08)",
                _ => "transparent"
            };
        }

        return t switch
        {
            "0" or "1" => "#CDE5F8",
            "2" => "#CDECEA",
            "3" => "#F4EAD1",
            "4" or "5" => "#E5E8EC",
            _ => "transparent"
        };
    }

    /// <summary>
    /// Tier 卡片边框色。
    /// </summary>
    public static string GetTierCardBorder(string? tier, bool isDark)
    {
        var t = (tier ?? "").Trim();
        if (isDark)
        {
            return t switch
            {
                "0" or "1" => "rgba(59, 130, 246, 0.25)",
                "2" => "rgba(20, 184, 166, 0.25)",
                "3" => "rgba(234, 179, 8, 0.20)",
                "4" or "5" => "rgba(148, 163, 184, 0.20)",
                _ => "rgba(255, 255, 255, 0.08)"
            };
        }

        return "rgba(0, 0, 0, 0.095)";
    }

    /// <summary>
    /// QWER 技能色块（文字色、背景色、边框色）。
    /// </summary>
    public static (string Text, string Bg, string Border) GetSkillKeyColor(string key) => key.ToUpperInvariant() switch
    {
        "Q" => ("#1c7ed6", "rgba(51, 154, 240, 0.08)", "rgba(51, 154, 240, 0.15)"),
        "W" => ("#2b8a3e", "rgba(55, 178, 77, 0.08)", "rgba(55, 178, 77, 0.15)"),
        "E" => ("#d9480f", "rgba(247, 103, 7, 0.08)", "rgba(247, 103, 7, 0.15)"),
        "R" => ("#c92a2a", "rgba(240, 62, 62, 0.08)", "rgba(240, 62, 62, 0.15)"),
        _ => ("#868e96", "rgba(134, 142, 150, 0.08)", "rgba(134, 142, 150, 0.15)")
    };

    public static string FormatPercent(double? v) => v.HasValue ? (v.Value * 100).ToString("F1") + "%" : "-";

    public static string FormatRatio(int win, int play) => play > 0 ? ((double)win * 100 / play).ToString("F1") + "%" : "-";

    public static string FormatKda(double? k) => k.HasValue ? k.Value.ToString("F2") : "-";
}
