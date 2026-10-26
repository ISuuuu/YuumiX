using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Navigation;
using YuumiX.App.Theming;
using YuumiX.Core;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.GameLifecycle;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.SavedPlayers.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Career;

/// <summary>生涯页的页签。顺序即界面顺序（生涯战绩 / 战利品 / 英雄熟练度）；查看他人时隐藏页签栏。</summary>
internal enum CareerTab
{
    Matches,
    Loot,
    Mastery,
}

/// <summary>
/// 当前生涯的查看目标：<c>null</c> 表示自己；非 <c>null</c> 表示查看他人（需求 §2.8）。
/// puuid 与预载资料合成一个状态，避免"目标换了、资料还是上一个人"的中间帧。
/// </summary>
public sealed record CareerViewTarget(string Puuid, SummonerProfile? Preloaded);

/// <summary>生涯页 props：路由参数（R8）。Puuid 非空 = 查看他人生涯，可带预载资料。</summary>
public sealed record CareerPageProps(string? Puuid = null, SummonerProfile? Preload = null);

/// <summary>
/// 生涯页：资料卡 + 页签，对应原 <c>Career.vue</c> 与其三个子组件。
/// 本页只做状态编排（R14）：数据加载在 <see cref="CareerLoaders"/>，战利品页签是
/// <see cref="CareerLootSection"/> 子组件（五个批量操作已 UseMutation 化，R7），
/// 确认/数量弹窗仍由本页统一遮罩（弹窗必须盖整个正文区域）。
///
/// 查看他人生涯（§2.8）：其他页面以 <c>AppRoute.CareerRoute(puuid, preload)</c> 路由
/// 跳入（R8：参数跟着路由走），本页进入"查看他人"模式——隐藏战利品/熟练度页签、
/// 显示「返回我的生涯」、刷新按钮改为刷新他人；解析失败用占位对象兜底，不阻断排位/战绩加载。
/// 请求代际防串号：<see cref="Render"/> 内 <c>requestSeqRef</c> 递增计数，回写前校验
/// （先点 A 再点 B，慢响应不回写）。
///
/// <b>规模</b>：约 400 行（R14 过渡期，超 300 理由：hooks 编排必须整块留在 Render 内保证调用顺序；
/// 加载/弹窗/命令/分节实现已外移到 CareerLoaders/CareerLootSection/CareerCommands/CareerMatchesSection）。
/// </summary>
public sealed class CareerPage : Component<CareerPageProps>
{
    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var summonerService = services.Services.GetRequiredService<ISummonerService>();
        var careerService = services.Services.GetRequiredService<ICareerService>();
        var matchService = services.Services.GetRequiredService<IMatchService>();
        var lifecycle = services.Services.GetService<GameLifecycleService>();
        var lcu = services.Services.GetRequiredService<LcuConnection>();

        var nav = UseNavigation<AppRoute>();

        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = theme.Subscribe(setPalette);
                return () => subscription.Dispose();
            },
            0);

        // 连接态走订阅值而非 lcu.CurrentState：客户端启动/退出必须让本页重渲染，
        // 否则离线占位会停在进页那一刻的判断上（客户端启动后仍是一张空页）。
        var (connState, setConnState) = UseState(lcu.CurrentState, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = lcu.States.Subscribe(setConnState);
                return () => subscription.Dispose();
            },
            0);

        var (summoner, setSummoner) = UseState<SummonerProfile?>(null, threadSafe: true);
        var (ranked, setRanked) = UseState(RankedStats.Empty, threadSafe: true);
        var (matches, setMatches) = UseState(CareerLoaders.EmptyMatches, threadSafe: true);
        var (loot, setLoot) = UseState<PlayerLootOverview?>(null, threadSafe: true);
        var (mastery, setMastery) = UseState<MasteryOverview?>(null, threadSafe: true);
        var (tab, setTab) = UseState(CareerTab.Matches, threadSafe: true);
        var (queueFilter, setQueueFilter) = UseState(0, threadSafe: true);
        var (loading, setLoading) = UseState(true, threadSafe: true);
        var (copied, setCopied) = UseState(false, threadSafe: true);
        var (refreshToken, setRefreshToken) = UseState(0, threadSafe: true);
        var (viewTarget, setViewTarget) = UseState<CareerViewTarget?>(
            Props.Puuid is { Length: > 0 } p ? new CareerViewTarget(p, Props.Preload) : null,
            threadSafe: true);

        // 英雄熟练度筛选与排序状态
        var (masterySearch, setMasterySearch) = UseState(string.Empty, threadSafe: true);
        var (masteryChest, setMasteryChest) = UseState(0, threadSafe: true);
        var (masteryLevel, setMasteryLevel) = UseState(0, threadSafe: true);
        var (masterySort, setMasterySort) = UseState(0, threadSafe: true);

        // 最近队友请求（读驱动）：null = 尚未请求；Nonce 保证重复点击同参数也会重新拉取
        var (teammatesRequest, setTeammatesRequest) = UseState<RecentTeammatesRequest?>(null, threadSafe: true);

        // 战利品弹窗（遮罩在本页）：数量弹窗与批量确认弹窗，载荷由 LootSection 请求
        var (quantityDialog, setQuantityDialog) = UseState<LootQuantityDialog?>(null, threadSafe: true);
        var (openQuantity, setOpenQuantity) = UseState(1, threadSafe: true);
        var (confirmDialog, setConfirmDialog) = UseState<LootConfirmDialog?>(null, threadSafe: true);

        // R6：非渲染数据走 UseRef——请求代际（防串号）与刷新 token 严格递增，
        // 避免 effect/事件闭包固化旧值后 +1 得到重复值而不触发刷新。
        var requestSeqRef = UseRef(0);
        var refreshSeqRef = UseRef(0);
        var teammatesNonceRef = UseRef(0);

        void BumpRefresh()
        {
            refreshSeqRef.Current += 1;
            setRefreshToken(refreshSeqRef.Current);
        }

        // 资源表（读）：客户端未启动时静默失败，保持空表，英雄图标退化为占位块而不是空白。
        var catalogResource = UseResource(
            ct => services.Services.GetRequiredService<GameDataService>().GetAsync(ct),
            deps: new object[] { 0 });
        GameDataCatalog catalog = catalogResource switch
        {
            AsyncValue<GameDataCatalog>.Data d => d.Value,
            _ => GameDataCatalog.Empty,
        };

        // 外部跳转（最近队友卡片、战绩详情玩家名等入口）：路由参数驱动进入"查看他人"。
        // deps 用路由参数而不是 props 对象本身：props record 值相等时（同目标重复跳转）不重复加载。
        UseEffect(
            () =>
            {
                setQueueFilter(0);
                setViewTarget(Props.Puuid is { Length: > 0 } puuid ? new CareerViewTarget(puuid, Props.Preload) : null);
                setTab(CareerTab.Matches);
                return static () => { };
            },
            Props.Puuid ?? "",
            Props.Preload);

        // 召唤师变更时清空队友缓存、重置队列筛选与战利品选中
        UseEffect(
            () =>
            {
                setQueueFilter(0);
                setTeammatesRequest(null);
                return static () => { };
            },
            summoner?.Puuid ?? string.Empty);

        // 召唤师状态流变化（如客户端从未登录变为登录）时自动拉取刷新
        UseEffect(
            () =>
            {
                var sub = summonerService.CurrentChanged.Subscribe(profile =>
                {
                    if (viewTarget is null && profile is not null)
                    {
                        // 时间戳做 token：不依赖闭包捕获，每次必不同（对应旧版 setRefreshToken(TickCount)）
                        setRefreshToken(Environment.TickCount);
                    }
                });
                return () => sub.Dispose();
            },
            viewTarget?.Puuid ?? string.Empty);

        // 对局结束与新对局开始自动刷新
        UseEffect(
            () =>
            {
                if (lifecycle is null) return static () => { };
                var sub1 = lifecycle.GameEnded.Subscribe(time =>
                    _ = CareerLoaders.DelayedRefreshAsync(refreshSeqRef, setRefreshToken));
                var sub2 = lifecycle.NewGameStarted.Subscribe(time => BumpRefresh());
                return () =>
                {
                    sub1.Dispose();
                    sub2.Dispose();
                };
            },
            0);

        // 首屏与刷新共用一条链路：查看目标、refreshToken 或对局加载数量变化即重新取数，
        // token > 0 时强制绕过排位与战绩缓存；代际计数在加载入口递增、回写前校验。
        // 首屏与刷新共用一条链路（实现见 CareerLoaders.BeginCareerLoad）
        string loadKey = $"{viewTarget?.Puuid ?? string.Empty}|{refreshToken}|{services.Config.Functions.CareerGamesNumber}";
        UseEffect(
            () => CareerLoaders.BeginCareerLoad(
                services,
                summonerService,
                careerService,
                matchService,
                viewTarget,
                refreshToken,
                requestSeqRef,
                setSummoner,
                setRanked,
                setMatches,
                setLoot,
                setMastery,
                setLoading),
            loadKey);

        // "已复制"提示 1.5 秒后自动消失。
        UseEffect(
            () =>
            {
                if (!copied)
                {
                    return static () => { };
                }

                var cancellation = new CancellationTokenSource();
                _ = CareerLoaders.ResetCopiedAsync(setCopied, cancellation.Token);
                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                };
            },
            copied ? 1 : 0);

        // 最近队友（读）：点击「最近队友」按钮时以 nonce 请求驱动 UseResource 拉取
        //（R7：替代原 Task.Run + 手写 loadingTeammates；nonce 保证重复点击同参数也会重新拉）。
        var teammatesResource = UseResource(
            async ct => teammatesRequest is { } req
                ? await careerService.GetRecentTeammatesAsync(req.Puuid, req.GameIds, req.Puuid, ct)
                : null,
            // deps 用标量哨兵（可空 record 不能直接进 object[]）：puuid+nonce 唯一标识一次请求
            deps: new object[] { teammatesRequest?.Puuid ?? string.Empty, teammatesRequest?.Nonce ?? -1 },
            options: new ResourceOptions(CacheKey: "career/recent-teammates"));
        RecentTeammatesResult? teammates = teammatesResource switch
        {
            AsyncValue<RecentTeammatesResult?>.Data d => d.Value,
            _ => null,
        };
        bool loadingTeammates = teammatesResource is AsyncValue<RecentTeammatesResult?>.Loading;

        void LoadTeammates()
        {
            if (summoner is null || matches.Count == 0 || loadingTeammates)
            {
                return;
            }

            teammatesNonceRef.Current += 1;
            setTeammatesRequest(new RecentTeammatesRequest(
                summoner.Puuid,
                matches.Select(m => m.GameId).ToArray(),
                teammatesNonceRef.Current));
        }

        bool isViewingOther = viewTarget is not null;
        var (filterLabels, filterModes) = UseMemo(() => CareerLoaders.BuildFilterLabels(matches), matches);
        int filter = queueFilter >= 0 && queueFilter < filterModes.Length ? queueFilter : 0;
        string targetMode = filterModes[filter];
        MatchSummary[] visibleMatches = string.IsNullOrEmpty(targetMode)
            ? [.. matches]
            : [.. matches.Where(match => string.Equals(match.QueueName, targetMode, StringComparison.Ordinal))];

        RecentFormSummary summary = RecentFormSummary.Compute(matches);

        // 命令实现集中 CareerCommands
        void OnClickTeammate(RecentTeammate tm) =>
            CareerCommands.OnClickTeammate(tm, setQueueFilter, setViewTarget, setTab, nav);

        void BackToMyCareer() => CareerCommands.BackToMyCareer(setQueueFilter, setViewTarget, setTab);

        void GoToHistory() => CareerCommands.GoToHistory(nav, summoner);

        void OpenMatchInSearch(MatchSummary match) => CareerCommands.OpenMatchInSearch(nav, summoner, match);

        // 查看他人时强制停留在「生涯战绩」（旧版 watch(isViewingOther) 的等效实现）
        CareerTab effectiveTab = isViewingOther ? CareerTab.Matches : tab;

        // 「生涯战绩」页签（组装在 CareerMatchesSection）
        Element MatchesTab() => CareerMatchesSection.Render(
            ranked,
            summary,
            catalog,
            palette,
            filterLabels,
            filter,
            queueFilter,
            loading,
            visibleMatches,
            teammates,
            loadingTeammates,
            index =>
            {
                if (index != queueFilter)
                {
                    setQueueFilter(index);
                }
            },
            LoadTeammates,
            OnClickTeammate,
            OpenMatchInSearch);

        Element header = summoner is null
            ? CareerVisuals.EmptyHeader(loading)
            : CareerProfileHeader.Render(
                summoner,
                palette,
                copied,
                onCopy: () => CareerCommands.CopyRiotId(summoner, setCopied),
                onRefresh: BumpRefresh,
                onHistory: GoToHistory,
                isViewingOther: isViewingOther,
                onBackToSelf: BackToMyCareer);

        // 页签栏：仅在查看自己时显示（查看他人时直接展示战绩内容，不显示「生涯战绩」等页签）
        Element tabBar = summoner is null || isViewingOther
            ? Empty()
            : CareerVisuals.TabBar(effectiveTab, setTab);

        // D1：战利品/熟练度读取失败（null）给提示条；成功但为空由子视图给普通空态。
        Element lootFailureHint = loot is null && !loading
            ? CareerVisuals.FailureHint("获取战利品失败，请确认客户端已连接", BumpRefresh)
            : Empty();
        Element masteryFailureHint = mastery is null && !loading
            ? CareerVisuals.FailureHint("获取英雄熟练度失败，请确认客户端已连接", BumpRefresh)
            : Empty();

        Element content = effectiveTab switch
        {
            CareerTab.Loot => VStack(12,
                lootFailureHint,
                Component<CareerLootSection, CareerLootSectionProps>(
                    new CareerLootSectionProps(
                        loot,
                        palette,
                        BumpRefresh,
                        // 数量弹窗带初值打开；确认弹窗存储时重包 onConfirm（先关窗再执行，对齐旧版闭包）
                        dialog => { setQuantityDialog(dialog); setOpenQuantity(Math.Max(dialog.MaxQuantity, 0)); },
                        dialog => setConfirmDialog(dialog with { OnConfirm = () => { setConfirmDialog(null); dialog.OnConfirm(); } })))),
            CareerTab.Mastery => mastery is null
                ? masteryFailureHint
                : ChampionMasteryView.Render(
                    mastery,
                    catalog,
                    palette,
                    onRefresh: BumpRefresh,
                    searchQuery: masterySearch,
                    onSearchChanged: setMasterySearch,
                    chestFilterIndex: masteryChest,
                    onChestFilterChanged: setMasteryChest,
                    levelFilterIndex: masteryLevel,
                    onLevelFilterChanged: setMasteryLevel,
                    sortIndex: masterySort,
                    onSortChanged: setMasterySort),
            _ => MatchesTab(),
        };

        const double MaxContentWidth = 1000;
        Element body = VStack(16, header, tabBar, content)
            .MaxWidth(MaxContentWidth)
            .HAlign(HorizontalAlignment.Center);

        // 弹窗层（实现见 CareerLootOverlay）：单件数量弹窗 / 批量确认弹窗，覆盖在正文之上
        Element? overlay = CareerLootOverlay.Render(
            quantityDialog, openQuantity, confirmDialog, palette,
            setOpenQuantity, setQuantityDialog, setConfirmDialog);

        // 客户端未启动：整页替换为离线占位，与战绩查询、对局信息同一套表达。
        // 位置必须在本方法末尾：所有 UseState/UseEffect 都得按固定顺序跑完，
        // 提前 return 会让 hook 数量随连接态变化，Reactor 直接判状态错位。
        if (connState.Status != LcuConnectionStatus.Connected)
        {
            return LcuOfflineState.Render();
        }

        Element pageBody = overlay is null
            ? body
            : Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                [body, overlay]);

        return ScrollViewer(
            Border(pageBody)
                .Padding(24)
                .HAlign(HorizontalAlignment.Stretch));
    }

}
