namespace YuumiX.App.Theming;

/// <summary>
/// 战绩/游戏域的品牌与资产语义色（旧版 Yuumi 的 CSS 变量与 League 客户端资产色）。
///
/// 这些色不随深浅主题变化（阵营、精粹、货币、角标是游戏语义而非界面结构色），
/// 也没有对应的 WinUI 语义令牌——按 AGENTS.md §2 作为图表/品牌位例外集中在本类，
/// Pages 下禁止再新增 8 位 ARGB 字面量（R13 门禁的扫描范围排除 Theming 目录）。
/// </summary>
public static class AssetColors
{
    // ─── 胜负面板（原版 App.vue --win-color / --loss-color，Tailwind emerald / rose）───
    public const string WinDark = "#FF10B981";
    public const string WinLight = "#FF065F46";
    public const string LoseDark = "#FFF43F5E";
    public const string LoseLight = "#FF9F1239";

    // ─── 货币/资产 ───
    public const string BlueEssence = "#FF3B82F6";   // 蓝色精粹（Tailwind blue-500）
    public const string OrangeEssence = "#FFF97316"; // 橙色精粹（Tailwind orange-500）
    public const string Gold = "#FFEAB308";          // 金币/钥匙（Tailwind yellow-500）

    // ─── 红蓝方阵营（League 客户端蓝/红语义；色值与精粹蓝、rose-500 相同但语义独立）───
    public const string TeamBlue = "#FF3B82F6";
    public const string TeamRed = "#FFF43F5E";

    // ─── 评价/成就（对齐旧版战绩评价徽标配色）───
    public const string Amber = "#FFF59E0B";      // KDA 最高档金/「需要钥匙」/「招牌」前景（旧版 vue #f59e0b）
    public const string AmberDeep = "#FFD97706";  // Perfect 评价/「战绩已隐藏」（Tailwind amber-600）
    public const string AmberFaint = "#25F59E0B"; // 「招牌」徽标淡底（amber 15%）
    public const string Rose = "#FFDB2777";       // 高 KDA 玫红（Tailwind pink-600）
    public const string Blue = "#FF2563EB";       // 评价蓝（Tailwind blue-600）
    public const string BlueSoft = "#FF60A5FA";   // KDA 次高档蓝，深色态（Tailwind blue-400）
    public const string BlueFaint = "#203B82F6";  // 「单双」徽标淡底（blue 12%）

    /// <summary>纯金：英雄熟练度顶级描边 / 经验条满级（旧版 mastery 金，小字号下过亮只用于描边与底）。</summary>
    public const string PureGold = "#FFFFD700";

    // ─── 头像角标 ───
    public const string BadgeDark = "#D00F172A";  // 等级微标底（slate-900 82%，旧版 badge 样式）
    public const string BadgeRose = "#E0E06B6B";  // 「练英雄」角标底（rose 88%）

    /// <summary>低胜率徽标灰蓝（slate-400，裁决 D5：低胜率不是"失败"，只是平淡）。</summary>
    public const string LowWinRate = "#FF94A3B8";

    // ─── 计时轨道（旧版 --gauge-track-color 深浅两套）───
    public const string TrackDark = "#FF334155";
    public const string TrackLight = "#FFE2E8F0";

    // ─── 海克斯强化符文（旧版 augment-color 紫）───
    public const string AugmentBackground = "#1F9333EA";
    public const string AugmentBorder = "#73A855F7";
}
