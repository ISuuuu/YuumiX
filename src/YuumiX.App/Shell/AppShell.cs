using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Navigation;
using YuumiX.App.Pages;
using YuumiX.App.Pages.Bench;
using YuumiX.App.Pages.Career;
using YuumiX.App.Pages.GameInfo;
using YuumiX.App.Pages.Home;
using YuumiX.App.Pages.Notice;
using YuumiX.App.Pages.SavedPlayers;
using YuumiX.App.Pages.Search;
using YuumiX.App.Pages.Settings;
using YuumiX.App.Pages.Tools;
using YuumiX.App.Theming;
using YuumiX.App.Windowing;
using YuumiX.Core.Automation;
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
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
    /// <summary>
    /// 侧边栏收起时的图标栏宽度（DIP）。对齐旧版 <c>.sidebar.collapsed</c> 的 64px。
    /// 召唤师卡片在收起态要按这个宽度铺满整栏并把头像居中，所以这里单独定义常量供两处引用。
    /// </summary>
    private const double PaneCompactWidth = 64;

    /// <summary>侧边栏展开时的宽度（DIP），对齐旧版 <c>.sidebar.expanded</c> 的 210px。</summary>
    private const double PaneOpenWidth = 210;

    /// <summary>
    /// 更新提示红点的直径（DIP）。
    ///
    /// 压在图标右上角的角标，尺寸只在「看得见」与「不抢戏」之间取窄带：
    /// 4 对应 16px 图标的 1/4，比系统 InfoBadge 的 dot 更小、更收敛。
    /// 调这一个常量即可整体缩放。
    /// </summary>
    private const double UpdateDotSize = 4;

    public override Element Render()
    {
        var services = AppHost.Services;
        var shellLogger = services.LoggerFactory.CreateLogger<AppShell>();

        // —— 托盘与「关闭到托盘」（对齐旧版 App.vue 的 handleClose 与 Tauri 主窗托盘）——
        // 托盘图标常驻：窗口被隐藏后它就是唯一的恢复入口，因此不能只在开关打开时才创建
        // （UseTrayIcon 只在渲染期生效，配置改动不会重渲染本组件，图标会来不及出现）。
        var window = UseWindow();
        var exitRequested = UseRef(false);
        var tray = UseTrayIcon(new TrayIconSpec(
            WindowIcon.FromPath(AppAssets.AppIconPath),
            "YuumiX",
            IsVisible: true));

        // 关闭拦截：EnableCloseToTray 为 true 时把窗口藏起来而不是结束进程，
        // 后台服务（LCU 监听 / 自动化 / 上传队列）继续运行——这就是「关闭到托盘」的产品语义。
        // 配置在每次关闭时实时读取，所以设置页改完立即生效，不依赖重渲染。
        // 依赖固定为 0：ReactorWindow 与 ReactorTrayIcon 的实例跨渲染稳定，
        // 事件只能挂一次，重复订阅会让关闭被拦截多次（AGENTS.md §5.7）。
        UseEffect(
            () =>
            {
                if (window is null)
                {
                    return static () => { };
                }

                var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                if (dispatcherQueue is not null)
                {
                    AppLifetime.RegisterExitAction(RequestExit, dispatcherQueue);
                }

                void OnClosing(object? sender, WindowClosingEventArgs e)
                {
                    // 托盘菜单的「退出」或外部退出（控制台 Ctrl+C / 进程关闭）必须放行，否则窗口永远关不掉。
                    if (exitRequested.Current ||
                        AppLifetime.IsExitRequested ||
                        !services.Config.General.EnableCloseToTray.GetValueOrDefault())
                    {
                        return;
                    }

                    // 托盘不可用（创建失败/测试环境）时宁可正常退出：
                    // 藏起来的窗口没有恢复入口，比直接退出更糟。
                    if (tray is null)
                    {
                        return;
                    }

                    e.Cancel = true;
                    window.Hide();
                }

                window.Closing += OnClosing;
                return () =>
                {
                    AppLifetime.UnregisterExitAction();
                    window.Closing -= OnClosing;
                };
            },
            0);

        // 托盘交互：左键/双击恢复窗口，右键给出「显示主窗口 / 退出」菜单（Windows 托盘惯例）。
        UseEffect(
            () =>
            {
                if (tray is null)
                {
                    return static () => { };
                }

                void Restore(object? sender, EventArgs e) => ShowMainWindow();

                // 托盘右键走系统原生弹出菜单（TrayMenu），不用 ShowFlyout：
                // 后者的宿主窗口尺寸不受控，单按钮会被居中在大片空白里；
                // MenuItems/MenuItem 产出的是 MenuFlyoutContentElement，又不能独立挂载（会抛异常）。
                void OpenMenu(object? sender, EventArgs e)
                {
                    var logger = services.LoggerFactory.CreateLogger("YuumiX.Tray");
                    try
                    {
                        var hwnd = window is null
                            ? IntPtr.Zero
                            : Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);

                        // 诊断信息走 Debug：默认 Info 级别下不产生噪音，排查时把 LogLevel 调到 0 即可。
                        logger.LogDebug("托盘右键：window={HasWindow} hwnd={Hwnd}", window is not null, hwnd);

                        if (TrayMenu.ShowExitMenu(hwnd, message => logger.LogDebug("托盘菜单: {Message}", message)))
                        {
                            RequestExit();
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "托盘右键菜单失败");
                    }
                }

                tray.Click += Restore;
                tray.DoubleClick += Restore;
                tray.RightClick += OpenMenu;
                return () =>
                {
                    tray.Click -= Restore;
                    tray.DoubleClick -= Restore;
                    tray.RightClick -= OpenMenu;
                };
            },
            0);

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
        // 因此直接持有实例——既作为 Provide 的值，也供本组件两处提示使用。
        var toastQueueRef = UseRef<ToastQueue?>(null);
        var toastQueue = toastQueueRef.Current ??= new ToastQueue();
        // 公告弹窗可见性：侧边栏「公告」动作项触发显示；关闭由弹窗自身（手动 × 或 8s 自动）回调。
        // threadSafe: true —— 自动关闭计时器在后台线程调用 OnClose（ADR-07）。
        var (noticeVisible, setShowNotice) = UseState(false, threadSafe: true);

        // —— 更新提示（侧边栏「设置」红点）——
        // 数据源是 AppHost.UpdateNotice：启动检查、周期复查、设置页手动检查三处都往它发布结果，
        // 本组件只负责订阅并绘制。threadSafe: true —— 检查在后台线程发布（ADR-07）。
        var updateNotice = AppHost.UpdateNotice;
        var (updateAvailable, setUpdateAvailable) = UseState(updateNotice.HasUpdate, threadSafe: true);
        // 已用 Toast 提示过的版本：同一次驻留内不因周期复查重复打扰，
        // 但红点会一直亮到用户装上新版本为止。
        var notifiedVersion = UseRef<string?>(null);
        UseEffect(
            () =>
            {
                // 上次更新未完成的提示：只弹一次（状态对象读后即清），与「发现新版本」提示互不影响。
                // 这条提示是失败路径上唯一的反馈——脚本执行时主进程已经退出，用户只会看到
                // 「应用关了、版本没变」，没有这条 Toast 就只能自己猜。
                if (updateNotice.FailureCode is { } failureCode)
                {
                    updateNotice.ClearFailure();
                    shellLogger.LogInformation("已提示上次更新未完成：{Code}", failureCode);
                    toastQueue.Error($"上次更新未完成：{DescribeUpdateFailure(failureCode)}，可在「设置 → 软件更新」重试");
                }

                void Apply(UpdateNoticeState state)
                {
                    setUpdateAvailable(state.HasUpdate);

                    if (state.Version is not { } version || notifiedVersion.Current == version)
                    {
                        return;
                    }

                    notifiedVersion.Current = version;
                    toastQueue.Info($"发现新版本 v{version}，可在「设置 → 通用设置 → 软件更新」中查看");
                }

                // 先按当前快照执行一次：启动检查通常早于本组件挂载，
                // 漏掉这一次的话下一次提示要等周期复查（最长 6 小时）。
                Apply(updateNotice);
                var subscription = updateNotice.Subscribe(Apply);
                return () => subscription.Dispose();
            },
            0);

        // 红点覆盖层用到的三个原生引用：菜单项容器（定位基准）、圆点本体（呼吸/位移目标）、
        // 覆盖画布（坐标换算基准）。只在挂载回调里写，渲染期只读。
        var navViewRef = UseRef<NavigationView?>(null);
        var updateDotRef = UseRef<FrameworkElement?>(null);
        var updateOverlayRef = UseRef<Canvas?>(null);

        // 标题栏红蓝方：GameInfoService 在选人/对局阶段解析出本方阵营（blue/red）后经状态流推过来。
        // threadSafe: true —— 推送来自 LCU 后台线程（ADR-07）。
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
            () =>
            {
                var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
                var lastPhase = GamePhase.None;

                void NavigateToGameInfo()
                {
                    if (nav.CurrentRoute is not AppRoute.GameInfoRoute)
                    {
                        nav.Replace(new AppRoute.GameInfoRoute());
                    }
                }

                void EnqueueNavigation()
                {
                    if (dispatcherQueue is not null)
                    {
                        dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, NavigateToGameInfo);
                    }
                    else
                    {
                        NavigateToGameInfo();
                    }
                }

                void HandlePhase(GamePhase phase)
                {
                    setGamePhase(phase);
                    bool shouldNavigate = (phase == GamePhase.ChampSelect && lastPhase != GamePhase.ChampSelect)
                        || (phase is GamePhase.InProgress or GamePhase.GameStart && lastPhase is not (GamePhase.InProgress or GamePhase.GameStart or GamePhase.ChampSelect));

                    if (shouldNavigate)
                    {
                        if (phase == GamePhase.ChampSelect && services.Config.Functions.AutoShowOpgg)
                        {
                            OpggWindowHost.ShowOrActivate();
                        }

                        EnqueueNavigation();
                    }

                    lastPhase = phase;
                }

                if (automationHost.CurrentPhase is GamePhase.InProgress or GamePhase.GameStart or GamePhase.ChampSelect)
                {
                    HandlePhase(automationHost.CurrentPhase);
                }

                var phaseSubscription = automationHost.Phases.Subscribe(HandlePhase);
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

        // 跟随系统模式下运行中切换 Windows 深浅：Theme.* 结构色自动跟随，
        // 业务色（palette.WinText 等）不会——把根元素实际主题写回 ThemeState 重算调色板（AGENTS.md §8）。
        // ActualThemeChanged 是 TypedEventHandler<FrameworkElement, object>（非通用 RoutedEventHandler）；
        // 委托必须跨渲染保持同一实例（AGENTS.md §5.7），用 UseRef 缓存。
        var themeChangedHandler = UseRef<TypedEventHandler<FrameworkElement, object>?>(null);
        themeChangedHandler.Current ??= (sender, _) => AppHost.Theme.UpdateActualTheme(sender.ActualTheme);

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

        // 界面缩放状态是本页的重渲染来源之一：设置页切「界面缩放」后，
        // ScaleState 通过 EventStream 推过来，驱动内容视口等比缩放。
        // threadSafe: true —— 推送可能来自保存完成的回调线程（ADR-07）。
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

        // 侧边栏召唤师信息卡片的数据源（对应旧版 sidebar-bottom 的 user-card）。
        // 客户端连上后拉取当前召唤师；未登录时在后台持续探测，一旦登录或状态变更立即响应；断线即清空。
        // threadSafe: true —— 拉取在后台线程完成，回调直接封送回 UI 线程（ADR-07）。
        var summonerService = services.Services.GetRequiredService<ISummonerService>();
        var (summoner, setSummoner) = UseState<SummonerProfile?>(null, threadSafe: true);

        // 订阅召唤师服务变更流（生涯页读取、对局信息读取或后台探测成功时统一通知）
        UseEffect(
            () =>
            {
                var subscription = summonerService.CurrentChanged.Subscribe(profile =>
                {
                    setSummoner(profile);
                });
                return () => subscription.Dispose();
            },
            0);

        // 连接建立后探测召唤师：已获取到时无需轮询；未登录时每 2 秒探测一次直至登录成功或断开连接。
        // key 为标量整数，避免元组分配触发 REACTOR_HOOKS_004。
        var summonerPollKey = (isClientConnected ? 1 : 0) | (summoner is null ? 2 : 0);
        UseEffect(
            () =>
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
                            services.LoggerFactory
                                .CreateLogger<AppShell>()
                                .LogWarning(ex, "首次读取当前召唤师失败，将在后台重试探测");
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
                        services.LoggerFactory
                            .CreateLogger<AppShell>()
                            .LogWarning(ex, "召唤师后台探测任务发生未预期异常");
                    }
                }, token);

                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                };
            },
            summonerPollKey);

        // 订阅语言变更以刷新侧边栏标签与标题栏，并在语言变化时重置页面栈以丢弃旧语言缓存的页面实例
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

        // 菜单项必须跨渲染保持同一批实例：每次渲染新建数组会让 NavigationView 重建菜单，
        // 选中项被重置为第一项并触发 OnSelectedTagChanged，表现为「界面重渲染后莫名跳回主页」。
        // 这是主题即时生效暴露出来的问题（ADR-12 决策三带来的额外重渲染）。
        // key 为可见性签名 + 语言签名：在「隐藏菜单」配置或当前语言实际变化时重建菜单。
        var menuKey = (menuVisibility.HideTft ? 1 : 0) | (menuVisibility.HideSavedPlayers ? 2 : 0) | (currentLang.GetHashCode() << 2);
        var menuItems = UseMemo(
            () => RouteCatalog.Primary
                .Where(item => item.Key switch
                {
                    "Tft" => !menuVisibility.HideTft,
                    "SavedPlayers" => !menuVisibility.HideSavedPlayers,
                    _ => true,
                })
                .Select(item => NavItem(GetNavLabel(i18n, item.Key, item.Label), item.Glyph, item.Key))
                .ToArray(),
            menuKey);

        // 底部附加操作区：动作项（OP.GG / 修复无限加载）在前，路由项（公告 / 设置）在后，
        // 拼出来的顺序与旧版 sidebar-bottom 一致。这些项不受「隐藏菜单」影响，依赖语言变化重建。
        var footerItems = UseMemo(() => BuildFooterItems(i18n), currentLang.GetHashCode());

        // 导航栏开合：初值取自持久化配置（跨启动恢复上次状态）。本地 UseState 保证
        // 主题/菜单变化引发重渲染时，用户当前的开合状态不会被重置回配置初值。
        var (paneOpen, setPaneOpen) = UseState(services.Config.Personalization.NavigationPaneOpen);

        // —— 初始化守卫 ——
        // NavigationView 在模板应用阶段会把自己的 IsPaneOpen 复位成默认态（LeftCompact 下为收起），
        // 渲染期赋的初值会被覆盖，于是「配置里写了收起，启动仍是展开」。
        // 更糟的是这次复位会触发 PaneOpenChanged：若照单写回配置，用户保存的偏好
        // 会在每次启动时被框架纠正后的值当场覆盖——这正是「改了没生效」的根因。
        //
        // 因此：初始化阶段的事件一律忽略（不写配置），真正的校正由 Loaded 完成——
        // 收起态在 Loaded 里同步执行（赶在首帧呈现前，避免「先开后收」闪烁），
        // 展开态则再推迟到调度器 Low 优先级（见 paneLoadedHandler）；
        // 校正完成之后的事件才视为用户操作。
        var paneInitialized = UseRef(false);

        // Loaded 处理器必须在多次渲染间保持同一实例（OnMountAdd/OnUnmountAdd 要求稳定委托），
        // 因此用 UseRef 缓存；处理器只依赖稳定对象（AppHost.Services / paneInitialized）。
        var paneLoadedHandler = UseRef<RoutedEventHandler?>(null);
        paneLoadedHandler.Current ??= (RoutedEventHandler)((sender, _) =>
        {
            if (sender is not NavigationView view)
            {
                return;
            }

            var desired = services.Config.Personalization.NavigationPaneOpen;

            if (!desired)
            {
                // 收起态：Loaded 里同步校正即可。标签渲染问题只影响「展开」方向，
                // 收起不需要等布局；同步赋值能在首帧呈现前把面板收回，
                // 否则模板应用阶段的展开默认态会先呈现一帧，形成「先开后收」的闪烁。
                if (view.IsPaneOpen)
                {
                    view.IsPaneOpen = false;
                }

                paneInitialized.Current = true;
                return;
            }

            // 展开态：菜单项此时可能尚未完成首轮布局。LeftCompact 模式下菜单项的
            // 文字标签由「面板是否已展开」驱动，若在模板应用阶段就赋值 IsPaneOpen，
            // 标签状态不会被刷新（展开后只剩图标、没有文字）。因此推到调度器队列的
            // Low 优先级：等首轮布局结束、菜单项已按收起态创建完毕后再展开，标签才正常。
            view.DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () =>
                {
                    if (!view.IsPaneOpen)
                    {
                        view.IsPaneOpen = true;
                    }

                    // 校正完成后才算初始化结束：校正本身也会触发 PaneOpenChanged，
                    // 那时若守卫已放行，就会把配置误写成控件的中间态。
                    paneInitialized.Current = true;
                });
        });

        // 页面切换不做过渡动画：NavigationHost 默认 Entrance（整页上滑 + 淡入），
        // 而 LCU 未连接时各页显示的是同一张离线占位——内容一模一样，动画就只剩
        // 「闪一下」。旧版用 v-show 平级切换同样没有动画，这里对齐旧行为。
        //
        // 页面缓存必须显式打开：NavigationCacheMode 默认 Disabled，
        // 语义是「切走即卸载、切回即重建」，组件树连同全部 UseState 一起销毁。
        // 生涯页因此每次切回都要从「summoner = null / loading = true」重新走一遍
        // 取数链路，首帧必定闪出「正在读取生涯数据…」——旧版 Vue 用 v-show 平级切换，
        // 组件实例常驻，从来没有这个空态。CacheSize 取 RouteCatalog.All 的条数，
        // 保证全站页面都能命中缓存而不是被 LRU 推出（占位页状态极轻，多留几份无成本）。
        var pageHost = NavigationHost(nav, BuildPage) with
        {
            Transition = NavigationTransition.None,
            CacheMode = NavigationCacheMode.Enabled,
            CacheSize = RouteCatalog.All.Count,
        };

        var navigationView = NavigationView(menuItems, pageHost) with
        {
            SelectedTag = RouteCatalog.KeyFor(nav.CurrentRoute),
            IsSettingsVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            // LeftCompact：收起态保留 64px 图标栏（对齐旧版 .sidebar.collapsed），
            // 不会像 Left 模式那样整条侧边栏消失、只剩标题栏上一个开关。
            PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact,
            // 展开/折叠宽度对齐旧版 sidebar：210px / 64px。
            OpenPaneLength = PaneOpenWidth,
            CompactPaneLength = PaneCompactWidth,
            IsPaneOpen = paneOpen,
            IsPaneToggleButtonVisible = true,
            // 底部附加操作区：OP.GG / 修复无限加载 / 公告 / 设置。
            FooterMenuItems = footerItems,
            // 分隔线 + 召唤师信息卡片。
            // WinUI 模板把 PaneFooter 排在 FooterMenuItems 之上，所以卡片落在底部菜单项上方，
            // 而分隔线正好是底部区的顶边——与旧版 sidebar-bottom 的 border-top 同位。
            // VStack 必须显式 Stretch：否则它会按内容宽度收紧、整体贴左（分隔线与卡片都缩在左侧）。
            PaneFooter = VStack(
                Border(Empty())
                    .Height(1)
                    .Background(Theme.DividerStroke)
                    .Margin(new Microsoft.UI.Xaml.Thickness(0, 0, 0, 8)),
                Component<SummonerPaneCard, SummonerPaneCardProps>(
                    new SummonerPaneCardProps(
                        summoner,
                        isClientConnected,
                        paneOpen,
                        paneOpen ? PaneOpenWidth : PaneCompactWidth,
                        () => nav.Replace(new AppRoute.CareerRoute()))))
                .HorizontalAlignment(HorizontalAlignment.Stretch),
        };

        // 菜单项高度对齐旧版 nav-item 的 42px：WinUI 左栏项默认 MinHeight 36，
        // 加上上下各 2 的 margin 共 40；把 MinHeight 提到 38 正好凑满 42。
        // 圆角与项间距沿用 WinUI 原生值，不额外覆盖。
        navigationView = navigationView.Resources(builder =>
        {
            builder.Set("NavigationViewItemOnLeftMinHeight", 38d);
        });

        // 「有新版本」红点。用 .Set(...) 而非 OnMountAdd：
        // Set 在每次更新时都会在 descriptor 属性之后重新执行（框架文档明确的契约），
        // 因此即使 Reactor 重建了菜单项实例、或红点随菜单可见性变化被丢弃，下一次渲染也会重新挂上。
        navigationView = navigationView.Set(view => ApplyUpdateHint(view, updateAvailable, updateNotice.Version));

        // —— 更新红点覆盖层 ——
        // 为什么自绘而不用 NavigationViewItem.InfoBadge：dot 形态的直径（约 6px）和位置
        // （展开态固定贴在最右端、收起态压在图标右上角外）都由 NavigationView 的模板写死。
        // 这两个恰恰是红点可读性的来源——太小看不见、太远不像"设置这一项的通知"。
        // 自绘 4px 圆点 + 按菜单项实测坐标定位，才能同时满足"够醒目"和"贴住图标"。
        //
        // 定位走命令式（Canvas.SetLeft/Top）而不是 state：位置依赖每帧布局，
        // 若塞进 UseState，导航栏展开动画期间会持续触发整个外壳重渲染。
        var updateDotLayoutHandler = UseRef<EventHandler<object>?>(null);
        updateDotLayoutHandler.Current ??= (_, _) => PositionUpdateDot();

        void PositionUpdateDot()
        {
            if (updateDotRef.Current is not { } dot
                || updateOverlayRef.Current is not { } canvas
                || navViewRef.Current is not { } view)
            {
                return;
            }

            var item = FindSettingsMenuItem(view);
            if (item is null)
            {
                return;
            }

            double left;
            double top;

            // 优先直接取图标本体的实测角点：
            // 收起/展开两种状态的内边距完全不同，用固定数字估会在某一种状态下偏到图标上方。
            if (FindIconHost(item) is { ActualWidth: > 0 } icon)
            {
                Point corner;
                try
                {
                    corner = icon.TransformToVisual(canvas).TransformPoint(new Point(icon.ActualWidth, 0));
                }
                catch (ArgumentException)
                {
                    return;
                }

                // 圆点中心对准图标右上角顶点：一半压在角上、一半探出图标外，
                // 这是系统角标的常见位置，也避免整颗点漂在图标上方。
                left = corner.X - UpdateDotSize / 2;
                top = corner.Y - UpdateDotSize / 2;
            }
            else
            {
                Point origin;
                try
                {
                    origin = item.TransformToVisual(canvas).TransformPoint(new Point(0, 0));
                }
                catch (ArgumentException)
                {
                    // 两者尚未处于同一棵视觉树（模板重建的瞬间）：等下一次布局回调即可，不必报错。
                    return;
                }

                // 模板结构变化导致找不到图标时的兜底：按已知的内边距估算
                //（收起态图标在 64px 栏内居中，展开态起于 16px 内边距）。
                double iconRight = view.IsPaneOpen ? 32 : 40;
                left = origin.X + iconRight - UpdateDotSize;
                top = origin.Y + 8;
            }

            if (Math.Abs(Microsoft.UI.Xaml.Controls.Canvas.GetLeft(dot) - left) < 0.5
                && Math.Abs(Microsoft.UI.Xaml.Controls.Canvas.GetTop(dot) - top) < 0.5)
            {
                return;
            }

            // 走 Debug：只在坐标真的变化时输出，排查"点跑到别处"时把日志级别调到 0 即可。
            shellLogger.LogDebug(
                "更新红点定位：left={Left} top={Top} paneOpen={Open} canvas={CW}x{CH}",
                left, top, view.IsPaneOpen, canvas.ActualWidth, canvas.ActualHeight);

            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(dot, left);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(dot, top);
        }

        Element BuildUpdateDot() =>
            Border(Empty())
                .Width(UpdateDotSize)
                .Height(UpdateDotSize)
                .CornerRadius(YuumiTheme.PillRadius)
                .Background(Theme.SystemCritical)
                .IsHitTestVisible(false)
                // 元素常驻、只切可见性：条件渲染（有更新 ? 红点 : Empty）会在两种元素类型之间切换，
                // 每次都让 Reactor 重建 Canvas 并重挂 LayoutUpdated；隐藏一个已有元素没有这个代价。
                // 呼吸动画在不可见期间继续跑，合成层开销可忽略，换来的是「显示时立刻处于当前相位」。
                .IsVisible(updateAvailable)
                .OnMountAdd(element =>
                {
                    updateDotRef.Current = (FrameworkElement)element;
                    StartBreathing((FrameworkElement)element, shellLogger);
                    PositionUpdateDot();
                })
                .OnUnmountAdd(_ => updateDotRef.Current = null);

        var updateOverlay = Canvas([BuildUpdateDot()])
            .IsHitTestVisible(false)
            .OnMountAdd(element =>
            {
                updateOverlayRef.Current = (Canvas)element;
                // LayoutUpdated 覆盖所有会移动「设置」项的情况：导航栏展开/收起、窗口缩放、
                // 菜单项因可见性配置重建。回调内部先比较坐标，因此不会因自身的 SetLeft 反复触发。
                element.LayoutUpdated += updateDotLayoutHandler.Current!;
                PositionUpdateDot();
            })
            .OnUnmountAdd(element =>
            {
                element.LayoutUpdated -= updateDotLayoutHandler.Current!;
                updateOverlayRef.Current = null;
            });

        // 侧边栏是平级切换语义，因此用 Replace 而非 Navigate，避免堆积返回栈。
        navigationView = navigationView with
        {
            OnSelectedTagChanged = tag =>
            {
                if (tag == SidebarTags.Opgg)
                {
                    if (!isClientConnected)
                    {
                        toastQueue.Error("请先连接英雄联盟客户端");
                    }
                    else
                    {
                        OpggWindowHost.ShowOrActivate();
                    }
                    return;
                }

                if (tag == SidebarTags.Reconnect)
                {
                    // 旧版的「修复无限加载」是手动重连动作；新项目目前只有自动重连代理，
                    // 入口先预留在这里，点击给出明确反馈而不是静默无响应。
                    toastQueue.Info("「修复无限加载」待迁移：当前仅支持自动重连");
                    return;
                }

                if (tag == SidebarTags.Notice)
                {
                    // 旧版行为（App.vue navigate('notice')）：不切换页面，只弹出右下角公告弹窗。
                    // threadSafe：自动关闭计时器在后台线程触发 OnClose（ADR-07）。
                    setShowNotice(true);
                    return;
                }

                // 仅在切换到不同模块时导航；已在目标模块（如带参 CareerRoute/SearchRoute）不覆盖现有路由。
                if (tag is not null && RouteCatalog.KeyFor(nav.CurrentRoute) != tag && RouteCatalog.Resolve(tag) is { } route)
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
            // 初始化阶段（NavigationView 模板应用时复位自身状态）触发的事件不是用户操作：
            // 直接忽略，不写配置。否则用户保存的偏好会在每次启动时被框架的中间态覆盖。
            //
            // 这里刻意不把控件拉回配置值：模板应用阶段赋值 IsPaneOpen 时机过早，
            // 菜单项尚未按展开态渲染，文字标签会缺失（展开后只剩图标）。
            // 真正的校正由 Loaded 后的调度器延迟完成（见 paneLoadedHandler）。
            if (!paneInitialized.Current)
            {
                return;
            }

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

        // 挂载时挂上 Loaded 钩子：Loaded 是「框架复位已发生」的确定时刻，
        // 在那里做最终校正（校正被推到调度器队列，等首轮布局结束再执行）。
        navigationView = navigationView
            .OnMountAdd(element =>
            {
                navViewRef.Current = (NavigationView)element;
                ((NavigationView)element).Loaded += paneLoadedHandler.Current!;
            })
            .OnUnmountAdd(element =>
            {
                ((NavigationView)element).Loaded -= paneLoadedHandler.Current!;
                navViewRef.Current = null;
            });

        var titleBar = TitleBar("YuumiX") with
        {
            // 标题栏图标与原 CustomTitleBar.vue 的 titlebar-logo 对齐：同一个 Logo 文件。
            // 用 ImageIcon 而非 BitmapIcon —— 后者默认单色化，会把彩色 Logo 染成主题色。
            Icon = ImageIcon(new Uri(AppAssets.LogoUri)),
            // 对应原 CustomTitleBar.vue 的 titlebar-phase：已连接且非空闲时展示阶段中文名；
            // 活跃对局三阶段已知阵营时追加红蓝方（旧版 mapSideLabel）。
            Subtitle = GetTitleBarPhaseSubtitle(isClientConnected, gamePhase, mapSide, i18n),
            IsBackButtonVisible = false,
        };

        // 标题栏高度：旧版是自绘 38（Tauri decorations:false），本版走 WinUI 系统 TitleBar，
        // 高度由 AppWindowTitleBar 的 Standard 档锁定 32。
        // 实测两种改法都无效且不可取：
        //   1) `.Height(38)` —— 不落到控件（properties 查得 Height=NaN、ActualHeight=32），
        //      TitleBar 的模板把高度钉在系统 caption 上；
        //   2) `PreferredHeightOption = Custom` —— 需同时接管非客户区绘制与系统按钮定位，
        //      超出「尺寸对齐」范围，且窗口按钮会与标题栏内容错位。
        // 因此保留 32，差额（6 DIP）体现在内容区高度：旧版 872 vs 本版 877。

        var contentContainer = Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                navigationView,
                // 更新红点：与导航栏同格、后绘制压在其上；IsHitTestVisible=false 让点击穿透到
                // 「设置」菜单项本身——点红点等于点设置，不需要额外的事件处理。
                updateOverlay,
                // 全局 Toast 宿主（R5）：压在页面内容之上，队列由 ToastContext 提供。
                Component<ToastHost>(),
                // 大乱斗板凳席悬浮窗的显隐协调者：不产生视觉输出，只把 LCU 状态翻译成窗口开关。
                Component<AramBenchOverlayCoordinator>(),
                // 公告弹窗：右下角覆盖层（对应旧版 NoticePopup 的 position: fixed）。
                // 挂在最外层 Grid 上，压在页面内容之上、与 Toast 同层。
                noticeVisible
                    ? Component<NoticePopupView, NoticePopupProps>(
                        new NoticePopupProps(() => setShowNotice(false)))
                    : Empty(),
            ]
        );

        Element contentArea;
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

            contentArea = Viewbox(scaledContent)
                .Stretch(Stretch.Fill)
                .HAlign(HorizontalAlignment.Stretch)
                .VAlign(VerticalAlignment.Stretch);
        }
        else
        {
            contentArea = contentContainer
                .Width(double.NaN)
                .Height(double.NaN)
                .HAlign(HorizontalAlignment.Stretch)
                .VAlign(VerticalAlignment.Stretch);
        }

        var contentHost = Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [contentArea])
            .Grid(row: 1)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch)
            .OnSizeChanged(sizeChangedHandler.Current!);

        var root = Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Star()],
            [
                titleBar.Grid(row: 0),
                contentHost,
            ]
        )
        .Backdrop(palette.Backdrop)
        .RequestedTheme(palette.ElementTheme)
        .Provide(AppHost.Context, services)
        .Provide(AppHost.ThemeContext, AppHost.Theme)
        .Provide(AppHost.ScaleContext, AppHost.Scale)
        .Provide(AppHost.UpdateNoticeContext, updateNotice)
        .Provide(AppHost.ToastContext, toastQueue);

        // 挂载即同步一次实际主题：首帧调色板的 IsDark 走启动时解析值兜底，
        // 若应用运行期间系统已切过深浅，这里立即校准（挂载时 ActualTheme 已可读）。
        // 「固定主题」模式由 UpdateActualTheme 内部守卫忽略，不会污染调色板。
        root = root
            .OnMountAdd(element =>
            {
                var fe = (FrameworkElement)element;
                fe.ActualThemeChanged += themeChangedHandler.Current!;
                AppHost.Theme.UpdateActualTheme(fe.ActualTheme);
            })
            .OnUnmountAdd(element =>
                ((FrameworkElement)element).ActualThemeChanged -= themeChangedHandler.Current!);

        // 托盘菜单动作。定义在 Render 内以捕获当次渲染的 window 实例；
        // 传给 UseEffect 的委托本身只挂一次，捕获的 window 实例跨渲染稳定。
        void ShowMainWindow()
        {
            if (window is null)
            {
                return;
            }

            // Hide 会把窗口连同任务栏按钮一起移除；Show 之后不会自动获得焦点，必须显式 Activate。
            window.Show();
            window.Activate();
        }

        void RequestExit()
        {
            // 先置位再关闭：Closing 里靠它放行，避免"退出"被自己的守卫取消。
            exitRequested.Current = true;
            AppLifetime.IsExitRequested = true;

            // 窗口已被隐藏（关闭到托盘）时不再"复活"它：直接请求进程退出，避免闪一帧窗口。
            if (window is null || !window.IsVisible)
            {
                ReactorApp.Exit();
                return;
            }

            window.Close();
        }

        return root;
    }

    /// <summary>
    /// 计算标题栏副标题（游戏状态）。已连接且非空闲（None）阶段时返回阶段中文名，
    /// 否则返回空串隐藏（对齐原 CustomTitleBar.vue 的 <c>isConnected &amp;&amp; gamePhase !== 'None'</c> 规则）。
    ///
    /// 红蓝方（旧版 mapSideLabel）：仅选人/游戏加载/游戏中三阶段且阵营已知时拼接；
    /// 其余阶段（Lobby/EndOfGame/Reconnect 等）只显示阶段名，与旧版 else 分支清除阵营一致。
    /// </summary>
    private static string GetTitleBarPhaseSubtitle(bool isClientConnected, GamePhase gamePhase, string? mapSide, YuumiX.Core.Localization.I18nService? i18n = null)
    {
        if (!isClientConnected || gamePhase is GamePhase.None or GamePhase.Unknown)
        {
            return string.Empty;
        }

        var label = i18n?.T("phase." + gamePhase, null) ?? (gamePhase switch
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
        });

        if (mapSide is "blue" or "red" &&
            gamePhase is GamePhase.ChampSelect or GamePhase.GameStart or GamePhase.InProgress)
        {
            var blueSide = i18n?.T("titlebar.blueSide", "蓝色方") ?? "蓝色方";
            var redSide = i18n?.T("titlebar.redSide", "红色方") ?? "红色方";
            label += mapSide == "blue" ? $" - {blueSide}" : $" - {redSide}";
        }

        return label;
    }

    /// <summary>
    /// 把更新失败原因码翻成人话。
    ///
    /// 原因码由延迟脚本写入（英文，避免 cmd 代码页问题），中文只在 UI 层做，
    /// 这样脚本改动不会牵动文案，文案调整也不用动脚本。
    /// </summary>
    private static string DescribeUpdateFailure(string code) => code switch
    {
        YuumiX.Core.Updates.UpdateService.FailureCodeWaitExit => "应用未能在超时前退出，替换已放弃",
        YuumiX.Core.Updates.UpdateService.FailureCodeCopyFailed => "文件替换中断，程序可能不完整",
        YuumiX.Core.Updates.UpdateService.FailureCodeSetupFailed => "安装程序返回失败",
        _ => "原因未知",
    };

    /// <summary>
    /// 找「设置」菜单项里的图标本体（或其 ContentPresenter 宿主）。
    ///
    /// 用它而不是按内边距估算：图标在收起态（栏内居中）与展开态（16px 内边距）的位置完全不同，
    /// 任何写死的偏移都只会在其中一种状态下正确——实测展开态会偏到图标上方。
    /// 取实测角点则可以两种状态通用，也顺带适应 DPI 缩放与模板尺寸调整。
    ///
    /// 三条识别路径按精确度排序：<c>IconElement</c> 本体 → 其 <c>ContentPresenter</c> 宿主
    /// （模板里叫 <c>IconPresenter</c>，但名字并非公开契约，因此再兜一层名字匹配）。
    /// </summary>
    private static FrameworkElement? FindIconHost(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.IconElement icon)
            {
                return icon;
            }

            if (child is ContentPresenter { Content: Microsoft.UI.Xaml.Controls.IconElement } host)
            {
                return host;
            }

            if (child is FrameworkElement { Name: "IconPresenter" } named)
            {
                return named;
            }

            if (FindIconHost(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// 给「设置」菜单项补上无障碍提示与悬停说明。
    ///
    /// 红点是纯视觉元素（<c>IsHitTestVisible=false</c>，不在无障碍树里），
    /// 屏幕阅读器与悬停提示都读不到它，所以「有新版本 vX」必须挂到菜单项本身。
    /// </summary>
    private static void ApplyUpdateHint(NavigationView view, bool available, string? version)
    {
        var item = FindSettingsMenuItem(view);
        if (item is null)
        {
            return;
        }

        string helpText = !available
            ? string.Empty
            : string.IsNullOrEmpty(version) ? "有新版本可用" : $"有新版本 v{version} 可用";

        AutomationProperties.SetHelpText(item, helpText);
        ToolTipService.SetToolTip(item, helpText.Length == 0 ? null : helpText);
    }

    /// <summary>
    /// 找到底部操作区「设置」项对应的原生容器。
    ///
    /// 优先按 Tag 匹配：Reactor 把 <c>NavigationViewItemData.Tag</c> 映射到原生
    /// <c>NavigationViewItem.Tag</c>，而 <see cref="RouteCatalog.Footer"/> 的 Key 是稳定字符串。
    /// Tag 未透传时（框架映射方式变化）再按文案兜底——文案取自路由目录，不在这里写死，
    /// 菜单改名时不会静默失配。
    /// </summary>
    private static NavigationViewItem? FindSettingsMenuItem(NavigationView view)
    {
        var descriptor = RouteCatalog.Footer[0];

        foreach (var entry in view.FooterMenuItems)
        {
            if (entry is NavigationViewItem item
                && string.Equals(item.Tag as string, descriptor.Key, StringComparison.Ordinal))
            {
                return item;
            }
        }

        foreach (var entry in view.FooterMenuItems)
        {
            if (entry is NavigationViewItem item
                && string.Equals(item.Content as string, descriptor.Label, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>
    /// 启动红点呼吸动画：1.0 → 0.35 → 1.0，2 秒一轮，无限循环。
    ///
    /// 两端与中点都用同一条缓入缓出曲线，亮度变化比线性更「像呼吸」；
    /// 时长定 2 秒是因为更短的周期在余光里会显得焦躁（接近闪烁告警），更长则容易被忽略。
    ///
    /// 系统开启「减少动画」（设置 → 辅助功能 → 视觉效果）时常亮不呼吸：
    /// 持续明暗变化对前庭敏感用户不友好，而红点本身的可见性并不依赖动画。
    /// </summary>
    private static void StartBreathing(FrameworkElement target, ILogger logger)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(target);

            if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                visual.Opacity = 1f;
                return;
            }

            var compositor = visual.Compositor;
            var easing = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.4f, 0f),
                new System.Numerics.Vector2(0.6f, 1f));

            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0f, 1f, easing);
            animation.InsertKeyFrame(0.5f, 0.35f, easing);
            animation.InsertKeyFrame(1f, 1f, easing);
            animation.Duration = TimeSpan.FromSeconds(2);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;

            visual.StartAnimation("Opacity", animation);
        }
        catch (Exception ex)
        {
            // 动画只是锦上添花：合成器/无障碍查询在极端环境下可能不可用，
            // 此时红点保持常亮即可——不能因为「灯不呼吸」把整个外壳渲染打断。
            logger.LogWarning(ex, "更新红点的呼吸动画启动失败，已降级为常亮");
            target.Opacity = 1;
        }
    }

    private static string GetNavLabel(YuumiX.Core.Localization.I18nService i18n, string key, string fallback) => key switch
    {
        "Home" => i18n.T("nav.home", fallback),
        "Career" => i18n.T("nav.career", fallback),
        "Search" => i18n.T("nav.search", fallback),
        "GameInfo" => i18n.T("nav.gameinfo", fallback),
        "Tft" => i18n.T("nav.tft", fallback),
        "SavedPlayers" => i18n.T("nav.savedPlayers", fallback),
        "Tools" => i18n.T("nav.tools", fallback),
        "Settings" => i18n.T("nav.settings", fallback),
        SidebarTags.Opgg => i18n.T("nav.opgg", fallback),
        SidebarTags.Reconnect => i18n.T("nav.reconnect", fallback),
        SidebarTags.Notice => i18n.T("nav.notice", fallback),
        _ => fallback,
    };

    /// <summary>
    /// 构造底部附加操作区的菜单项。
    ///
    /// 动作项（OP.GG / 修复无限加载 / 公告）在前、路由项（设置）在后，
    /// 拼出来的顺序即旧版 sidebar-bottom 的排列顺序。
    ///
    /// OP.GG 用旧版同款品牌图标（18px PNG）覆盖 Segoe 字形；公告与其余菜单项
    /// 一致走 Segoe 字形（<see cref="Icons.Notice"/>，RouteCatalog.FooterActions 已配好），
    /// 不再保留旧版 Feather 铃铛的填充几何特殊渲染。
    /// </summary>
    private static NavigationViewItemData[] BuildFooterItems(YuumiX.Core.Localization.I18nService i18n)
    {
        var items = new List<NavigationViewItemData>();

        foreach (var action in RouteCatalog.FooterActions)
        {
            var label = GetNavLabel(i18n, action.Tag, action.Label);
            var item = NavItem(label, action.Glyph, action.Tag);

            if (action.Tag == SidebarTags.Opgg && !string.IsNullOrEmpty(AppAssets.OpggNavIconUri))
            {
                // 旧版用的是彩色品牌 SVG 而非 Segoe 字形；IconElement 优先于 Icon 字形渲染。
                item = item with { IconElement = ImageIcon(new Uri(AppAssets.OpggNavIconUri)) };
            }

            items.Add(item);
        }

        foreach (var route in RouteCatalog.Footer)
        {
            var label = GetNavLabel(i18n, route.Key, route.Label);
            items.Add(NavItem(label, route.Glyph, route.Key));
        }

        return items.ToArray();
    }

    /// <summary>
    /// 路由 → 页面组件。已迁移的模块在这里替换占位组件；其余仍走 <see cref="PlaceholderPage"/>，
    /// 待迁移清单以 <see cref="PageCatalog"/> 为准。
    /// </summary>
    private static Element BuildPage(AppRoute route) => route switch
    {
        AppRoute.HomeRoute => Component<HomePage>(),
        // CareerRoute / SearchRoute 的参数经 props 下发（R8：参数跟着路由走）。
        AppRoute.CareerRoute career => Component<CareerPage, CareerPageProps>(
            new CareerPageProps(career.Puuid, career.Preload)),
        AppRoute.GameInfoRoute => Component<GameInfoPage>(),
        AppRoute.SavedPlayersRoute => Component<SavedPlayersPage>(),
        AppRoute.SearchRoute search => Component<SearchPage, SearchPageProps>(
            new SearchPageProps(search.Query, search.Puuid, search.GameId)),
        AppRoute.SettingsRoute => Component<SettingsPage>(),
        AppRoute.ToolsRoute => Component<ToolsPage>(),
        AppRoute.NoticeRoute => Component<NoticePage>(),
        _ => BuildPlaceholder(route),
    };

    private static Element BuildPlaceholder(AppRoute route)
    {
        var (title, sourceView, pending) = PageCatalog.For(RouteCatalog.KeyFor(route));

        // 主页展示品牌 Logo（对应原 Home.vue 顶部 96px 的 Logo）；其余占位页不显示图片。
        var brandLogoUri = route is AppRoute.HomeRoute ? AppAssets.LogoUri : null;

        return Component<PlaceholderPage, PlaceholderProps>(
            new PlaceholderProps(title, sourceView, pending, brandLogoUri));
    }
}
