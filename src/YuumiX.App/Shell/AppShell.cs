using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Navigation;
using YuumiX.App.Pages;
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

    public override Element Render()
    {
        var services = AppHost.Services;
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
                _ = Task.Run(async () =>
                {
                    // 连上后先立即拉取一次
                    try
                    {
                        var profile = await summonerService
                            .GetCurrentSummonerAsync(cancellation.Token)
                            .ConfigureAwait(false);

                        if (!cancellation.IsCancellationRequested && profile is not null)
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
                    while (!cancellation.IsCancellationRequested)
                    {
                        try
                        {
                            if (!await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
                            {
                                break;
                            }

                            var profile = await summonerService
                                .GetCurrentSummonerAsync(cancellation.Token)
                                .ConfigureAwait(false);

                            if (!cancellation.IsCancellationRequested && profile is not null)
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
                }, cancellation.Token);

                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                };
            },
            summonerPollKey);

        // 菜单项必须跨渲染保持同一批实例：每次渲染新建数组会让 NavigationView 重建菜单，
        // 选中项被重置为第一项并触发 OnSelectedTagChanged，表现为「界面重渲染后莫名跳回主页」。
        // 这是主题即时生效暴露出来的问题（ADR-12 决策三带来的额外重渲染）。
        // key 为可见性签名：只在「隐藏菜单」配置实际变化时重建菜单，
        // 平时（主题变化/普通重渲染）保持原实例数组。
        var menuKey = (menuVisibility.HideTft ? 1 : 0) | (menuVisibility.HideSavedPlayers ? 2 : 0);
        var menuItems = UseMemo(
            () => RouteCatalog.Primary
                .Where(item => item.Key switch
                {
                    "Tft" => !menuVisibility.HideTft,
                    "SavedPlayers" => !menuVisibility.HideSavedPlayers,
                    _ => true,
                })
                .Select(item => NavItem(item.Label, item.Glyph, item.Key))
                .ToArray(),
            menuKey);

        // 底部附加操作区：动作项（OP.GG / 修复无限加载）在前，路由项（公告 / 设置）在后，
        // 拼出来的顺序与旧版 sidebar-bottom 一致。这些项不受「隐藏菜单」影响，key 固定。
        var footerItems = UseMemo(BuildFooterItems, 0);

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

        var navigationView = NavigationView(menuItems, NavigationHost(nav, BuildPage)) with
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

                if (tag is not null && RouteCatalog.Resolve(tag) is { } route && route != nav.CurrentRoute)
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
            .OnMountAdd(element => ((NavigationView)element).Loaded += paneLoadedHandler.Current!)
            .OnUnmountAdd(element => ((NavigationView)element).Loaded -= paneLoadedHandler.Current!);

        var titleBar = TitleBar("YuumiX") with
        {
            // 标题栏图标与原 CustomTitleBar.vue 的 titlebar-logo 对齐：同一个 Logo 文件。
            // 用 ImageIcon 而非 BitmapIcon —— 后者默认单色化，会把彩色 Logo 染成主题色。
            Icon = ImageIcon(new Uri(AppAssets.LogoUri)),
            // 对应原 CustomTitleBar.vue 的 titlebar-phase：已连接且非空闲时展示阶段中文名；
            // 活跃对局三阶段已知阵营时追加红蓝方（旧版 mapSideLabel）。
            Subtitle = GetTitleBarPhaseSubtitle(isClientConnected, gamePhase, mapSide),
            IsBackButtonVisible = false,
        };

        var root = Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Star()],
            [
                titleBar.Grid(row: 0),
                navigationView.Grid(row: 1),
                // 全局 Toast 宿主（R5）：压在页面内容之上，队列由 ToastContext 提供。
                Component<ToastHost>().Grid(row: 1),
                // 公告弹窗：右下角覆盖层（对应旧版 NoticePopup 的 position: fixed）。
                // 挂在最外层 Grid 上，压在页面内容之上、与 Toast 同层。
                noticeVisible
                    ? Component<NoticePopupView, NoticePopupProps>(
                        new NoticePopupProps(() => setShowNotice(false))).Grid(row: 1)
                    : Empty(),
            ]
        )
        .Backdrop(palette.Backdrop)
        .RequestedTheme(palette.ElementTheme)
        .Provide(AppHost.Context, services)
        .Provide(AppHost.ThemeContext, AppHost.Theme)
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

        return root;
    }

    /// <summary>
    /// 计算标题栏副标题（游戏状态）。已连接且非空闲（None）阶段时返回阶段中文名，
    /// 否则返回空串隐藏（对齐原 CustomTitleBar.vue 的 <c>isConnected &amp;&amp; gamePhase !== 'None'</c> 规则）。
    ///
    /// 红蓝方（旧版 mapSideLabel）：仅选人/游戏加载/游戏中三阶段且阵营已知时拼接；
    /// 其余阶段（Lobby/EndOfGame/Reconnect 等）只显示阶段名，与旧版 else 分支清除阵营一致。
    /// </summary>
    private static string GetTitleBarPhaseSubtitle(bool isClientConnected, GamePhase gamePhase, string? mapSide)
    {
        if (!isClientConnected || gamePhase is GamePhase.None or GamePhase.Unknown)
        {
            return string.Empty;
        }

        var label = gamePhase switch
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

        if (mapSide is "blue" or "red" &&
            gamePhase is GamePhase.ChampSelect or GamePhase.GameStart or GamePhase.InProgress)
        {
            label += mapSide == "blue" ? " - 蓝色方" : " - 红色方";
        }

        return label;
    }

    /// <summary>
    /// 公告菜单项的铃铛图标（填充几何，18×18 坐标系，由脚本从旧版 Feather bell 光栅轮廓生成）。
    ///
    /// 旧版 NavigationSidebar.vue 的公告项用的是 Feather 细线铃铛（stroke 2px、currentColor
    /// 随文字色），与 Segoe 的实心 Ringer 字形观感差异明显。Reactor 的图标通道只接受
    /// 填充几何或位图，不支持描边，因此这里用脚本把旧版描边路径转成**空心填充轮廓**
    /// （中心线向两侧偏移 1px 取环，EvenOdd 挖空钟体内部），形状与旧版渲染一致。
    ///
    /// 走 <see cref="PathIcon(string)"/>：PathIcon 的 Fill 继承所在菜单项的前景色，
    /// 主题切换与悬停高亮自动跟随——这一点是固定位图 PNG 做不到的。
    /// </summary>
    private const string NoticeBellPath =
        "M1.77 12.18 L1.71 12.23 L1.66 12.28 L1.62 12.34 L1.58 12.4 L1.55 12.47 L1.53 12.54 L1.51 12.61 L1.5 12.68 L1.5 12.76 L1.5 12.83 L1.52 12.9 L1.53 12.97 L1.56 13.04 L1.59 13.11 L1.63 13.17 L1.67 13.23 L1.72 13.28 L1.78 13.33 L1.84 13.38 L1.9 13.41 L1.96 13.44 L2.03 13.47 L2.1 13.49 L2.18 13.5 L2.25 13.5 L15.75 13.5 L15.82 13.5 L15.9 13.49 L15.97 13.47 L16.04 13.44 L16.1 13.41 L16.16 13.38 L16.22 13.33 L16.28 13.28 L16.33 13.23 L16.37 13.17 L16.41 13.11 L16.44 13.04 L16.47 12.97 L16.48 12.9 L16.5 12.83 L16.5 12.76 L16.5 12.68 L16.49 12.61 L16.47 12.54 L16.45 12.47 L16.42 12.4 L16.38 12.34 L16.34 12.28 L16.29 12.23 L16.23 12.18 L16.18 12.14 L16.18 12.14 L16.17 12.13 L16.16 12.12 L16.13 12.1 L16.1 12.07 L16.06 12.03 L16.0 11.98 L15.94 11.92 L15.88 11.85 L15.8 11.76 L15.73 11.66 L15.64 11.56 L15.56 11.43 L15.47 11.3 L15.38 11.15 L15.28 10.98 L15.19 10.81 L15.1 10.61 L15.0 10.4 L14.91 10.17 L14.82 9.93 L14.74 9.67 L14.66 9.39 L14.58 9.09 L14.51 8.78 L14.45 8.44 L14.39 8.09 L14.34 7.71 L14.3 7.32 L14.27 6.9 L14.26 6.46 L14.25 5.99 L14.25 5.98 L14.24 5.68 L14.24 5.63 L14.21 5.34 L14.2 5.29 L14.15 5.0 L14.14 4.95 L14.08 4.67 L14.06 4.62 L13.98 4.34 L13.96 4.29 L13.86 4.01 L13.84 3.97 L13.72 3.7 L13.7 3.66 L13.56 3.4 L13.53 3.35 L13.38 3.1 L13.35 3.06 L13.18 2.82 L13.15 2.78 L12.96 2.56 L12.93 2.52 L12.73 2.31 L12.69 2.27 L12.48 2.07 L12.44 2.04 L12.22 1.85 L12.18 1.82 L11.94 1.65 L11.9 1.62 L11.65 1.47 L11.6 1.44 L11.34 1.3 L11.3 1.28 L11.03 1.16 L10.99 1.14 L10.71 1.04 L10.66 1.02 L10.38 0.94 L10.33 0.92 L10.05 0.86 L10.0 0.85 L9.71 0.8 L9.66 0.79 L9.37 0.76 L9.32 0.76 L9.02 0.75 L8.98 0.75 L8.68 0.76 L8.63 0.76 L8.34 0.79 L8.29 0.8 L8.0 0.85 L7.95 0.86 L7.67 0.92 L7.62 0.94 L7.34 1.02 L7.29 1.04 L7.01 1.14 L6.97 1.16 L6.7 1.28 L6.66 1.3 L6.4 1.44 L6.35 1.47 L6.1 1.62 L6.06 1.65 L5.82 1.82 L5.78 1.85 L5.56 2.04 L5.52 2.07 L5.31 2.27 L5.27 2.31 L5.07 2.52 L5.04 2.56 L4.85 2.78 L4.82 2.82 L4.65 3.06 L4.62 3.1 L4.47 3.35 L4.44 3.4 L4.3 3.66 L4.28 3.7 L4.16 3.97 L4.14 4.01 L4.04 4.29 L4.02 4.34 L3.94 4.62 L3.92 4.67 L3.86 4.95 L3.85 5.0 L3.8 5.29 L3.79 5.34 L3.76 5.63 L3.76 5.68 L3.75 5.98 L3.75 5.99 L3.74 6.46 L3.73 6.9 L3.7 7.32 L3.66 7.71 L3.61 8.09 L3.55 8.44 L3.49 8.78 L3.42 9.09 L3.34 9.39 L3.26 9.67 L3.18 9.93 L3.09 10.17 L3.0 10.4 L2.9 10.61 L2.81 10.81 L2.72 10.98 L2.62 11.15 L2.53 11.3 L2.44 11.43 L2.36 11.56 L2.27 11.66 L2.2 11.76 L2.12 11.85 L2.06 11.92 L2.0 11.98 L1.94 12.03 L1.9 12.07 L1.87 12.1 L1.84 12.12 L1.83 12.13 L1.82 12.14 L1.82 12.14 L1.77 12.18 Z M3.91 11.92 L3.92 11.9 L4.03 11.71 L4.04 11.69 L4.14 11.49 L4.16 11.47 L4.26 11.25 L4.27 11.23 L4.37 11.0 L4.38 10.97 L4.48 10.72 L4.49 10.7 L4.59 10.43 L4.6 10.41 L4.69 10.13 L4.7 10.1 L4.79 9.8 L4.79 9.78 L4.87 9.46 L4.88 9.43 L4.96 9.09 L4.96 9.07 L5.03 8.71 L5.03 8.69 L5.09 8.31 L5.1 8.29 L5.15 7.89 L5.15 7.87 L5.19 7.45 L5.19 7.43 L5.22 6.99 L5.22 6.97 L5.24 6.51 L5.24 6.49 L5.25 6.02 L5.26 5.75 L5.28 5.51 L5.32 5.27 L5.38 5.03 L5.45 4.79 L5.54 4.57 L5.64 4.34 L5.75 4.13 L5.88 3.92 L6.03 3.72 L6.18 3.53 L6.35 3.35 L6.53 3.18 L6.72 3.03 L6.92 2.88 L7.13 2.75 L7.34 2.64 L7.57 2.54 L7.79 2.45 L8.03 2.38 L8.27 2.32 L8.51 2.28 L8.75 2.26 L9.0 2.25 L9.25 2.26 L9.49 2.28 L9.73 2.32 L9.97 2.38 L10.21 2.45 L10.43 2.54 L10.66 2.64 L10.87 2.75 L11.08 2.88 L11.28 3.03 L11.47 3.18 L11.65 3.35 L11.82 3.53 L11.97 3.72 L12.12 3.92 L12.25 4.13 L12.36 4.34 L12.46 4.57 L12.55 4.79 L12.62 5.03 L12.68 5.27 L12.72 5.51 L12.74 5.75 L12.75 6.02 L12.76 6.49 L12.76 6.51 L12.78 6.97 L12.78 6.99 L12.81 7.43 L12.81 7.45 L12.85 7.87 L12.85 7.89 L12.9 8.29 L12.91 8.31 L12.97 8.69 L12.97 8.71 L13.04 9.07 L13.04 9.09 L13.12 9.43 L13.13 9.46 L13.21 9.78 L13.21 9.8 L13.3 10.1 L13.31 10.13 L13.4 10.41 L13.41 10.43 L13.51 10.7 L13.52 10.72 L13.62 10.97 L13.63 11.0 L13.73 11.23 L13.74 11.25 L13.84 11.47 L13.86 11.49 L13.96 11.69 L13.97 11.71 L14.08 11.9 L14.09 11.92 L14.14 12.0 L3.86 12.0 L3.91 11.92 Z M9.62 16.17 L9.59 16.21 L9.56 16.24 L9.53 16.28 L9.49 16.31 L9.46 16.34 L9.42 16.37 L9.37 16.4 L9.33 16.42 L9.29 16.44 L9.24 16.46 L9.19 16.47 L9.15 16.49 L9.1 16.49 L9.05 16.5 L9.0 16.5 L8.95 16.5 L8.9 16.49 L8.85 16.49 L8.81 16.47 L8.76 16.46 L8.71 16.44 L8.67 16.42 L8.63 16.4 L8.58 16.37 L8.54 16.34 L8.51 16.31 L8.47 16.28 L8.44 16.24 L8.41 16.21 L8.38 16.17 L8.35 16.12 L8.33 16.08 L8.31 16.04 L8.29 15.99 L8.28 15.94 L8.26 15.9 L8.26 15.85 L8.25 15.8 L8.25 15.73 L8.24 15.65 L8.23 15.58 L8.21 15.51 L8.18 15.44 L8.15 15.38 L8.11 15.31 L8.06 15.26 L8.01 15.2 L7.96 15.15 L7.9 15.11 L7.83 15.08 L7.76 15.05 L7.69 15.03 L7.62 15.01 L7.55 15.0 L7.48 15.0 L7.4 15.01 L7.33 15.02 L7.26 15.04 L7.19 15.07 L7.13 15.1 L7.06 15.14 L7.01 15.19 L6.95 15.24 L6.9 15.29 L6.86 15.35 L6.83 15.42 L6.8 15.49 L6.78 15.56 L6.76 15.63 L6.75 15.7 L6.75 15.77 L6.75 15.87 L6.76 15.92 L6.77 16.02 L6.77 16.07 L6.79 16.16 L6.8 16.21 L6.82 16.31 L6.83 16.36 L6.86 16.45 L6.88 16.5 L6.91 16.59 L6.93 16.63 L6.97 16.72 L6.99 16.77 L7.04 16.85 L7.06 16.9 L7.12 16.98 L7.14 17.02 L7.2 17.1 L7.23 17.14 L7.29 17.21 L7.32 17.25 L7.39 17.32 L7.43 17.36 L7.5 17.43 L7.54 17.46 L7.61 17.52 L7.65 17.55 L7.73 17.61 L7.77 17.63 L7.85 17.69 L7.9 17.71 L7.98 17.76 L8.03 17.78 L8.12 17.82 L8.16 17.84 L8.25 17.87 L8.3 17.89 L8.39 17.92 L8.44 17.93 L8.54 17.95 L8.59 17.96 L8.68 17.98 L8.73 17.98 L8.83 17.99 L8.88 18.0 L8.98 18.0 L9.02 18.0 L9.12 18.0 L9.17 17.99 L9.27 17.98 L9.32 17.98 L9.41 17.96 L9.46 17.95 L9.56 17.93 L9.61 17.92 L9.7 17.89 L9.75 17.87 L9.84 17.84 L9.88 17.82 L9.97 17.78 L10.02 17.76 L10.1 17.71 L10.15 17.69 L10.23 17.63 L10.27 17.61 L10.35 17.55 L10.39 17.52 L10.46 17.46 L10.5 17.43 L10.57 17.36 L10.61 17.32 L10.68 17.25 L10.71 17.21 L10.77 17.14 L10.8 17.1 L10.86 17.02 L10.88 16.98 L10.94 16.9 L10.96 16.85 L11.01 16.77 L11.03 16.72 L11.07 16.63 L11.09 16.59 L11.12 16.5 L11.14 16.45 L11.17 16.36 L11.18 16.31 L11.2 16.21 L11.21 16.16 L11.23 16.07 L11.23 16.02 L11.24 15.92 L11.25 15.85 L11.24 15.77 L11.23 15.7 L11.21 15.62 L11.19 15.55 L11.15 15.48 L11.11 15.42 L11.07 15.36 L11.01 15.3 L10.96 15.26 L10.89 15.21 L10.83 15.18 L10.63 15.08 L10.56 15.05 L10.49 15.03 L10.42 15.01 L10.35 15.0 L10.27 15.0 L10.2 15.01 L10.13 15.02 L10.06 15.04 L9.99 15.07 L9.92 15.1 L9.86 15.14 L9.8 15.19 L9.75 15.24 L9.7 15.29 L9.66 15.35 L9.62 15.42 L9.6 15.49 L9.57 15.56 L9.56 15.63 L9.55 15.7 L9.55 15.78 L9.55 15.85 L9.57 15.92 L9.59 15.99 L9.61 16.06 L9.65 16.13 L9.65 16.13 L9.62 16.17 Z";

    /// <summary>
    /// 构造底部附加操作区的菜单项。
    ///
    /// 动作项（OP.GG / 修复无限加载 / 公告）在前、路由项（设置）在后，
    /// 拼出来的顺序即旧版 sidebar-bottom 的排列顺序。
    ///
    /// OP.GG 用旧版同款品牌图标（18px PNG）覆盖 Segoe 字形；公告用旧版同款
    /// 铃铛几何（<see cref="NoticeBellPath"/>）覆盖实心 Ringer 字形。
    /// </summary>
    private static NavigationViewItemData[] BuildFooterItems()
    {
        var items = new List<NavigationViewItemData>();

        foreach (var action in RouteCatalog.FooterActions)
        {
            var item = NavItem(action.Label, action.Glyph, action.Tag);

            if (action.Tag == SidebarTags.Opgg && !string.IsNullOrEmpty(AppAssets.OpggNavIconUri))
            {
                // 旧版用的是彩色品牌 SVG 而非 Segoe 字形；IconElement 优先于 Icon 字形渲染。
                item = item with { IconElement = ImageIcon(new Uri(AppAssets.OpggNavIconUri)) };
            }
            else if (action.Tag == SidebarTags.Notice)
            {
                // 旧版是 Feather 风格细线铃铛，PathIcon 填充色继承前景色（随主题/悬停变色）。
                item = item with { IconElement = PathIcon(NoticeBellPath) };
            }

            items.Add(item);
        }

        foreach (var route in RouteCatalog.Footer)
        {
            items.Add(NavItem(route.Label, route.Glyph, route.Key));
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
