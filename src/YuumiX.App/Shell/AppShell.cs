using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Navigation;
using YuumiX.App.Pages;
using YuumiX.App.Pages.Career;
using YuumiX.App.Pages.GameInfo;
using YuumiX.App.Pages.Home;
using YuumiX.App.Pages.SavedPlayers;
using YuumiX.App.Pages.Search;
using YuumiX.App.Pages.Settings;
using YuumiX.App.Pages.Tools;
using YuumiX.App.Theming;
using YuumiX.App.Windowing;
using YuumiX.Core.Automation;
using YuumiX.Core.Lcu;

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

        // 标题栏游戏状态：LCU 连接态 + 游戏阶段是本页的重渲染来源。
        // threadSafe: true —— 推送来自 LCU 后台线程（ADR-07），由 UseState 封送回 UI 线程。
        var lcu = services.Services.GetRequiredService<LcuConnection>();
        var automationHost = services.Services.GetRequiredService<AutomationHost>();
        var (gamePhase, setGamePhase) = UseState(GamePhase.None, threadSafe: true);
        var (isClientConnected, setIsClientConnected) = UseState(
            lcu.CurrentState.Status == LcuConnectionStatus.Connected,
            threadSafe: true);
        var (toasts, setToasts) = UseState<IReadOnlyList<ToastItem>>(Array.Empty<ToastItem>());

        void ShowToast(string message, bool isError = false)
        {
            var item = new ToastItem(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), message, isError);
            setToasts([.. toasts, item]);
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000).ConfigureAwait(false);
                setToasts([.. toasts.Where(t => t.Id != item.Id)]);
            });
        }

        UseEffect(
            () =>
            {
                var phaseSubscription = automationHost.Phases.Subscribe(phase =>
                {
                    setGamePhase(phase);
                    if (phase == GamePhase.ChampSelect && services.Config.Functions.AutoShowOpgg)
                    {
                        OpggWindowHost.ShowOrActivate();
                    }
                });
                var stateSubscription = lcu.States.Subscribe(state =>
                    setIsClientConnected(state.Status == LcuConnectionStatus.Connected));
                return () =>
                {
                    phaseSubscription.Dispose();
                    stateSubscription.Dispose();
                };
            },
            0);

        // 主题状态是本页的重渲染来源之一：设置页改了主题色/模式/Mica 后，
        // ThemeState 通过 EventStream 推过来，这里 setPalette 触发整体重渲染。
        // threadSafe: true —— 推送可能来自保存完成的回调线程（ADR-07）。
        var (palette, setPalette) = UseState(AppHost.Theme.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = AppHost.Theme.Subscribe(setPalette);
                return () => subscription.Dispose();
            },
            0);

        // 菜单可见性是另一个重渲染来源：设置页切「隐藏菜单」开关后，
        // MenuVisibilityState 推过来，重新计算带过滤的菜单（同主题的 threadSafe 语义）。
        var menuState = AppHost.Menu;
        var (menuVisibility, setMenuVisibility) = UseState(menuState, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = menuState.Subscribe(setMenuVisibility);
                return () => subscription.Dispose();
            },
            1);

        // 菜单项必须跨渲染保持同一批实例：每次渲染新建数组会让 NavigationView 重建菜单，
        // 选中项被重置为第一项并触发 OnSelectedTagChanged，表现为「界面重渲染后莫名跳回主页」。
        // 这是主题即时生效暴露出来的问题（ADR-12 决策三带来的额外重渲染）。
        // key 为可见性签名：只在「隐藏菜单」配置实际变化时重建菜单，
        // 平时（主题变化/普通重渲染）保持原实例数组。
        var menuKey = (menuVisibility.HideTft ? 1 : 0) | (menuVisibility.HideSavedPlayers ? 2 : 0);
        var menuItems = UseMemo(
            () => RouteCatalog.All
                .Where(item => item.Route switch
                {
                    AppRoute.Tft => !menuVisibility.HideTft,
                    AppRoute.SavedPlayers => !menuVisibility.HideSavedPlayers,
                    _ => true,
                })
                .Select(item => NavItem(item.Label, item.Glyph, item.Route.ToString()))
                .ToArray(),
            menuKey);

        // 导航栏开合：初值取自持久化配置（跨启动恢复上次状态）。本地 UseState 保证
        // 主题/菜单变化引发重渲染时，用户当前的开合状态不会被重置回配置初值。
        var (paneOpen, setPaneOpen) = UseState(services.Config.Personalization.NavigationPaneOpen);

        var navigationView = NavigationView(menuItems, NavigationHost(nav, BuildPage)) with
        {
            SelectedTag = nav.CurrentRoute.ToString(),
            IsSettingsVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            OpenPaneLength = 150,
            IsPaneOpen = paneOpen,
            IsPaneToggleButtonVisible = true,
            FooterMenuItems = [NavItem("OP.GG", "\uE737", "Opgg")],
        };

        // 侧边栏是平级切换语义，因此用 Replace 而非 Navigate，避免堆积返回栈。
        navigationView = navigationView with
        {
            OnSelectedTagChanged = tag =>
            {
                if (tag == "Opgg")
                {
                    if (!isClientConnected)
                    {
                        ShowToast("请先连接英雄联盟客户端", isError: true);
                    }
                    else
                    {
                        OpggWindowHost.ShowOrActivate();
                    }
                    return;
                }

                if (Enum.TryParse<AppRoute>(tag, out var route) && route != nav.CurrentRoute)
                {
                    nav.Replace(route);
                }
            },
        };

        // 开合变化 → 更新本地状态并把值写回 config.json（跨启动恢复）。
        // 回调来自 UI 线程，setPaneOpen 无需 threadSafe；写配置沿用设置页的做法
        // （SaveWithBackup + 异常记日志，见 ADR 对应约定：Save 失败不可静默吞）。
        navigationView = navigationView.PaneOpenChanged(open =>
        {
            setPaneOpen(open);

            if (services.Config.Personalization.NavigationPaneOpen == open)
            {
                return;
            }

            services.Config.Personalization.NavigationPaneOpen = open;
            try
            {
                services.ConfigStore.SaveWithBackup(services.Config);
            }
            catch (Exception ex)
            {
                services.LoggerFactory
                    .CreateLogger<AppShell>()
                    .LogWarning(ex, "导航栏开合状态写入 config.json 失败，本次变更不会跨启动保留");
            }
        });

        var titleBar = TitleBar("YuumiX") with
        {
            // 标题栏图标与原 CustomTitleBar.vue 的 titlebar-logo 对齐：同一个 Logo 文件。
            // 用 ImageIcon 而非 BitmapIcon —— 后者默认单色化，会把彩色 Logo 染成主题色。
            Icon = ImageIcon(new Uri(AppAssets.LogoUri)),
            // 对应原 CustomTitleBar.vue 的 titlebar-phase：已连接且非空闲时展示阶段中文名。
            Subtitle = GetTitleBarPhaseSubtitle(isClientConnected, gamePhase),
            IsBackButtonVisible = false,
        };

        Element RenderToasts()
        {
            if (toasts.Count == 0) return Empty();

            return VStack(8,
                toasts.Select(item =>
                {
                    var iconGlyph = item.IsError ? "\uE783" : "\uE73E";
                    var iconColor = item.IsError
                        ? Theme.Ref("SystemFillColorCriticalBrush")
                        : Theme.Ref("SystemFillColorSuccessBrush");

                    return Border(
                        HStack(10,
                            Border(
                                TextBlock(iconGlyph)
                                    .FontFamily("Segoe Fluent Icons")
                                    .FontSize(11)
                                    .Foreground(iconColor)
                                    .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Center)
                                    .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center)
                            )
                            .Width(22)
                            .Height(22)
                            .CornerRadius(11)
                            .Background(Theme.Ref("LayerFillColorDefaultBrush")),

                            TextBlock(item.Message)
                                .FontSize(13)
                                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                                .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center)
                        )
                    )
                    .Padding(new Microsoft.UI.Xaml.Thickness(16, 9, 20, 9))
                    .CornerRadius(20)
                    .Background(Theme.CardBackground)
                    .WithBorder(Theme.CardStroke, 1)
                    .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Center)
                    .WithKey(item.Id.ToString());
                }).ToArray()
            )
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Top)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Center)
            .Margin(new Microsoft.UI.Xaml.Thickness(0, 48, 0, 0));
        }

        return Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Star()],
            [
                titleBar.Grid(row: 0),
                navigationView.Grid(row: 1),
                RenderToasts().Grid(row: 1),
            ]
        )
        .Backdrop(palette.Backdrop)
        .RequestedTheme(palette.ElementTheme)
        .Provide(AppHost.Context, services)
        .Provide(AppHost.ThemeContext, AppHost.Theme);
    }

    /// <summary>
    /// 计算标题栏副标题（游戏状态）。已连接且非空闲（None）阶段时返回阶段中文名，
    /// 否则返回空串隐藏（对齐原 CustomTitleBar.vue 的 <c>isConnected &amp;&amp; gamePhase !== 'None'</c> 规则）。
    /// </summary>
    private static string GetTitleBarPhaseSubtitle(bool isClientConnected, GamePhase gamePhase)
    {
        if (!isClientConnected || gamePhase is GamePhase.None or GamePhase.Unknown)
        {
            return string.Empty;
        }

        return gamePhase switch
        {
            GamePhase.Lobby => "房间组队中",
            GamePhase.Matchmaking => "匹配中",
            GamePhase.ReadyCheck => "确认对局",
            GamePhase.ChampSelect => "选择英雄",
            GamePhase.GameStart => "游戏加载",
            GamePhase.InProgress => "游戏中",
            GamePhase.Reconnect => "重新连接",
            GamePhase.WaitingForStats => "等待数据",
            GamePhase.PreEndOfGame => "结算中",
            GamePhase.EndOfGame => "对局结束",
            GamePhase.WatchInProgress => "观战中",
            _ => gamePhase.ToString(),
        };
    }

    /// <summary>
    /// 路由 → 页面组件。已迁移的模块在这里替换占位组件；其余仍走 <see cref="PlaceholderPage"/>，
    /// 待迁移清单以 <see cref="PageCatalog"/> 为准。
    /// </summary>
    private static Element BuildPage(AppRoute route) => route switch
    {
        AppRoute.Home => Component<HomePage>(),
        AppRoute.Career => Component<CareerPage>(),
        AppRoute.GameInfo => Component<GameInfoPage>(),
        AppRoute.SavedPlayers => Component<SavedPlayersPage>(),
        AppRoute.Search => Component<SearchPage>(),
        AppRoute.Settings => Component<SettingsPage>(),
        AppRoute.Tools => Component<ToolsPage>(),
        _ => BuildPlaceholder(route),
    };

    private static Element BuildPlaceholder(AppRoute route)
    {
        var (title, sourceView, pending) = PageCatalog.For(route);

        // 主页展示品牌 Logo（对应原 Home.vue 顶部 96px 的 Logo）；其余占位页不显示图片。
        var brandLogoUri = route == AppRoute.Home ? AppAssets.LogoUri : null;

        return Component<PlaceholderPage, PlaceholderProps>(
            new PlaceholderProps(title, sourceView, pending, brandLogoUri));
    }
}
