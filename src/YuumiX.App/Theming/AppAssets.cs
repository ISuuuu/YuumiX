using YuumiX.Core.Features.Match.Models;

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

    /// <summary>OP.GG 品牌 Logo SVG 路径。</summary>
    public static string OpggLogoPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "opgg.svg");

    /// <summary><see cref="OpggLogoPath"/> 的 <c>file:///</c> 形式。</summary>
    public static string OpggLogoUri { get; } = File.Exists(OpggLogoPath) ? new Uri(OpggLogoPath).AbsoluteUri : "";

    /// <summary>
    /// 侧边栏专用 OP.GG 图标（54px PNG，显示面 16 DIP，约 3.4× 超采样）。
    /// 由 <c>opgg.svg</c> 光栅化而来。
    ///
    /// 为什么不直接用 <c>opgg.svg</c>：<c>IconResolver</c> 把 <c>ImageIconData</c> 解析成
    /// <c>ImageIcon { Source = new BitmapImage(uri) }</c>，而 <c>BitmapImage</c> 不认 SVG——
    /// 加载静默失败、图标整个消失。WinUI 原生的
    /// <c>IconSourceElement + ImageIconSource + SvgImageSource</c> 确实能在 <c>IconElement</c>
    /// 槽位放矢量，但 Reactor 没有对应工厂（<c>IconData</c> 只有 Bitmap/Font/Image/Path/Symbol
    /// 五个封闭 record，<c>RegisterHandler</c> 又只对 <c>Element</c> 生效），所以只能走位图。
    ///
    /// 尺寸取 54px 而非 18px：旧 18px 源在 200% 缩放下要放大 1.78×（方块内的白字糊成一团），
    /// 54px 在 200%~300% 都是降采样，观感与矢量等价。
    /// </summary>
    public static string OpggNavIconPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "opgg-nav.png");

    /// <summary><see cref="OpggNavIconPath"/> 的 <c>file:///</c> 形式。</summary>
    public static string OpggNavIconUri { get; } =
        File.Exists(OpggNavIconPath) ? new Uri(OpggNavIconPath).AbsoluteUri : "";

    /// <summary>OP.GG 梯度 T1~T4 盾牌图标 URI 字典。</summary>
    public static IReadOnlyDictionary<string, string> TierIconUris { get; } = BuildTierIconUris();

    private static Dictionary<string, string> BuildTierIconUris()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Tier");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i <= 4; i++)
        {
            string path = Path.Combine(directory, $"tier-{i}.svg");
            if (File.Exists(path))
            {
                var uri = new Uri(path).AbsoluteUri;
                result[i.ToString()] = uri;
                if (i == 1) result["0"] = uri;
                if (i == 4) result["5"] = uri;
            }
        }
        return result;
    }

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

    /// <summary>
    /// 对局详情双方资源统计图标（<c>Assets/MatchDetail/*.svg</c>）。
    ///
    /// 共 11 个矢量文件：击杀、防御塔、巨龙、男爵、先锋、水晶，除击杀外都含
    /// <c>-100</c>（蓝方）/ <c>-200</c>（红方）两套配色。全部由 <c>Image()</c> 渲染，
    /// 而 <c>Image()</c> 按扩展名分派到 <c>SvgImageSource</c>，所以能直接吃 SVG，
    /// 不受 <c>IconData</c> 通道只能位图的限制。这批图标显示面仅 16 DIP，
    /// 矢量化后在任意缩放下都是重新光栅化，不会像原 14px/36px 位图那样放大发虚。
    ///
    /// 探测顺序 <c>.svg</c> → <c>.png</c>，PNG 分支作为兜底保留；调用方一律沿用
    /// <c>kills.png</c> 这类旧名字，扩展名在这里被剥离，格式调整不需要动调用点。
    /// </summary>
    public static string? GetMatchDetailIconUri(string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Assets", "MatchDetail");
        string stem = Path.GetFileNameWithoutExtension(name);

        string svgPath = Path.Combine(directory, stem + ".svg");
        if (File.Exists(svgPath))
        {
            return new Uri(svgPath).AbsoluteUri;
        }

        string pngPath = Path.Combine(directory, stem + ".png");
        return File.Exists(pngPath) ? new Uri(pngPath).AbsoluteUri : null;
    }

    /// <summary>击杀图标 URI。</summary>
    public static string? KillsIconUri { get; } = GetMatchDetailIconUri("kills.png");

    /// <summary>根据资源类型与队伍ID（100/200）获取对应的资源统计图标 URI。</summary>
    public static string? GetTeamResourceIconUri(string resource, int teamId) =>
        GetMatchDetailIconUri($"{resource}-{teamId}.png");

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

    /// <summary>
    /// 分路位置图标（<c>Assets/Positions/{top|jungle|middle|bottom|utility}.png</c>）。
    /// </summary>
    public static IReadOnlyDictionary<MatchPosition, string> PositionIconUris { get; } = BuildPositionIconUris();

    public static string? GetPositionIconUri(MatchPosition? position) =>
        position.HasValue && PositionIconUris.TryGetValue(position.Value, out string? uri) ? uri : null;

    private static Dictionary<MatchPosition, string> BuildPositionIconUris()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Positions");
        var map = new (MatchPosition Pos, string File)[]
        {
            (MatchPosition.Top, "top.png"),
            (MatchPosition.Jungle, "jungle.png"),
            (MatchPosition.Middle, "middle.png"),
            (MatchPosition.Bottom, "bottom.png"),
            (MatchPosition.Utility, "utility.png"),
        };

        var result = new Dictionary<MatchPosition, string>();
        foreach (var (pos, file) in map)
        {
            string path = Path.Combine(directory, file);
            if (File.Exists(path))
            {
                result[pos] = new Uri(path).AbsoluteUri;
            }
        }
        return result;
    }
}
