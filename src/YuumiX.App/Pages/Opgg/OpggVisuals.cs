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
                "0" or "1" => "#263851", // OP.GG Tier 1 业务色
                "2" => "#203D3C",         // OP.GG Tier 2 业务色
                "3" => "#423A22",         // OP.GG Tier 3 业务色
                "4" or "5" => "#353A42", // OP.GG Tier 4/5 业务色
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
                "0" or "1" => "#456DA8", // OP.GG Tier 1 业务色
                "2" => "#39867E",         // OP.GG Tier 2 业务色
                "3" => "#8A793D",         // OP.GG Tier 3 业务色
                "4" or "5" => "#687481", // OP.GG Tier 4/5 业务色
                _ => "#686868"
            };
        }

        return "rgba(0, 0, 0, 0.095)";
    }

    /// <summary>
    /// QWER 技能色块（文字色、背景色）。实心底 + 白字：原先的 8% 半透明底
    /// 在深色主题下几乎不可见，也与 OP.GG 官网技能色块、站内实心按钮的语言一致。
    /// </summary>
    public static (string Text, string Bg) GetSkillKeyColor(string key) => key.ToUpperInvariant() switch
    {
        "Q" => ("#FFFFFF", "#339af0"),
        "W" => ("#FFFFFF", "#37b24d"),
        "E" => ("#FFFFFF", "#f76707"),
        "R" => ("#FFFFFF", "#f03e3e"),
        _ => ("#FFFFFF", "#868e96")
    };

    public static string FormatPercent(double? v) => v.HasValue ? (v.Value * 100).ToString("F1") + "%" : "-";

    public static string FormatRatio(int win, int play) => play > 0 ? ((double)win * 100 / play).ToString("F1") + "%" : "-";

    public static string FormatKda(double? k) => k.HasValue ? k.Value.ToString("F2") : "-";
}
