namespace YuumiX.Core.Features.Opgg.Models;

/// <summary>
/// 筛选选项元数据。
/// </summary>
public sealed record FilterOption(string Value, string Label);

/// <summary>
/// OP.GG 大区常量与元数据。
/// </summary>
public static class OpggRegion
{
    public const string Kr = "kr";
    public const string Global = "global";

    public static readonly IReadOnlyList<FilterOption> All =
    [
        new(Kr, "韩服"),
        new(Global, "全球")
    ];
}

/// <summary>
/// OP.GG 游戏模式常量与元数据。
/// </summary>
public static class OpggMode
{
    public const string Ranked = "ranked";
    public const string Normal = "normal";
    public const string Aram = "aram";
    public const string Arena = "arena";

    public static readonly IReadOnlyList<FilterOption> All =
    [
        new(Ranked, "排位模式"),
        new(Normal, "匹配模式"),
        new(Aram, "极地大乱斗"),
        new(Arena, "斗魂竞技场")
    ];
}

/// <summary>
/// OP.GG 段位筛选常量与元数据。
/// </summary>
public static class OpggTier
{
    public const string AllTier = "all";
    public const string GoldPlus = "gold_plus";
    public const string PlatinumPlus = "platinum_plus";
    public const string EmeraldPlus = "emerald_plus";
    public const string DiamondPlus = "diamond_plus";
    public const string Master = "master";
    public const string MasterPlus = "master_plus";
    public const string Grandmaster = "grandmaster";
    public const string Challenger = "challenger";

    public static readonly IReadOnlyList<FilterOption> All =
    [
        new(AllTier, "全部"),
        new(GoldPlus, "黄金+"),
        new(PlatinumPlus, "铂金+"),
        new(EmeraldPlus, "翡翠+"),
        new(DiamondPlus, "钻石+"),
        new(Master, "大师"),
        new(MasterPlus, "大师+"),
        new(Grandmaster, "宗师"),
        new(Challenger, "王者")
    ];
}

/// <summary>
/// OP.GG 位置常量与元数据（仅排位模式使用）。
/// </summary>
public static class OpggPosition
{
    public const string Top = "TOP";
    public const string Jungle = "JUNGLE";
    public const string Mid = "MID";
    public const string Adc = "ADC";
    public const string Support = "SUPPORT";

    public static readonly IReadOnlyList<FilterOption> All =
    [
        new(Top, "上单"),
        new(Jungle, "打野"),
        new(Mid, "中单"),
        new(Adc, "下路 (ADC)"),
        new(Support, "辅助")
    ];

    /// <summary>
    /// 将客户端 assigned_position 标准化为 OP.GG 位置格式。
    /// </summary>
    public static string FromAssignedPosition(string? pos) => (pos ?? "").ToLowerInvariant() switch
    {
        "top" => Top,
        "jungle" => Jungle,
        "middle" or "mid" => Mid,
        "bottom" or "bot" or "adc" => Adc,
        "utility" or "support" or "sup" => Support,
        _ => ""
    };
}
