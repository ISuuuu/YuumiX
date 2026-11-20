using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Navigation;
using YuumiX.App.Pages.AramTest;
using YuumiX.App.Pages.Bench;
using YuumiX.App.Pages.Notice;
using YuumiX.App.Theming;
using YuumiX.App.Windowing;
using YuumiX.Core.Automation;
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Shell;

/// <summary>
/// 应用外壳：自定义标题栏 + 侧边导航 + 内容宿主。
///
/// 对应原实现的 App.vue（CustomTitleBar + NavigationSidebar + 页面 v-show 切换）。
/// 差异说明：原实现用 v-show 保留 Search/GameInfo 的挂载状态以避免重新拉取数据，
/// Reactor 侧由 NavigationHost 的 CacheMode 承担该职责。
///
/// 本组件是上下文根：它位于 <c>Provide</c> 之上，读不到自己提供的上下文，
/// 因此服务直接取自 <see cref="AppHost.Services"/>；子组件一律走 <c>UseContext</c>。
/// </summary>
public sealed class AppShell : Component
{
    /// <summary>
    /// 侧边栏收起时的图标栏宽度（DIP）。对齐旧版 <c>.sidebar.collapsed</c> 的 64px。
    /// 召唤师卡片在收起态要按这个宽度铺满整栏并把头像居中，所以这里单独定义常量供两处引用。
    /// </summary>
    private const double PaneCompactWidth = 64;

    /// <summary>侧边栏展开时的宽度（DIP），对齐旧版 <c>.sidebar.expanded</c> 的 210px。</summary>
    private const double PaneOpenWidth = 210;

    public override Element Render()
    {
        var services = AppHost.Services;
        var shellLogger = services.LoggerFactory.CreateLogger<AppShell>();

        // —— 托盘与窗口生命周期 ——
        // 托盘图标常驻：窗口被隐藏后它就是唯一的恢复入口，因此不能只在开关打开时才创建。
        var window = UseWindow();
        var exitRequested = UseRef(false);
        var tray = UseTrayIcon(new TrayIconSpec(
            WindowIcon.FromPath(AppAssets.AppIconPath),
            "YuumiX",
            IsVisible: true));

        // 依赖固定为 0：ReactorWindow 与 ReactorTrayIcon 的实例跨渲染稳定（AGENTS.md §5.7）。
        UseEffect(() => ShellWindowLifecycle.AttachCloseToTray(window, tray, exitRequested, services), 0);
        UseEffect(() => ShellWindowLifecycle.AttachFirstActivationVisibilityCheck(window), 0);
        UseEffect(() => ShellWindowLifecycle.AttachSilentStartupHide(window, tray, services), 0);
        UseEffect(() => ShellWindowLifecycle.AttachTrayInteractions(window, tray, exitRequested, services), 0);

        var reducedMotion = UseReducedMotion();
        var highContrast = UseHighContrast();
        var nav = UseNavigation<AppRoute>(new AppRoute.HomeRoute());

        // 标题栏游戏状态：LCU 连接态 + 游戏阶段是本页的重渲染来源。
        // threadSafe: true —— 推送来自 LCU 后台线程（ADR-07），由 UseState 封送回 UI 线程。
        var lcu = services.Services.GetRequiredService<LcuConnection>();
        var automationHost = services.Services.GetRequiredService<AutomationHost>();
        var (gamePhase, setGamePhase) = UseState(GamePhase.None, threadSafe: true);
        var (isClientConnected, setIsClientConnected) = UseState(
            lcu.CurrentState.Status == LcuConnectionStatus.Connected,
            threadSafe: true);

        // 全局 Toast 队列（R5）：AppShell 是组合根，读不到自己 Provide 的上下文，
        // 因此直接持有实例——既作为 Provide 的值，也供本组件提示使用。
        var toastQueueRef = UseRef<ToastQueue?>(null);
        var toastQueue = toastQueueRef.Current ??= new ToastQueue();
        // 公告弹窗可见性：侧边栏「公告」动作项触发显示；关闭由弹窗自身（手动 × 或 8s 自动）回调。
        var (noticeVisible, setShowNotice) = UseState(false, threadSafe: true);

        // —— 更新提示（侧边栏「设置」红点）——
        var updateNotice = AppHost.UpdateNotice;
        var (updateAvailable, setUpdateAvailable) = UseState(updateNotice.HasUpdate, threadSafe: true);
        var notifiedVersion = UseRef<string?>(null);
        UseEffect(
            () => ShellUpdateBadge.AttachUpdateNotice(
                updateNotice,
                notifiedVersion,
                setUpdateAvailable,
                toastQueue,
                shellLogger),
            0);

        // 红点覆盖层用到的三个原生引用：菜单项容器、圆点本体、覆盖画布。只在挂载回调里写，渲染期只读。
        var navViewRef = UseRef<NavigationView?>(null);
        var updateDotRef = UseRef<FrameworkElement?>(null);
        var updateOverlayRef = UseRef<Canvas?>(null);

        // 标题栏红蓝方：GameInfoService 在选人/对局阶段解析出本方阵营（blue/red）后经状态流推过来。
        var gameInfoService = services.Services.GetRequiredService<IGameInfoService>();
        var (mapSide, setMapSide) = UseState<string?>(gameInfoService.CurrentState.MapSide, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = gameInfoService.StateStream.Subscribe(state => setMapSide(state.MapSide));
                return () => subscription.Dispose();
            },
            0);

        UseEffect(
            () => ShellNavigation.AttachGamePhaseAndLcu(
                automationHost,
                lcu,
                services,
                setGamePhase,
                setIsClientConnected,
                () =>
                {
                    if (nav.CurrentRoute is not AppRoute.GameInfoRoute)
                    {
                        nav.Replace(new AppRoute.GameInfoRoute());
                    }
                }),
            0);

        // 主题状态：设置页改了主题色/模式/Mica 后，ThemeState 推过来触发整体重渲染。
        var (palette, setPalette) = UseState(AppHost.Theme.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = AppHost.Theme.Subscribe(setPalette);
                return () => subscription.Dispose();
            },
            0);

        // 跟随系统模式下运行中切换 Windows 深浅：把根元素实际主题写回 ThemeState 重算调色板（AGENTS.md §8）。
        var themeChangedHandler = UseRef<TypedEventHandler<FrameworkElement, object>?>(null);
        themeChangedHandler.Current ??= (sender, _) => AppHost.Theme.UpdateActualTheme(sender.ActualTheme);

        // 菜单可见性：状态必须解构成标量，避免可变类实例比较相等跳过重渲染（REACTOR_HOOKS_010）。
        var menuState = AppHost.Menu;
        var (hideTft, setHideTft) = UseState(menuState.HideTft, threadSafe: true);
        var (hideSavedPlayers, setHideSavedPlayers) = UseState(menuState.HideSavedPlayers, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = menuState.Subscribe(state =>
                {
                    setHideTft(state.HideTft);
                    setHideSavedPlayers(state.HideSavedPlayers);
                });
                return () => subscription.Dispose();
            },
            0);

        // 界面缩放状态：驱动内容视口等比缩放。
        var (scaleFactor, setScaleFactor) = UseState(AppHost.Scale.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = AppHost.Scale.Subscribe(setScaleFactor);
                return () => subscription.Dispose();
            },
            0);

        // 内容视口尺寸（DIP）：监听 row 1 视口容器的实际尺寸，驱动 Viewbox 等比缩放计算。
        var (viewportSize, setViewportSize) = UseState<(double Width, double Height)>((0, 0), threadSafe: true);
        var lastReportedSizeRef = UseRef<(double Width, double Height)>((0, 0));
        var sizeChangedHandler = UseRef<Action<object, SizeChangedEventArgs>?>(null);
        sizeChangedHandler.Current ??= (_, e) =>
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                var (lastW, lastH) = lastReportedSizeRef.Current;
                if (Math.Abs(e.NewSize.Width - lastW) >= 0.5 || Math.Abs(e.NewSize.Height - lastH) >= 0.5)
                {
                    lastReportedSizeRef.Current = (e.NewSize.Width, e.NewSize.Height);
                    setViewportSize((e.NewSize.Width, e.NewSize.Height));
                }
            }
        };

        // 侧边栏召唤师信息卡片数据源：客户端连上后拉取，未登录时后台每 2 秒探测，断线清空。
        var summonerService = services.Services.GetRequiredService<ISummonerService>();
        var (summoner, setSummoner) = UseState<SummonerProfile?>(null, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = summonerService.CurrentChanged.Subscribe(setSummoner);
                return () => subscription.Dispose();
            },
            0);

        // key 为标量整数，避免元组分配触发 REACTOR_HOOKS_004。
        var summonerPollKey = (isClientConnected ? 1 : 0) | (summoner is null ? 2 : 0);
        UseEffect(
            () => StartSummonerPolling(isClientConnected, summoner, summonerService, setSummoner, shellLogger),
            summonerPollKey);

        // 订阅语言变更以刷新侧边栏标签与标题栏，并在语言变化时重置页面栈以丢弃旧语言缓存的页面实例。
        var i18n = services.I18n;
        var (currentLang, setCurrentLang) = UseState(i18n.CurrentLanguage);
        var lastLangRef = UseRef(currentLang);
        UseEffect(
            () =>
            {
                var subscription = i18n.CurrentLanguageChanged.Subscribe(lang =>
                {
                    setCurrentLang(lang);
                    if (lastLangRef.Current != lang)
                    {
                        lastLangRef.Current = lang;
                        nav.Reset(nav.CurrentRoute);
                    }
                });
                return () => subscription.Dispose();
            },
            0);

        // 菜单项必须跨渲染保持同一批实例，避免 NavigationView 重建菜单导致选中态跳回主页。
        var menuKey = (hideTft ? 1 : 0) | (hideSavedPlayers ? 2 : 0) | (currentLang.GetHashCode() << 2);
        var menuItems = UseMemo(
            () => ShellNavigation.BuildPrimaryMenuItems(i18n, hideTft, hideSavedPlayers),
            menuKey);
        var footerItems = UseMemo(() => ShellNavigation.BuildFooterItems(i18n), currentLang.GetHashCode());

        // 导航栏开合状态与初始化守卫（AGENTS.md §5.6）。
        var (paneOpen, setPaneOpen) = UseState(services.Config.Personalization.NavigationPaneOpen);
        var paneInitialized = UseRef(false);
        var paneLoadedHandler = UseRef<RoutedEventHandler?>(null);
        paneLoadedHandler.Current ??= ShellNavigation.CreatePaneLoadedHandler(services, paneInitialized);

        UseEffect(() =>
        {
            DropDownServiceHost.Close();
            return static () => { };
        }, RouteCatalog.KeyFor(nav.CurrentRoute));

        // 已连接、开启动效且非高对比度时启用 120ms 纯淡入交叉过渡；
        // LCU 未连接时共用离线占位，禁用过渡避免闪烁。
        var pageTransition = isClientConnected && !reducedMotion && !highContrast
            ? NavigationTransition.Fade(TimeSpan.FromMilliseconds(120))
            : NavigationTransition.None;

        var pageHost = NavigationHost(nav, ShellNavigation.BuildPage) with
        {
            Transition = pageTransition,
            CacheMode = NavigationCacheMode.Enabled,
            CacheSize = RouteCatalog.All.Count,
        };

        var updateDotLayoutHandler = UseRef<EventHandler<object>?>(null);
        updateDotLayoutHandler.Current ??= (_, _) =>
            ShellUpdateBadge.PositionUpdateDot(navViewRef.Current, updateDotRef.Current, updateOverlayRef.Current, shellLogger);

        var navigationView = NavigationView(menuItems, pageHost) with
        {
            SelectedTag = RouteCatalog.KeyFor(nav.CurrentRoute),
            IsSettingsVisible = false,
            IsBackEnabled = nav.CanGoBack,
            IsBackButtonVisible = nav.CanGoBack
                ? NavigationViewBackButtonVisible.Visible
                : NavigationViewBackButtonVisible.Collapsed,
            OnBackRequested = () => nav.GoBack(),
            PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact,
            OpenPaneLength = PaneOpenWidth,
            CompactPaneLength = PaneCompactWidth,
            IsPaneOpen = paneOpen,
            IsPaneToggleButtonVisible = true,
            FooterMenuItems = footerItems,
            PaneFooter = VStack(
                Border(Empty())
                    .Height(1)
                    .Background(Theme.DividerStroke)
                    .Margin(new Thickness(0, 0, 0, 8)),
                Component<SummonerPaneCard, SummonerPaneCardProps>(
                    new SummonerPaneCardProps(
                        summoner,
                        isClientConnected,
                        paneOpen,
                        paneOpen ? PaneOpenWidth : PaneCompactWidth,
                        () => nav.Navigate(new AppRoute.CareerRoute()))))
                .HorizontalAlignment(HorizontalAlignment.Stretch),
            OnItemInvoked = tag => ShellNavigation.HandleActionTag(tag, isClientConnected, toastQueue, setShowNotice),
            OnSelectedTagChanged = tag => ShellNavigation.HandleSelectedTagChanged(tag, nav.CurrentRoute, route => nav.Navigate(route)),
        };

        // 菜单项高度对齐旧版 nav-item 的 42px（38 + 上下各 2 margin）。
        navigationView = navigationView
            .Resources(builder => builder.Set("NavigationViewItemOnLeftMinHeight", 38d))
            // 「有新版本」无障碍/悬停提示：Set 在每次更新时重放，菜单重建后也能重新挂上。
            .Set(view => ShellUpdateBadge.ApplyUpdateHint(view, updateAvailable, updateNotice.Version))
            .PaneOpenChanged(open =>
                ShellNavigation.HandlePaneOpenChanged(open, paneInitialized, setPaneOpen, services, shellLogger))
            .OnMountAdd(element =>
            {
                var view = (NavigationView)element;
                navViewRef.Current = view;
                view.Loaded += paneLoadedHandler.Current!;
                NavIconPressFeedback.Attach(view, shellLogger);
            })
            .OnUnmountAdd(element =>
            {
                var view = (NavigationView)element;
                view.Loaded -= paneLoadedHandler.Current!;
                NavIconPressFeedback.Detach(view);
                navViewRef.Current = null;
            });

        var updateOverlay = ShellUpdateBadge.BuildOverlay(
            updateAvailable,
            navViewRef,
            updateDotRef,
            updateOverlayRef,
            updateDotLayoutHandler.Current!,
            shellLogger);

        var titleBar = ShellTitleBar.Build(isClientConnected, gamePhase, mapSide, i18n);

        var contentContainer = Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                navigationView,
                updateOverlay,
                Component<ToastHost>(),
                Component<DropDownLayer>(),
                Component<AramBenchOverlayCoordinator>(),
                Component<AramDataAutoOpenCoordinator>(),
                noticeVisible
                    ? Component<NoticePopupView, NoticePopupProps>(
                        new NoticePopupProps(() => setShowNotice(false)))
                    : Empty(),
            ]);

        var contentArea = BuildContentArea(contentContainer, scaleFactor, viewportSize);

        var contentHost = Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [contentArea])
            .Grid(row: 1)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch)
            .OnSizeChanged(sizeChangedHandler.Current!);

        return Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Star()],
            [
                titleBar.Grid(row: 0),
                contentHost,
            ])
            .Backdrop(palette.Backdrop)
            .RequestedTheme(palette.ElementTheme)
            .Provide(AppHost.Context, services)
            .Provide(AppHost.ThemeContext, AppHost.Theme)
            .Provide(AppHost.ScaleContext, AppHost.Scale)
            .Provide(AppHost.MenuContext, menuState)
            .Provide(AppHost.UpdateNoticeContext, updateNotice)
            .Provide(AppHost.ToastContext, toastQueue)
            // 挂载即同步一次实际主题：首帧调色板的 IsDark 走启动时解析值兜底，
            // 若应用运行期间系统已切过深浅，这里立即校准（挂载时 ActualTheme 已可读）。
            .OnMountAdd(element =>
            {
                var fe = (FrameworkElement)element;
                fe.ActualThemeChanged += themeChangedHandler.Current!;
                AppHost.Theme.UpdateActualTheme(fe.ActualTheme);
            })
            .OnUnmountAdd(element =>
                ((FrameworkElement)element).ActualThemeChanged -= themeChangedHandler.Current!);
    }

    private static Element BuildContentArea(
        Element contentContainer,
        double scaleFactor,
        (double Width, double Height) viewportSize)
    {
        if (scaleFactor > 1.0)
        {
            var (w, h) = viewportSize;
            if (w <= 0 || h <= 0)
            {
                var (initialW, initialH) = WindowMetrics.CalculateWindowSize(AppHost.Scale.CurrentDpiScale);
                w = initialW;
                h = Math.Max(400, initialH - 32);
            }

            var scaledContent = contentContainer
                .Width(w / scaleFactor)
                .Height(h / scaleFactor);

            return Viewbox(scaledContent)
                .Stretch(Stretch.Fill)
                .HAlign(HorizontalAlignment.Stretch)
                .VAlign(VerticalAlignment.Stretch);
        }

        return contentContainer
            .Width(double.NaN)
            .Height(double.NaN)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch);
    }

    private static Action StartSummonerPolling(
        bool isClientConnected,
        SummonerProfile? summoner,
        ISummonerService summonerService,
        Action<SummonerProfile?> setSummoner,
        ILogger logger)
    {
        if (!isClientConnected)
        {
            setSummoner(null);
            return static () => { };
        }

        if (summoner is not null)
        {
            return static () => { };
        }

        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // 连上后先立即拉取一次
                try
                {
                    var profile = await summonerService
                        .GetCurrentSummonerAsync(token)
                        .ConfigureAwait(false);

                    if (!token.IsCancellationRequested && profile is not null)
                    {
                        setSummoner(profile);
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "首次读取当前召唤师失败，将在后台重试探测");
                }

                // 客户端未登录（404 等），以 2 秒间隔持续探测直至登录
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                        {
                            break;
                        }

                        var profile = await summonerService
                            .GetCurrentSummonerAsync(token)
                            .ConfigureAwait(false);

                        if (!token.IsCancellationRequested && profile is not null)
                        {
                            setSummoner(profile);
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        // 探测失败（例如尚未登录）为正常状态，静默等待下一次
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 任务已取消，正常退出
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "召唤师后台探测任务发生未预期异常");
            }
        }, token);

        return () =>
        {
            cancellation.Cancel();
            cancellation.Dispose();
        };
    }
}
