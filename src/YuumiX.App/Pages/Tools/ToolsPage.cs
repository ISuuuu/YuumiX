using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Pages.Tools.Sections;
using YuumiX.App.Theming;
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
    private static readonly FriendUi InitialFriend = new([], string.Empty, 0, [], false, 0, 0);
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

        // 正文列宽度与左边距：跟随滚动区收缩；边距必须取整，避免 ComboBox 弹层落在半像素上。
        var (columnWidth, setColumnWidth) = UseState(ContentColumn.MaxWidth, threadSafe: true);
        var (columnOffset, setColumnOffset) = UseState(0.0, threadSafe: true);

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

        // ── 异步资源（R7）：英雄/技能目录在任一相关卡片展开时拉取；挑战勋章在勋章卡展开时拉取。
        // 收起 → 展开命中 StaleTime 内的缓存，不再重复请求 LCU（对齐旧"列表非空不重拉"语义）。
        bool catalogNeeded = isLcuConnected && (expandedKeys.Contains("autoHover") || expandedKeys.Contains("autoBan") || expandedKeys.Contains("autoSpells"));
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
            (_, _) => friendCleanerService.ScanFriendsAsync((done, total) =>
                setFriend(friendRef.Current with { ScanCompleted = done, ScanTotal = total })),
            new MutationOptions<object?, IReadOnlyList<FriendEntry>>(
                OnOptimistic: _ => setFriend(friendRef.Current with { ScanCompleted = 0, ScanTotal = 0 }),
                OnSuccess: (list, _) =>
                {
                    setFriend(friendRef.Current with { AllFriends = list });
                    toast.Success($"好友扫描完成，共找到 {list.Count} 位好友");
                },
                OnError: (ex, _) => toast.Error($"好友扫描失败: {ex.Message}")));

        var deleteFriends = UseMutation<List<string>, int>(
            (ids, _) => friendCleanerService.BatchDeleteFriendsAsync(ids),
            new MutationOptions<List<string>, int>(
                OnSuccess: (count, ids) =>
                {
                    toast.Success($"成功清理 {count} 位离线好友");
                    setFriend(friendRef.Current with { SelectedIds = [], ConfirmingDelete = false });
                    _ = scanFriends.RunAsync(null);
                },
                OnError: (ex, _) => toast.Error($"删除好友失败: {ex.Message}")));

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
                ClientSection.Render(ui, confirm, setConfirm),

                // 4. 个人主页
                ToolsVisuals.GroupHeader(services.I18n.T("tools.groupProfile", "个人主页")),
                ProfileSection.Render(ui, draft, setDraft, bg, setBg, spoof, setSpoof, confirm, setConfirm, badge, setBadge, champions, challenges, challengesLoading, () => setChallengesTick(challengesTick + 1))
            )
            // 显式宽度 + 整数左边距：不要用 HAlign(Center)，奇数宽度差会产生半像素偏移。
            .Width(columnWidth)
            .HAlign(HorizontalAlignment.Left)
            .Margin(left: columnOffset)
            .Padding(24)
        );

        mainScroll = mainScroll.OnSizeChanged((_, e) =>
        {
            var targetWidth = ContentColumn.Fit(e.NewSize.Width);
            var targetOffset = ContentColumn.CenterOffset(e.NewSize.Width, targetWidth);
            if (Math.Abs(targetWidth - columnWidth) >= 0.5)
            {
                setColumnWidth(targetWidth);
            }

            if (Math.Abs(targetOffset - columnOffset) >= 0.5)
            {
                setColumnOffset(targetOffset);
            }
        });

        return Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                mainScroll.Grid(row: 0, column: 0),
            ]
        );
    }
}
