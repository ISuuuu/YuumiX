namespace YuumiX.App.Theming;

/// <summary>
/// 全局字体与排版体系（对齐原版 Outfit 与无衬线字体层级）。
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
}
