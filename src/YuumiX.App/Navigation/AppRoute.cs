using YuumiX.App.Theming;

namespace YuumiX.App.Navigation;

/// <summary>
/// 主导航路由。对应原 Yuumi 的 currentPage 取值集合。
/// BenchOverlay（大乱斗悬浮窗）与 Opgg（独立窗口）不属于主导航，由窗口系统单独管理。
/// </summary>
public enum AppRoute
{
    Home,
    Career,
    Search,
    GameInfo,
    SavedPlayers,
    Tft,
    Tools,
    Settings,
    Notice,
}

/// <summary>
/// 路由元数据：侧边栏标题与 Segoe Fluent Icons 字形。
/// </summary>
public sealed record RouteDescriptor(AppRoute Route, string Label, string Glyph);

/// <summary>
/// 侧边栏动作项元数据：不改变主导航路由，只在底部附加操作区触发一次性行为
/// （打开独立窗口、执行一次修复等）。
/// </summary>
public sealed record SidebarAction(string Tag, string Label, string Glyph);

/// <summary>侧边栏动作项的 Tag 常量。</summary>
public static class SidebarTags
{
    /// <summary>OP.GG：打开独立窗口（沿用原实现，不属于主导航）。</summary>
    public const string Opgg = "Opgg";

    /// <summary>修复无限加载：手动重连客户端。新项目暂无手动入口，先预留位置。</summary>
    public const string Reconnect = "Reconnect";

    /// <summary>公告：显示右下角公告弹窗（对应旧版 navigate('notice') → noticeVisible = true）。</summary>
    public const string Notice = "Notice";
}

/// <summary>
/// 侧边栏菜单的元数据源。结构对齐原 Yuumi 的 <c>NavigationSidebar.vue</c>：
/// <list type="bullet">
///   <item><description>主功能区 <see cref="Primary"/>：主页 / 生涯 / 战绩查询 / 对局信息 / 云顶之弈 / 路人集 / 工具箱；</description></item>
///   <item><description>底部附加操作区：OP.GG / 修复无限加载 / 公告（动作项，见 <see cref="FooterActions"/>）+
///         设置（路由项，见 <see cref="Footer"/>）。</description></item>
/// </list>
///
/// 底部区的最终顺序 = <see cref="FooterActions"/> 全部 + <see cref="Footer"/> 全部，
/// 与旧版 sidebar-bottom 的排列一致（OP.GG → 修复无限加载 → 公告 → 设置）。
///
/// 注意：公告在旧版里点击后**不切换页面**，只是弹出 NoticePopup——因此它是动作项
/// 而不是路由项（<see cref="AppRoute.Notice"/> 仅为迁移清单保留，不再参与导航）。
///
/// 图标沿用本项目既有的 Segoe Fluent Icons 字形——用户已确认符合要求，不要改动。
/// </summary>
public static class RouteCatalog
{
    /// <summary>
    /// 主功能区菜单项。顺序与旧版一致：云顶之弈在路人集之前。
    /// </summary>
    public static readonly IReadOnlyList<RouteDescriptor> Primary =
    [
        new(AppRoute.Home,         "主页",     Icons.Home),
        new(AppRoute.Career,       "生涯",     Icons.Career),
        new(AppRoute.Search,       "战绩查询", Icons.Search),
        new(AppRoute.GameInfo,     "对局信息", Icons.GameInfo),
        new(AppRoute.Tft,          "云顶之弈", Icons.Tft),
        new(AppRoute.SavedPlayers, "路人集",   Icons.People),
        new(AppRoute.Tools,        "工具箱",   Icons.Tools),
    ];

    /// <summary>
    /// 底部附加操作区里的**路由**项：设置。
    /// </summary>
    public static readonly IReadOnlyList<RouteDescriptor> Footer =
    [
        new(AppRoute.Settings, "设置", Icons.Settings),
    ];

    /// <summary>
    /// 底部附加操作区里的**动作**项：不改变主导航路由，点击后执行一次性行为。
    /// 公告对应旧版行为：显示右下角 NoticePopup 弹窗，页面本身不切换。
    /// </summary>
    public static readonly IReadOnlyList<SidebarAction> FooterActions =
    [
        new(SidebarTags.Opgg,      "OP.GG",       Icons.Opgg),
        new(SidebarTags.Reconnect, "修复无限加载", Icons.Refresh),
        new(SidebarTags.Notice,    "公告",         Icons.Notice),
    ];

    /// <summary>全部路由项（主功能区 + 底部附加操作区）。</summary>
    public static readonly IReadOnlyList<RouteDescriptor> All = [.. Primary, .. Footer];

    public static RouteDescriptor For(AppRoute route) =>
        All.First(descriptor => descriptor.Route == route);
}
