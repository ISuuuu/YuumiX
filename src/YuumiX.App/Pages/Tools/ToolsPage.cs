using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Pages.Tools.Sections;
using YuumiX.App.Theming;
using YuumiX.Core.Assets;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Features.Tools.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Tools;

/// <summary>
/// 工具箱页：状态编排与写回（R6/R7 hooks 化 + R14 拆分后的聚合层）。
///
/// 页面只保留：上下文解析、状态 hooks、异步资源/变更、写回、分组装配与滚动容器；
/// 四个业务分节在 <see cref="Sections"/> 下各一个文件，共享壳件在 <see cref="ToolsVisuals"/>。
///
/// 状态约定（AGENTS.md §5.10）：草稿与页内状态一律 UseState（不可变 record，跨线程写 threadSafe），
/// 非渲染数据用 UseRef，配置 POCO 由 Commit 写回后经 setSavedAt 触发重渲染——没有任何"踢重渲染"辅助。
/// 异步约定（§5.11）：读走 UseResource（展开卡片时才拉取，收起不重复拉），按钮触发读/写走 UseMutation。
/// </summary>
public sealed class ToolsPage : Component
{
    /// <summary>目录类资源（英雄/技能/挑战勋章）"每次挂载只拉一次"的缓存窗口；收起再展开命中缓存不再打 LCU。</summary>
    private static readonly TimeSpan CatalogStaleWindow = TimeSpan.FromHours(8);

    private static readonly ChampionUi InitialChampion = new(0, 0, 0, 0, string.Empty, string.Empty);
    private static readonly SpectateUi InitialSpectate = new(string.Empty, 0);
    private static readonly FriendUi InitialFriend = new([], string.Empty, 0, [], false, [], 0, 0);
    private static readonly ProfileDraftUi InitialDraft = new(string.Empty, string.Empty);
    private static readonly ProfileBgUi InitialBg = new("1", [], 0, false);
    private static readonly SpoofUi InitialSpoof = new(0, 0, 0);
    private static readonly ConfirmUi InitialConfirm = new(false, false, false);
    private static readonly BadgeUi InitialBadge = new(string.Empty, 0, string.Empty, []);

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var config = services.Config;
        var toolsService = services.Services.GetRequiredService<IToolsService>();
        var friendCleanerService = services.Services.GetRequiredService<IFriendCleanerService>();
        var lcuConnection = services.Services.GetRequiredService<LcuConnection>();
        var toast = UseContext(AppHost.ToastContext)!;

        var (savedAt, setSavedAt) = UseState<DateTime?>(null, threadSafe: true);
        var (error, setError) = UseState<string?>(null, threadSafe: true);
        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);

        var (lang, setLang) = UseState(services.I18n.CurrentLanguage, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = services.I18n.CurrentLanguageChanged.Subscribe(setLang);
                return () => subscription.Dispose();
            },
            0);

        // 记录展开的卡片 key 集合（默认全部收起，对齐原版设计）
        var initialExpanded = UseMemo(() => new HashSet<string>(), 0);
        var (expandedKeys, setExpandedKeys) = UseState(initialExpanded, threadSafe: true);

        // 各分节的页内状态（不可变 record，一次 hook 管一组，见各 Sections 文件）；
        // 初值用静态单例：REACTOR_HOOKS_013 禁止每次渲染重新分配 UseState 初值。
        var (championSt, setChampionSt) = UseState(InitialChampion, threadSafe: true);
        var (spectate, setSpectate) = UseState(InitialSpectate, threadSafe: true);
        var (friend, setFriend) = UseState(InitialFriend, threadSafe: true);
        var (draft, setDraft) = UseState(InitialDraft, threadSafe: true);
        var (bg, setBg) = UseState(InitialBg, threadSafe: true);
        var (spoof, setSpoof) = UseState(InitialSpoof, threadSafe: true);
        var (confirm, setConfirm) = UseState(InitialConfirm, threadSafe: true);
        var (badge, setBadge) = UseState(InitialBadge, threadSafe: true);

        // 游戏设置锁定状态（null = 未探测）
        var (isSettingsLocked, setIsSettingsLocked) = UseState<bool?>(null, threadSafe: true);
        if (isSettingsLocked is null)
        {
            try
            {
                setIsSettingsLocked(toolsService.GetGameSettingsReadonly(config.General.LolPath));
            }
            catch
            {
                setIsSettingsLocked(false);
            }
        }

        // 正文列宽度：跟随滚动区宽度收缩，超过上限后固定并居中（原因见 ContentColumn 注释）。
        var (columnWidth, setColumnWidth) = UseState(ContentColumn.MaxWidth, threadSafe: true);

        // 连接态必须订阅：原来直接读 lcu.CurrentState，客户端启动或退出都不会触发重渲染，
        // 「离线禁用」就会一直停在进页那一刻的判断上。
        var (connState, setConnState) = UseState(lcuConnection.CurrentState, threadSafe: true);
        bool isLcuConnected = connState.Status == LcuConnectionStatus.Connected;

        var loggerRef = UseRef<ILogger<ToolsPage>>(null!);
        var logger = loggerRef.Current ??= services.LoggerFactory.CreateLogger<ToolsPage>();

        // FriendUi 的最新值镜像：mutation 回调（异步落地后）读它构造 with 更新，
        // 避免闭包里捕获过期渲染的 friend（UseState 的 setter 没有 updater 形态）。
        var friendRef = UseRef(friend);
        friendRef.Current = friend;

        var closeDialogsRef = UseRef<Action?>(null);
        closeDialogsRef.Current = () =>
        {
            setConfirm(InitialConfirm);
            setFriend(friendRef.Current with { ConfirmingDelete = false, PendingDeleteIds = [] });
        };
        var navigatedFromRef = UseRef<Action<NavigatedFromContext>?>(null);
        navigatedFromRef.Current ??= _ => closeDialogsRef.Current?.Invoke();
        UseNavigationLifecycle(onNavigatedFrom: navigatedFromRef.Current);


        // ── 异步资源（R7）：英雄/技能目录**进页即预热**，挑战勋章仍在勋章卡展开时才拉。
        // 原先目录是按需拉取（展开 autoHover/autoBan/autoSpells 才请求），代价是展开那一下要等
        // LCU 往返 + 之后再渲染一百多个英雄项，手感明显比其它卡片慢一拍；改成进页先拉一次，
        // 8 小时缓存窗口内展开就是纯渲染，不再有网络等待（离线时不拉）。
        bool catalogNeeded = isLcuConnected;
        bool badgesNeeded = isLcuConnected && expandedKeys.Contains("badges");
        var catalogOptions = new ResourceOptions(StaleTime: CatalogStaleWindow, CacheTime: CatalogStaleWindow);

        var championsRes = UseResource(
            async ct =>
            {
                if (!catalogNeeded || ct.IsCancellationRequested)
                {
                    return Array.Empty<ChampionSummaryItem>();
                }
                return await toolsService.GetChampionSummaryAsync();
            },
            deps: new object[] { catalogNeeded },
            options: catalogOptions);

        var spellsRes = UseResource(
            async ct =>
            {
                if (!catalogNeeded || ct.IsCancellationRequested)
                {
                    return Array.Empty<SummonerSpellItem>();
                }
                return await toolsService.GetSummonerSpellsAsync();
            },
            deps: new object[] { catalogNeeded },
            options: catalogOptions);

        // 手动"重新加载"：步进序号进 deps，强制换缓存键重新拉取
        var (challengesTick, setChallengesTick) = UseState(0, threadSafe: true);
        var challengesRes = UseResource(
            async ct =>
            {
                if (!badgesNeeded || ct.IsCancellationRequested)
                {
                    return Array.Empty<ChallengeItem>();
                }
                return await toolsService.GetPlayerChallengesAsync();
            },
            deps: new object[] { badgesNeeded, challengesTick },
            options: catalogOptions);

        var champions = championsRes.AsList();
        var spells = spellsRes.AsList();
        var challenges = challengesRes.AsList();
        bool championsLoading = championsRes.IsLoading() || spellsRes.IsLoading();
        bool challengesLoading = challengesRes.IsLoading();

        // 头像预热：展开「自动选用 / 自动禁用」那一下之所以比别的卡片慢，不是数据多，
        // 是要在一帧里挂 173 个英雄头像，每个都要走一次「LCU 请求 → 落盘 → 解码」。
        // 目录一就绪就在后台把这些图标逐个过一遍（命中缓存直接跳过），展开时就只剩布局与解码。
        UseEffect(
            () =>
            {
                if (champions.Count == 0)
                {
                    return static () => { };
                }

                var assets = services.Services.GetRequiredService<AssetService>();
                var warmCts = new CancellationTokenSource();
                _ = WarmChampionIconsAsync(assets, champions, logger, warmCts.Token);
                return () =>
                {
                    warmCts.Cancel();
                    warmCts.Dispose();
                };
            },
            champions.Count);

        // 挑战勋章加载完成的提示（对齐旧 LoadChallenges 的 toast：每次真实拉取成功提示一次）
        UseEffect(
            () =>
            {
                if (challenges.Count > 0)
                {
                    toast.Success($"已加载 {challenges.Count} 个挑战勋章");
                }
            },
            challenges);

        // ── 好友清理（R7）：扫描是带进度的按钮触发读，删除是写，二者都走 UseMutation
        //（IsPending 即 loading，卸载自动取消，异常统一进 toast）。
        var scanFriends = UseMutation<object?, IReadOnlyList<FriendEntry>>(
            (_, ct) => friendCleanerService.ScanFriendsAsync((done, total) =>
                setFriend(friendRef.Current with { ScanCompleted = done, ScanTotal = total }), ct),
            new MutationOptions<object?, IReadOnlyList<FriendEntry>>(
                OnOptimistic: _ => setFriend(friendRef.Current with { ScanCompleted = 0, ScanTotal = 0 }),
                OnSuccess: (list, _) =>
                {
                    setFriend(friendRef.Current with { AllFriends = list });
                    toast.Success(services.I18n.T("tools.friendScanSuccess", "好友扫描完成，共找到 {count} 位好友").Replace("{count}", list.Count.ToString()));
                },
                OnError: (ex, _) => toast.Error(services.I18n.T("tools.friendScanFailed", "好友扫描失败: {error}").Replace("{error}", ex.Message))));

        var deleteFriends = UseMutation<List<string>, int>(
            (ids, ct) => friendCleanerService.BatchDeleteFriendsAsync(ids, ct),
            new MutationOptions<List<string>, int>(
                OnSuccess: (count, ids) =>
                {
                    toast.Success(services.I18n.T("tools.friendDeleteSuccess", "成功清理 {count} 位离线好友").Replace("{count}", count.ToString()));
                    setFriend(friendRef.Current with { SelectedIds = [], ConfirmingDelete = false, PendingDeleteIds = [] });
                    _ = scanFriends.RunAsync(null);
                },
                OnError: (ex, _) => toast.Error(services.I18n.T("tools.friendDeleteFailed", "删除好友失败: {error}").Replace("{error}", ex.Message))));

        var restartClient = UseMutation<object?, bool>(
            async (_, ct) =>
            {
                var transport = lcuConnection.Transport ?? throw new InvalidOperationException(services.I18n.T("tools.clientDisconnected", "LCU 客户端未连接，请先启动英雄联盟客户端"));
                var response = await transport.SendAsync(LcuRequest.Post("/riotclient/kill-and-restart-ux", "{}"), ct);
                EnsureRestartSucceeded(response, services.I18n.T("tools.httpStatusError", "HTTP 状态码 {status}").Replace("{status}", response.StatusCode.ToString()));
                return true;
            },
            new MutationOptions<object?, bool>(
                OnSuccess: (_, _) => toast.Success(services.I18n.T("tools.restartSuccess", "已向 Riot Client 发送重启指令，客户端正在重新引导...")),
                OnError: (ex, _) => toast.Error(services.I18n.T("tools.operationFailed", "操作失败: {error}").Replace("{error}", ex.Message))));
        var clearBadges = UseMutation<object?, bool>(
            async (_, ct) => { await toolsService.ClearChallengeBadgesAsync(ct); return true; },
            new MutationOptions<object?, bool>(
                OnSuccess: (_, _) => toast.Success(services.I18n.T("tools.clearBadgesSuccess", "已卸下展示的全部挑战勋章")),
                OnError: (ex, _) => toast.Error(services.I18n.T("tools.clearBadgesFailed", "卸下勋章失败: {error}").Replace("{error}", ex.Message))));
        var clearBorder = UseMutation<object?, bool>(
            async (_, ct) => { await toolsService.ClearProfileTokensAsync(ct); return true; },
            new MutationOptions<object?, bool>(
                OnSuccess: (_, _) => toast.Success(services.I18n.T("tools.clearBorderSuccess", "已卸下生涯头像框")),
                OnError: (ex, _) => toast.Error(services.I18n.T("tools.clearBorderFailed", "卸下头像框失败: {error}").Replace("{error}", ex.Message))));

        UseEffect(
            () =>
            {
                var subscription = theme.Subscribe(setPalette);
                return () =>
                {
                    subscription.Dispose();
                };
            },
            0);

        UseEffect(
            () =>
            {
                var subscription = lcuConnection.States.Subscribe(setConnState);
                return () => subscription.Dispose();
            },
            0);

        void Commit(string field)
        {
            try
            {
                services.ConfigStore.SaveWithBackup(config);
                setError(null);
                setSavedAt(DateTime.Now);
                toast.Success("设置已自动保存");
                logger.LogInformation("功能配置已写回: {Field}", field);
            }
            catch (Exception ex)
            {
                setError(ex.Message);
                setSavedAt(null);
                toast.Error($"设置保存失败: {ex.Message}");
                logger.LogError(ex, "功能配置写回失败: {Field}", field);
            }
        }

        void ToggleExpanded(string key)
        {
            var next = new HashSet<string>(expandedKeys);
            if (!next.Add(key))
            {
                next.Remove(key);
            }
            setExpandedKeys(next);
        }

        var ui = new ToolsUi(
            config, palette, toast, toolsService, friendCleanerService, lcuConnection, logger,
            isLcuConnected, expandedKeys, ToggleExpanded, Commit, services.I18n);

        // 原实现的 feedbackBanner（错误/保存横幅）建而未渲染、actionFeedback 从未被写入，均按死代码删除；
        // savedAt/error 保留——值虽不参与渲染，但 Commit 靠它们变更触发重渲染（替代旧 Rerender 踢法），
        // error 兼任"首次保存即失败"（savedAt 尚为 null）时的失败路径触发器。
        var offlineBanner = isLcuConnected ? null : ToolsVisuals.OfflineBanner();

        // 【A/B 实验】用传统 ScrollViewer 替代 WinUI 2.5 的 ScrollView。
        // 依据：同一窗口内，ScrollView 之外的元素（侧边栏图标、RenderPlaceholder 里的按钮）
        // 归一化边缘梯度 0.41~0.58，而 ScrollView 内的标题/说明只有 0.26~0.31，
        // 且与滚动位置无关（offset=0 时同样偏软）。判定为 ScrollPresenter 内容层渲染所致。
        var mainScroll = ScrollViewer(
            VStack(8,
                PageHeaders.PageHeader(
                    services.I18n.T("tools.title", "其他功能"),
                    includeBottomMargin: false)
                    .Margin(0, 0, 0, 8),

                offlineBanner ?? Empty(),

                // 1. 英雄选择
                ToolsVisuals.GroupHeader(services.I18n.T("tools.groupChampSelect", "英雄选择")),
                ChampionSection.Render(ui, championSt, setChampionSt, champions, championsLoading, spells, spellsRes.IsLoading()),

                // 2. 游戏
                ToolsVisuals.GroupHeader(services.I18n.T("tools.groupGame", "游戏")),
                GameSection.Render(ui, spectate, setSpectate, friend, setFriend, scanFriends, deleteFriends, isSettingsLocked, setIsSettingsLocked),

                // 3. 客户端
                ToolsVisuals.GroupHeader(services.I18n.T("tools.groupClient", "客户端")),
                ClientSection.Render(ui, confirm, setConfirm, restartClient.IsPending),

                // 4. 个人主页
                ToolsVisuals.GroupHeader(services.I18n.T("tools.groupProfile", "个人主页")),
                ProfileSection.Render(ui, draft, setDraft, bg, setBg, spoof, setSpoof, confirm, setConfirm, clearBadges.IsPending, clearBorder.IsPending, badge, setBadge, champions, challenges, challengesLoading, () => setChallengesTick(challengesTick + 1))
            )
            // 正文列：显式宽度 + 居中（不要写成 MaxWidth + Stretch，原因见 ContentColumn 注释）。
            .Width(columnWidth)
            .HAlign(HorizontalAlignment.Center)
            .Padding(24)
        );

        mainScroll = mainScroll.OnSizeChanged((_, e) =>
        {
            var target = ContentColumn.Fit(e.NewSize.Width);
            if (Math.Abs(target - columnWidth) >= 0.5)
            {
                setColumnWidth(target);
            }
        });

        return Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                mainScroll.Grid(row: 0, column: 0),
                ConfirmationDialog.Render(
                    services.I18n.T("tools.restartConfirmTitle", "重启客户端"),
                    Body(services.I18n.T("tools.restartConfirmPrompt", "确定重启客户端？无需重新登录。")).TextWrapping(),
                    services.I18n.T("tools.restartConfirmAction", "确认重启"), services.I18n.T("common.cancel", "取消"),
                    confirm.RestartClient,
                    () => { setConfirm(confirm with { RestartClient = false }); if (isLcuConnected && !restartClient.IsPending) _ = restartClient.RunAsync(null); },
                    () => setConfirm(confirm with { RestartClient = false }), palette, isLcuConnected && !restartClient.IsPending),
                ConfirmationDialog.Render(
                    services.I18n.T("tools.clearBadgesTitle", "卸下全部勋章"),
                    Body(services.I18n.T("tools.clearBadgesPrompt", "确定卸下展示的全部挑战勋章吗？")).TextWrapping(),
                    services.I18n.T("tools.clearConfirmAction", "确认卸下"), services.I18n.T("common.cancel", "取消"),
                    confirm.ClearBadges,
                    () => { setConfirm(confirm with { ClearBadges = false }); if (isLcuConnected && !clearBadges.IsPending) _ = clearBadges.RunAsync(null); },
                    () => setConfirm(confirm with { ClearBadges = false }), palette, isLcuConnected && !clearBadges.IsPending),
                ConfirmationDialog.Render(
                    services.I18n.T("tools.clearBorderTitle", "卸下头像框"),
                    Body(services.I18n.T("tools.clearBorderPrompt", "确定卸下生涯头像框吗？")).TextWrapping(),
                    services.I18n.T("tools.clearConfirmAction", "确认卸下"), services.I18n.T("common.cancel", "取消"),
                    confirm.ClearBorder,
                    () => { setConfirm(confirm with { ClearBorder = false }); if (isLcuConnected && !clearBorder.IsPending) _ = clearBorder.RunAsync(null); },
                    () => setConfirm(confirm with { ClearBorder = false }), palette, isLcuConnected && !clearBorder.IsPending),
            ]
        );
    }

    internal static void EnsureRestartSucceeded(LcuResponse response, string errorMessage)
    {
        if (!response.IsSuccess)
        {
            throw new InvalidOperationException(errorMessage);
        }
    }

    /// <summary>
    /// 后台预热英雄头像：并发 4（<see cref="AssetService"/> 自身还有 8 个许可，不会把页面自己的请求挤掉），
    /// 命中本地缓存的直接返回。目标是把「展开选人/禁用卡片」的等待从一百多次资源请求降成一次布局。
    /// 任何失败都只是少预热一张图 —— 展开时仍会走 <c>LcuImage</c> 的普通加载兜底。
    /// </summary>
    private static async Task WarmChampionIconsAsync(
        AssetService assets,
        IReadOnlyList<ChampionSummaryItem> champions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await Parallel.ForEachAsync(
                champions,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (champion, token) =>
                {
                    try
                    {
                        await assets.GetCachedPathAsync(champion.IconPath, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // 页面卸载或切走：正常退出路径
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "英雄头像预热失败: {Path}", champion.IconPath);
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 同上：取消不需要上报
        }
    }
}
