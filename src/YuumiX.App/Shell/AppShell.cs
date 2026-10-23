using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Diagnostics;
using YuumiX.App.Navigation;
using YuumiX.App.Pages;
using YuumiX.App.Theming;

namespace YuumiX.App.Shell;

/// <summary>
/// 应用外壳：自定义标题栏 + 侧边导航 + 内容宿主。
///
/// 对应原实现的 App.vue（CustomTitleBar + NavigationSidebar + 页面 v-show 切换）。
/// 差异说明：原实现用 v-show 保留 Search/GameInfo 的挂载状态以避免重新拉取数据，
/// Reactor 侧由 NavigationHost 的 CacheMode 承担该职责，后续接入真实页面时再配置。
///
/// 本组件是上下文根：它位于 <c>Provide</c> 之上，读不到自己提供的上下文，
/// 因此服务直接取自 <see cref="AppHost.Services"/>；子组件一律走 <c>UseContext</c>。
/// </summary>
public sealed class AppShell : Component
{
    public override Element Render()
    {
        var services = AppHost.Services;
        var nav = UseNavigation(AppRoute.Home);
        var current = RouteCatalog.For(nav.CurrentRoute);

        var menuItems = RouteCatalog.All
            .Select(item => NavItem(item.Label, item.Glyph, item.Route.ToString()))
            .ToArray();

        var navigationView = NavigationView(menuItems, NavigationHost(nav, BuildPage)) with
        {
            SelectedTag = nav.CurrentRoute.ToString(),
            IsSettingsVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            OpenPaneLength = 200,
            IsPaneToggleButtonVisible = true,
        };

        // 侧边栏是平级切换语义，因此用 Replace 而非 Navigate，避免堆积返回栈。
        navigationView = navigationView with
        {
            OnSelectedTagChanged = tag =>
            {
                if (Enum.TryParse<AppRoute>(tag, out var route) && route != nav.CurrentRoute)
                {
                    nav.Replace(route);
                }
            },
        };

        var titleBar = TitleBar("YuumiX") with
        {
            Subtitle = $"{current.Label} · 配置 v{services.Config.Version}",
            IsBackButtonVisible = false,
            IsPaneToggleButtonVisible = true,
            OnPaneToggleRequested = () => { },
            RightHeader = Button("架构验证", OpenCapabilityLab),
        };

        return Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Star()],
            [
                titleBar.Grid(row: 0),
                navigationView.Grid(row: 1),
            ]
        )
        .Backdrop(YuumiTheme.DefaultBackdrop)
        .Provide(AppHost.Context, services);
    }

    private static Element BuildPage(AppRoute route)
    {
        var (title, sourceView, pending) = PageCatalog.For(route);
        return Component<PlaceholderPage, PlaceholderProps>(
            new PlaceholderProps(title, sourceView, pending));
    }

    /// <summary>打开架构能力验证窗口（独立窗口，顺带验证多窗口能力）。</summary>
    private static void OpenCapabilityLab()
    {
        ReactorApp.OpenWindow(
            new WindowSpec
            {
                Title = "YuumiX 架构能力验证",
                Width = 760,
                Height = 700,
                Backdrop = BackdropChoice.Of(BackdropKind.Mica),
                StartPosition = WindowStartPosition.CenterOnCurrent,
            },
            () => new CapabilityLab());
    }
}
