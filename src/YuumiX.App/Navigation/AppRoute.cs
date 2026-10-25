using YuumiX.App.Theming;
using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.App.Navigation;

/// <summary>
/// 主导航路由（R8 路由化：参数跟着路由走，替代全局单例 payload）。
/// 对应原 Yuumi 的 currentPage 取值集合；BenchOverlay（大乱斗悬浮窗）与 Opgg（独立窗口）
/// 不属于主导航，由窗口系统单独管理。
///
/// record 值相等性承载两个语义：
/// 1. 无参实例彼此相等——菜单在当前页重复点击不触发导航（对齐旧 enum 同值不跳）；
/// 2. 带参实例参数不同即不等——同一页面以不同参数再次跳转也能触发更新。
/// </summary>
public abstract record AppRoute
{
    private AppRoute()
    {
    }

    /// <summary>主页。</summary>
    public sealed record HomeRoute : AppRoute;

    /// <summary>
    /// 生涯。无参 = 我的生涯；带 <paramref name="Puuid"/> = 查看他人生涯
    /// （最近队友卡片、对局信息玩家卡片、搜索结果等入口，需求 §2.8 / §11.4）。
    /// </summary>
    public sealed record CareerRoute(string? Puuid = null, SummonerProfile? Preload = null) : AppRoute;

    /// <summary>
    /// 战绩查询。无参 = 空白进入；带参 = 自动发起查询并可选定某局
    /// （生涯资料卡 gameId: -1 只搜索；对局卡片 gameId &gt; 0 搜索并选中该局）。
    /// </summary>
    public sealed record SearchRoute(string? Query = null, string? Puuid = null, long? GameId = null) : AppRoute;

    /// <summary>对局信息。</summary>
    public sealed record GameInfoRoute : AppRoute;

    /// <summary>路人集。</summary>
    public sealed record SavedPlayersRoute : AppRoute;

    /// <summary>云顶之弈（占位）。</summary>
    public sealed record TftRoute : AppRoute;

    /// <summary>工具箱。</summary>
    public sealed record ToolsRoute : AppRoute;

    /// <summary>设置。</summary>
    public sealed record SettingsRoute : AppRoute;

    /// <summary>公告（仅为迁移清单保留，实际公告走右下角弹窗不切页）。</summary>
    public sealed record NoticeRoute : AppRoute;
}

/// <summary>
/// 路由元数据：侧边栏标题与 Segoe Fluent Icons 字形。
/// <see cref="Key"/> 是菜单 tag 与当前路由高亮匹配用的稳定字符串
/// （record 的 ToString 含参数值，不能直接当 tag）。
/// </summary>
public sealed record RouteDescriptor(string Key, string Label, string Glyph);

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
/// 而不是路由项（<see cref="AppRoute.NoticeRoute"/> 仅为迁移清单保留，不再参与导航）。
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
        new("Home",         "主页",     Icons.Home),
        new("Career",       "生涯",     Icons.Career),
        new("Search",       "战绩查询", Icons.Search),
        new("GameInfo",     "对局信息", Icons.GameInfo),
        new("Tft",          "云顶之弈", Icons.Tft),
        new("SavedPlayers", "路人集",   Icons.People),
        new("Tools",        "工具箱",   Icons.Tools),
    ];

    /// <summary>
    /// 底部附加操作区里的**路由**项：设置。
    /// </summary>
    public static readonly IReadOnlyList<RouteDescriptor> Footer =
    [
        new("Settings", "设置", Icons.Settings),
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

    /// <summary>菜单 tag → 无参路由实例；不是路由 tag 时返回 null（动作项走各自分支）。</summary>
    public static AppRoute? Resolve(string key) => key switch
    {
        "Home" => new AppRoute.HomeRoute(),
        "Career" => new AppRoute.CareerRoute(),
        "Search" => new AppRoute.SearchRoute(),
        "GameInfo" => new AppRoute.GameInfoRoute(),
        "SavedPlayers" => new AppRoute.SavedPlayersRoute(),
        "Tft" => new AppRoute.TftRoute(),
        "Tools" => new AppRoute.ToolsRoute(),
        "Settings" => new AppRoute.SettingsRoute(),
        "Notice" => new AppRoute.NoticeRoute(),
        _ => null,
    };

    /// <summary>当前路由 → 菜单 tag（带参路由也归到所属菜单项，用于高亮匹配）。</summary>
    public static string KeyFor(AppRoute route) => route switch
    {
        AppRoute.HomeRoute => "Home",
        AppRoute.CareerRoute => "Career",
        AppRoute.SearchRoute => "Search",
        AppRoute.GameInfoRoute => "GameInfo",
        AppRoute.SavedPlayersRoute => "SavedPlayers",
        AppRoute.TftRoute => "Tft",
        AppRoute.ToolsRoute => "Tools",
        AppRoute.SettingsRoute => "Settings",
        AppRoute.NoticeRoute => "Notice",
        _ => string.Empty,
    };
}
