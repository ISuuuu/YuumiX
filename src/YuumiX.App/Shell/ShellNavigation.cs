using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
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
using YuumiX.Core;
using YuumiX.Core.Automation;
using YuumiX.Core.Lcu;
using YuumiX.Core.Localization;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Shell;

/// <summary>
/// 侧边栏导航项构建、开合状态校正、动作项/路由项分发、游戏阶段自动跳转及页面路由工厂。
/// </summary>
internal static class ShellNavigation
{
    /// <summary>构造主导航区菜单项，按隐藏菜单配置过滤并应用当前语言标签。</summary>
    internal static NavigationViewItemData[] BuildPrimaryMenuItems(
        I18nService i18n,
        bool hideTft,
        bool hideSavedPlayers) =>
        RouteCatalog.Primary
            .Where(item => item.Key switch
            {
                "Tft" => !hideTft,
                "SavedPlayers" => !hideSavedPlayers,
                _ => true,
            })
            .Select(item => NavItem(GetNavLabel(i18n, item.Key, item.Label), item.Glyph, item.Key))
            .ToArray();

    /// <summary>
    /// 构造底部附加操作区的菜单项。
    ///
    /// 动作项（OP.GG / 修复无限加载 / 公告）在前、路由项（设置）在后，
    /// 拼出来的顺序即旧版 sidebar-bottom 的排列顺序。
    ///
    /// OP.GG 用旧版同款品牌图标（54px PNG，3.4× 超采样，对应显示面 16 DIP）覆盖 Segoe 字形；公告与其余菜单项
    /// 一致走 Segoe 字形（<see cref="Icons.Notice"/>，RouteCatalog.FooterActions 已配好），
    /// 不再保留旧版 Feather 铃铛的填充几何特殊渲染。
    /// </summary>
    internal static NavigationViewItemData[] BuildFooterItems(I18nService i18n)
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

    internal static string GetNavLabel(I18nService i18n, string key, string fallback) => key switch
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
    /// 创建 NavigationView.Loaded 回调（需经 UseRef 缓存为稳定委托）：
    /// 收起态在 Loaded 同步校正以避免「先开后收」闪烁，展开态延后到调度器 Low 优先级以保证标签正常渲染。
    /// </summary>
    internal static RoutedEventHandler CreatePaneLoadedHandler(AppServices services, Ref<bool> paneInitialized) =>
        (sender, _) =>
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
        };

    /// <summary>
    /// 导航栏开合变化 → 更新本地状态并写回 config.json（跨启动恢复）。
    /// 初始化阶段（模板应用复位）触发的事件由 paneInitialized 守卫忽略。
    /// </summary>
    internal static void HandlePaneOpenChanged(
        bool open,
        Ref<bool> paneInitialized,
        Action<bool> setPaneOpen,
        AppServices services,
        ILogger logger)
    {
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
            logger.LogWarning(ex, "导航栏开合状态写入 config.json 失败，本次变更不会跨启动保留");
        }
    }

    /// <summary>
    /// 处理侧边栏底部动作项（OP.GG / 修复无限加载 / 公告）：
    /// 动作项统一走 ItemInvoked，确保重复点击已选中项时仍能触发。
    /// </summary>
    internal static void HandleActionTag(
        string? tag,
        bool isClientConnected,
        ToastQueue toastQueue,
        Action<bool> setShowNotice)
    {
        if (tag == SidebarTags.Opgg)
        {
            if (!isClientConnected)
            {
                toastQueue.Error("请先连接客户端");
            }
            else
            {
                OpggWindowHost.ShowOrActivate();
            }

            return;
        }

        if (tag == SidebarTags.Reconnect)
        {
            if (!isClientConnected)
            {
                toastQueue.Error("请先连接客户端");
                return;
            }

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
        }
    }

    /// <summary>处理路由项切换：跳过动作项，仅在切换到不同模块时压栈导航。</summary>
    internal static void HandleSelectedTagChanged(
        string? tag,
        AppRoute currentRoute,
        Action<AppRoute> navigate)
    {
        // 动作项已由 OnItemInvoked 处理：首次点击时两个回调都会触发，这里跳过避免重复执行。
        if (tag is SidebarTags.Opgg or SidebarTags.Reconnect or SidebarTags.Notice)
        {
            return;
        }

        // 仅在切换到不同模块时导航；已在目标模块（如带参 CareerRoute/SearchRoute）不覆盖现有路由。
        if (tag is not null && RouteCatalog.KeyFor(currentRoute) != tag && RouteCatalog.Resolve(tag) is { } route)
        {
            navigate(route);
        }
    }

    /// <summary>
    /// 订阅游戏阶段与 LCU 连接状态：进入选人或对局时自动切到对局页（并在配置开启时拉起 OP.GG）。
    /// </summary>
    internal static Action AttachGamePhaseAndLcu(
        AutomationHost automationHost,
        LcuConnection lcu,
        AppServices services,
        Action<GamePhase> setGamePhase,
        Action<bool> setIsClientConnected,
        Action navigateToGameInfo)
    {
        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        var lastPhase = GamePhase.None;

        void EnqueueNavigation()
        {
            if (dispatcherQueue is not null)
            {
                dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => navigateToGameInfo());
            }
            else
            {
                navigateToGameInfo();
            }
        }

        void HandlePhase(GamePhase phase)
        {
            setGamePhase(phase);
            bool shouldNavigate = (phase == GamePhase.ChampSelect && lastPhase != GamePhase.ChampSelect)
                || (phase is GamePhase.InProgress or GamePhase.GameStart
                    && lastPhase is not (GamePhase.InProgress or GamePhase.GameStart or GamePhase.ChampSelect));

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
    }

    /// <summary>
    /// 路由 → 页面组件。已迁移的模块在这里替换占位组件；其余仍走 <see cref="PlaceholderPage"/>，
    /// 待迁移清单以 <see cref="PageCatalog"/> 为准。
    /// </summary>
    internal static Element BuildPage(AppRoute route) => route switch
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

    internal static Element BuildPlaceholder(AppRoute route)
    {
        var (title, sourceView, pending) = PageCatalog.For(RouteCatalog.KeyFor(route));

        // 主页展示品牌 Logo（对应原 Home.vue 顶部 96px 的 Logo）；其余占位页不显示图片。
        var brandLogoUri = route is AppRoute.HomeRoute ? AppAssets.LogoUri : null;

        return Component<PlaceholderPage, PlaceholderProps>(
            new PlaceholderProps(title, sourceView, pending, brandLogoUri));
    }
}
