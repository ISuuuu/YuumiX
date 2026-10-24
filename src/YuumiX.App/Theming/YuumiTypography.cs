namespace YuumiX.App.Theming;

/// <summary>
/// 全局字体与排版体系（对齐 Fluent 规范与无衬线字体层级）。
/// </summary>
public static class YuumiTypography
{
    /// <summary>
    /// 全局西文+数字无衬线字体栈。优先加载本地分发的 Outfit，自动回退到 Segoe UI 与微软雅黑。
    /// </summary>
    public static string SansFont { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Outfit.ttf"))
            ? "ms-appx:///Assets/Fonts/Outfit.ttf#Outfit, Segoe UI Variable Display, Microsoft YaHei UI"
            : "Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI";

    /// <summary>等宽代码/数值字体栈。</summary>
    public const string MonoFont = "Cascadia Mono, Consolas, SF Mono, Microsoft YaHei UI";

    /// <summary>Fluent 图标字体。</summary>
    public const string IconFont = "Segoe Fluent Icons";

    // ─── 统一字阶规范（Fluent Type Scale） ───
    /// <summary>页面大标题字号（28px）。</summary>
    public const double PageTitleSize = 28;

    /// <summary>模块二级标题字号（16px）。</summary>
    public const double SubtitleSize = 16;

    /// <summary>设置分组小节标题字号（13px，SemiBold）。</summary>
    public const double SectionHeaderSize = 13;

    /// <summary>卡片主标题字号（13.5px，SemiBold）。</summary>
    public const double CardTitleSize = 13.5;

    /// <summary>常规正文与控件字号（13px）。</summary>
    public const double BodySize = 13;

    /// <summary>说明、注释与状态微标字号（12px）。</summary>
    public const double CaptionSize = 12;

    /// <summary>超微角标/状态提示字号（11px）。</summary>
    public const double BadgeSize = 11;
}
