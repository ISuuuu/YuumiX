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
}

/// <summary>
/// 路由元数据：侧边栏标题与 Segoe Fluent Icons 字形。
/// </summary>
public sealed record RouteDescriptor(AppRoute Route, string Label, string Glyph);

public static class RouteCatalog
{
    public static readonly IReadOnlyList<RouteDescriptor> All =
    [
        new(AppRoute.Home,         "主页",     "\uE80F"),
        new(AppRoute.Career,       "生涯",     "\uE81E"),
        new(AppRoute.Search,       "战绩查询", "\uE721"),
        new(AppRoute.GameInfo,     "对局信息", "\uE7FC"),
        new(AppRoute.SavedPlayers, "路人集",   "\uE716"),
        new(AppRoute.Tft,          "云顶之弈", "\uE8B4"),
        new(AppRoute.Tools,        "工具箱",   "\uE90F"),
        new(AppRoute.Settings,     "设置",     "\uE713"),
    ];

    public static RouteDescriptor For(AppRoute route) =>
        All.First(descriptor => descriptor.Route == route);
}
