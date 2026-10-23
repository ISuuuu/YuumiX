namespace YuumiX.App.Theming;

/// <summary>
/// 品牌静态资源的定位（二进制资源本体在 <c>YuumiX.App/Assets</c>，随 exe 分发到输出的
/// <c>Assets/</c> 目录）。
///
/// 与 <c>Core/Assets</c> 的区别：那里是**运行期从 LCU / CDragon 下载并缓存**的游戏资源；
/// 这里是编译期就随程序发布的品牌资源（应用图标、Logo），没有解析链，也不该走缓存。
///
/// 路径一律基于 <see cref="AppContext.BaseDirectory"/>：本项目是非打包（portable）模式，
/// 没有 <c>ms-appx:///</c> 虚拟根，输出目录就是 exe 所在目录。
///
/// 之所以暴露 <c>file:///</c> URI 而不只暴露路径：Reactor 的 <c>Image</c> / <c>ImageIcon</c>
/// 只吃 URI 字符串，而把 Windows 路径直接拼成 "file://" 在含空格或中文的目录下是非法 URI
/// （本机用户目录就是中文），必须交给 <see cref="Uri"/> 转义。
///
/// 代码放在 Theming 而非 Assets 目录：WinUI 的静态资源规则会把 <c>Assets/</c> 下的**所有**
/// 文件整体复制到输出目录，源码放进去会被当成资源一起拷出去。
/// </summary>
public static class AppAssets
{
    /// <summary>品牌 Logo 原图（614×614 透明 PNG），用于主页展示与标题栏图标。</summary>
    public static string LogoPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Logo.png");

    /// <summary>多尺寸应用图标（16/24/32/48/64/256），用于窗口与任务栏。</summary>
    public static string AppIconPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

    /// <summary><see cref="LogoPath"/> 的 <c>file:///</c> 形式，供 Reactor 图片元素使用。</summary>
    public static string LogoUri { get; } = new Uri(LogoPath).AbsoluteUri;

    /// <summary>
    /// 段位奖章图标（<c>Assets/RankedIcons/&lt;tier&gt;.png</c>）。
    ///
    /// 这些是**品牌静态资源**而不是 LCU 运行期资源：原项目直接从
    /// <c>src/assets/ranked-icons</c> 打包，不经过 CDragon，因此不能走
    /// <c>AssetService</c>（那条链要客户端在线才有图，而段位表在离线时也应该能显示）。
    ///
    /// 在类型初始化时一次性算出 URI 并缓存：排位表每行都要取一次，
    /// 若每次 <c>File.Exists</c> + <c>new Uri</c>，一次渲染就是十几次文件系统探测。
    /// </summary>
    public static IReadOnlyDictionary<string, string> RankedIconUris { get; } = BuildRankedIconUris();

    private static Dictionary<string, string> BuildRankedIconUris()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Assets", "RankedIcons");
        string[] tiers =
        [
            "IRON", "BRONZE", "SILVER", "GOLD", "PLATINUM",
            "EMERALD", "DIAMOND", "MASTER", "GRANDMASTER", "CHALLENGER",
        ];

        var result = new Dictionary<string, string>(tiers.Length, StringComparer.Ordinal);
        foreach (string tier in tiers)
        {
            string path = Path.Combine(directory, tier.ToLowerInvariant() + ".png");
            if (File.Exists(path))
            {
                result[tier] = new Uri(path).AbsoluteUri;
            }
        }

        return result;
    }
}
