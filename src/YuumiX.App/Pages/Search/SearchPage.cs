using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Navigation;
using YuumiX.App.Theming;
using YuumiX.Core;
using YuumiX.Core.Features.GameLifecycle;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;
using YuumiX.Core.Upload;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 战绩查询页：顶部搜索栏 + 左侧对局列表 + 右侧详情表格，数据全部来自 LCU
///（对应原 <c>Search.vue</c> 与 <c>MatchDetailPanel.vue</c>）。
/// 本页只做状态编排（R14）：视觉构件在 <see cref="SearchChrome"/>，筛选纯函数在
/// <see cref="SearchFilters"/>，异步辅助与 effect 回调体在 <see cref="SearchLoaders"/>。
///
/// <b>线程</b>：服务在后台线程发布状态、异步返回结果，所有 <c>UseState</c> 都必须带
/// <c>threadSafe: true</c>（ADR-07）。
///
/// <b>规模</b>：约 400 行（R14 过渡期，超 300 理由：hooks 编排必须整块留在 Render 内保证调用顺序）。
/// </summary>
/// <summary>战绩查询页 props：路由参数（R8）。Query/Puuid 非空 = 自动发起查询；GameId &gt; 0 时选中该局。</summary>
public sealed record SearchPageProps(string? Query = null, string? Puuid = null, long? GameId = null);

public sealed class SearchPage : Component<SearchPageProps>
{
    /// <summary>
    /// 左列表宽度（DIP）。卡片 202 + <c>ListView</c> 项容器与滚动条的余量：
    /// 给足 18 的差值可以避免出现横向滚动条（项容器自带内边距），容纳长模式名（如海克斯大乱斗）。
    /// </summary>
    /// <summary>左列战绩列表宽度（对齐 4px 网格与历史记录列表宽度）。</summary>
    internal const double ListWidth = 220;

    /// <summary>空战绩列表共享实例。</summary>
    private static readonly IReadOnlyList<MatchSummary> EmptyMatches = [];

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var matchService = services.Services.GetRequiredService<IMatchService>();
        var summonerService = services.Services.GetRequiredService<ISummonerService>();
        var historyStore = services.Services.GetRequiredService<SearchHistoryStore>();
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

        var (lang, setLang) = UseState(services.I18n.CurrentLanguage, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = services.I18n.CurrentLanguageChanged.Subscribe(setLang);
                return () => subscription.Dispose();
            },
            0);

        var langRef = UseRef(lang);
        langRef.Current = lang;
        var searchSeqRef = UseRef(0);
        var initialPageCancellation = UseMemo(() => new CancellationTokenSource(), 0);
        var pageCancellation = UseRef(initialPageCancellation);
        var refreshCancellation = UseRef<CancellationTokenSource?>(null);
        var noticeVersion = UseRef(0);
        var noticeCancellation = UseRef<CancellationTokenSource?>(null);
        var initialNoticeLock = UseMemo(() => new object(), 0);
        var noticeLock = UseRef(initialNoticeLock);
        var clearNoticeRef = UseRef<Action?>(null);
        var refreshOnReturnRef = UseRef<Action?>(null);
        var (clearHistoryOpen, setClearHistoryOpen) = UseState(false);
        var navigatedFromRef = UseRef<Action<NavigatedFromContext>?>(null);
        navigatedFromRef.Current ??= _ =>
        {
            setClearHistoryOpen(false);
            searchSeqRef.Current++;
            pageCancellation.Current.Cancel();
            refreshCancellation.Current?.Cancel();
            lock (noticeLock.Current)
            {
                noticeVersion.Current++;
                noticeCancellation.Current?.Cancel();
            }
            clearNoticeRef.Current?.Invoke();
        };
        var navigatedToRef = UseRef<Action<NavigatedToContext>?>(null);
        navigatedToRef.Current ??= _ =>
        {
            if (pageCancellation.Current.IsCancellationRequested)
            {
                pageCancellation.Current.Dispose();
                pageCancellation.Current = new CancellationTokenSource();
                refreshOnReturnRef.Current?.Invoke();
            }
            var curLang = services.I18n.CurrentLanguage;
            if (langRef.Current != curLang)
            {
                setLang(curLang);
            }
        };
        UseNavigationLifecycle(onNavigatedTo: navigatedToRef.Current, onNavigatedFrom: navigatedFromRef.Current);

        var (summoner, setSummoner) = UseState<SummonerProfile?>(null, threadSafe: true);
        var (selectedGameId, setSelectedGameId) = UseState(0L, threadSafe: true);
        var (pageIndex, setPageIndex) = UseState(0, threadSafe: true);
        var (detailVersion, setDetailVersion) = UseState(0, threadSafe: true);
        var (filterIndex, setFilterIndex) = UseState(0, threadSafe: true);
        var (requestVersion, setRequestVersion) = UseState(0, threadSafe: true);
        var (notice, setNoticeState) = UseState<string?>(null, threadSafe: true);
        void SetNotice(string? value, int timeout = 0)
        {
            int version;
            CancellationToken token = default;
            lock (noticeLock.Current)
            {
                if (pageCancellation.Current.IsCancellationRequested) return;
                noticeCancellation.Current?.Cancel();
                noticeCancellation.Current?.Dispose();
                noticeCancellation.Current = timeout > 0
                    ? CancellationTokenSource.CreateLinkedTokenSource(pageCancellation.Current.Token)
                    : null;
                if (noticeCancellation.Current is not null) token = noticeCancellation.Current.Token;
                version = ++noticeVersion.Current;
                setNoticeState(value);
            }
            if (timeout > 0)
                _ = SearchEffects.ClearNoticeLaterAsync(() =>
                {
                    lock (noticeLock.Current)
                        if (noticeVersion.Current == version) setNoticeState(null);
                }, timeout, token, services.LoggerFactory.CreateLogger("YuumiX.App.Pages.Search"));
        }
        void SetPersistentNotice(string? value) => SetNotice(value);
        clearNoticeRef.Current = () => setNoticeState(null);
        UseEffect(() => () =>
        {
            pageCancellation.Current.Cancel();
            refreshCancellation.Current?.Cancel();
            lock (noticeLock.Current)
            {
                noticeCancellation.Current?.Cancel();
                noticeCancellation.Current?.Dispose();
                noticeCancellation.Current = null;
            }
            refreshCancellation.Current?.Dispose();
            refreshCancellation.Current = null;
            pageCancellation.Current.Dispose();
        }, 0);
        var (uploadOn, setUploadOn) = UseState(services.Config.Functions.UploadEnabled, threadSafe: true);

        // R6：非响应式数据走 UseRef——
        // 搜索框草稿刻意不进 UseState：受控文本框在重渲染时会重置光标位置（类字段时代的既有决定）；
        // 上传去重集合只在 effect 里读写；requestSeq 让"踢一次重取"的 token 严格递增，
        // 避免 effect/事件闭包固化旧 requestVersion 后 +1 得到重复值而不触发刷新。
        var draftRef = UseRef<string?>(null);
        var uploadedGameIdsRef = UseRef<HashSet<long>?>(null);
        uploadedGameIdsRef.Current ??= [];
        var requestSeqRef = UseRef(0);
        refreshOnReturnRef.Current = () => { requestSeqRef.Current++; setRequestVersion(requestSeqRef.Current); };

        // 没配服务器地址时整个上传入口都不出现（对齐旧 Search.vue 的 v-if）：
        // 给了开关却没有地址，点了只会得到一串"未配置"的报错。
        bool uploadConfigured = !string.IsNullOrWhiteSpace(services.Config.General.UploadApiUrl);
        var uploadQueue = services.Services.GetRequiredService<UploadQueueService>();

        // R7：搜索命令（写操作）——解析目标 + 写历史 + 换人；IsPending 接管"查询中"按钮态，
        // 历史列表由 InvalidateKeys 驱动下方 UseResource 重拉。
        var searchMutation = UseMutation<SearchTargetInput, SummonerProfile?>(
            mutator: (input, ct) => SearchLoaders.ResolveTargetAsync(summonerService, historyStore, input, ct),
            options: new MutationOptions<SearchTargetInput, SummonerProfile?>(
                OnOptimistic: _ => SetPersistentNotice(null),
                OnSuccess: (found, input) =>
                {
                    if (input.Sequence != searchSeqRef.Current) return;
                    if (found is null)
                    {
                        SetPersistentNotice(input.NotFoundMessage);
                        return;
                    }

                    if (!string.IsNullOrEmpty(found.RiotId))
                    {
                        draftRef.Current = found.RiotId;
                    }

                    // 先清空列表再换人：否则新召唤师的头像旁边会短暂挂着上一个人的战绩
                    setSelectedGameId(input.TargetGameId);
                    setPageIndex(0);
                    setSummoner(found);

                    // 搜到的是同一个人时取数依赖键不变（puuid、页号都没动），必须显式触发一次重取
                    requestSeqRef.Current += 1;
                    setRequestVersion(requestSeqRef.Current);
                },
                OnError: (_, input) => { if (input.Sequence == searchSeqRef.Current) SetPersistentNotice(services.I18n.T("search.resolveFailed", "查询召唤师失败")); },
                InvalidateKeys: ["search/history"]));

        void RunSearch(SearchTargetInput input)
        {
            searchSeqRef.Current++;
            refreshCancellation.Current?.Cancel();
            _ = searchMutation.RunAsync(input with { Sequence = searchSeqRef.Current });
        }

        var removeHistoryMutation = UseMutation<string, bool>(
            mutator: async (item, ct) =>
            {
                await historyStore.RemoveAsync(item, ct).ConfigureAwait(false);
                return true;
            },
            options: new MutationOptions<string, bool>(
                InvalidateKeys: ["search/history"]));

        var clearHistoryMutation = UseMutation<bool, bool>(
            mutator: async (_, ct) =>
            {
                await historyStore.ClearAsync(ct).ConfigureAwait(false);
                return true;
            },
            options: new MutationOptions<bool, bool>(
                InvalidateKeys: ["search/history"]));

        // R7：搜索历史（读）——挂载拉一次；搜索成功后由 mutation 的 InvalidateKeys 失效重拉
        var historyResource = UseResource(
            ct => historyStore.GetHistoryAsync(ct),
            deps: new object[] { 0 },
            options: new ResourceOptions(CacheKey: "search/history"));
        IReadOnlyList<string> historyList = historyResource switch
        {
            AsyncValue<IReadOnlyList<string>>.Data d => d.Value,
            _ => [],
        };

        var (connState, setConnState) = UseState(lcu.CurrentState, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = lcu.States.Subscribe(state =>
                {
                    setConnState(state);
                    if (state.Status == LcuConnectionStatus.Connected)
                    {
                        // 客户端连接就绪时，重置召唤师解析状态，触发重新加载当前登录玩家与首屏战绩
                        searchSeqRef.Current++;
                        setSummoner(null);
                        requestSeqRef.Current += 1;
                        setRequestVersion(requestSeqRef.Current);
                    }
                });
                return () => subscription.Dispose();
            },
            0);

        // 资源映射表（读）：客户端没启动时静默失败，保持空表，图标退化为占位块而不是空白。
        //
        // deps 必须带连接态与请求代际（与生涯页同一处坑）：GetAsync 在未连接/资源未就绪时
        // 返回空表且不写缓存，但 UseResource 会把这次的空表当结果留住——deps 恒为 [0]
        // 就再也不会重取，表现为数据正常、装备/英雄图标整列空白。
        var catalogResource = UseResource(
            ct => services.Services.GetRequiredService<GameDataService>().GetAsync(ct),
            deps: new object[] { (int)connState.Status, requestVersion });
        GameDataCatalog catalog = catalogResource switch
        {
            AsyncValue<GameDataCatalog>.Data d => d.Value,
            _ => GameDataCatalog.Empty,
        };

        // 外部跳转（如生涯页点击最近队友、生涯资料卡点击战绩查询）传入的待查询目标：
        // 路由参数驱动（R8），deps 用参数值，同目标重复跳转不重复查询。
        UseEffect(
            () =>
            {
                if (!string.IsNullOrWhiteSpace(Props.Query) || !string.IsNullOrWhiteSpace(Props.Puuid))
                {
                    if (!string.IsNullOrWhiteSpace(Props.Query))
                    {
                        draftRef.Current = Props.Query;
                    }

                    RunSearch(new SearchTargetInput(Props.Query, Props.Puuid, null, Props.GameId ?? 0));
                }
                return static () => { };
            },
            Props.Query ?? "",
            Props.Puuid ?? "",
            Props.GameId ?? 0L);

        // 默认不查询任何人（用户要求）：查询对象只来自显式搜索或路由跳转；
        // summoner 为空时列表保持初始空态，等用户输入。
        string historyCacheKey = SearchEffects.HistoryCacheKey(summoner?.Puuid, pageIndex, (int)connState.Status);
        var historyPageResource = UseResource(
            async ct => new SearchHistoryResult(historyCacheKey,
                await SearchLoaders.GetHistoryAsync(services, matchService, summoner?.Puuid, pageIndex, ct).ConfigureAwait(false)),
            deps: new object[] { summoner?.Puuid ?? "", pageIndex, requestVersion, (int)connState.Status });
        var previousHistory = UseRef<SearchHistoryResult?>(null);
        if (historyPageResource is AsyncValue<SearchHistoryResult>.Data historyData && historyData.Value.Key == historyCacheKey)
            previousHistory.Current = historyData.Value;
        var historyPage = SearchEffects.CurrentHistoryPage(historyPageResource, historyCacheKey, previousHistory.Current);
        IReadOnlyList<MatchSummary> matches = historyPage?.Games ?? EmptyMatches;
        bool hasMore = historyPage?.HasMore ?? false;
        var matchesRef = UseRef(matches);
        matchesRef.Current = matches;
        // 对局结束自动静默刷新战绩
        UseEffect(
            () =>
            {
                if (lifecycle is null) return static () => { };
                var sub = lifecycle.GameEnded.Subscribe(time =>
                {
                    if (pageCancellation.Current.IsCancellationRequested) return;
                    refreshCancellation.Current?.Cancel();
                    refreshCancellation.Current?.Dispose();
                    refreshCancellation.Current = CancellationTokenSource.CreateLinkedTokenSource(pageCancellation.Current.Token);
                    long? prevLatestId = matchesRef.Current.Count > 0 ? matchesRef.Current[0].GameId : null;
                    long? targetGameId = lifecycle.LastEndedGameId;
                    _ = SearchEffects.DelayedRefreshAsync(
                        requestSeqRef,
                        setRequestVersion,
                        () => matchesRef.Current.Count > 0 ? matchesRef.Current[0].GameId : null,
                        prevLatestId,
                        targetGameId, refreshCancellation.Current.Token, services.LoggerFactory.CreateLogger("YuumiX.App.Pages.Search"));
                });
                return () => sub.Dispose();
            },
            0);


        // 筛选：结果按（源列表, 语言, 筛选序号）UseMemo 缓存引用——
        // 卡片元素按"列表引用"判等，每帧新数组会让卡片反复重建（改造前踩过的渲染自激）。
        string[] filterLabels = UseMemo(() => SearchFilters.BuildLabels(matches), matches, lang);
        int effectiveFilter = filterIndex < filterLabels.Length ? filterIndex : 0;
        IReadOnlyList<MatchSummary> visible = UseMemo(
            () => SearchFilters.Apply(matches, filterLabels, effectiveFilter),
            matches,
            filterLabels,
            effectiveFilter);

        int index = 0;
        if (selectedGameId > 0)
        {
            int found = SearchFilters.IndexOf(visible, selectedGameId);
            index = found >= 0 ? found : 0;
        }
        else if (selectedGameId == -1)
        {
            index = -1;
        }

        MatchSummary? current = (index >= 0 && index < visible.Count) ? visible[index] : null;
        long currentGameId = current?.GameId ?? 0;

        var detailResource = UseResource(
            ct => SearchLoaders.GetDetailAsync(matchService, currentGameId, summoner?.Puuid, ct),
            deps: new object[] { currentGameId, summoner?.Puuid ?? "", detailVersion });
        var rawDetail = detailResource is AsyncValue<MatchDetail?>.Data detailData ? detailData.Value : null;
        var enrichmentResource = UseResource(
            ct => SearchLoaders.LoadParticipantRanksAndPrivacyAsync(services, rawDetail, ct),
            deps: new object[] { currentGameId, summoner?.Puuid ?? "", detailVersion, rawDetail is not null });
        var detail = enrichmentResource is AsyncValue<SearchEnrichmentResult>.Data enriched ? enriched.Value.Detail : rawDetail;

        // 载入一页后补传本页对局（对齐旧 Search.vue：翻页即补传，服务端已有会返回 exists）。
        // 依赖键只取 matches 引用：换页或刷新会拿到新引用，纯筛选不会。勾选开关本身不应触发当前已有对局的上传。
        UseEffect(
            () =>
            {
                if (services.Config.Functions.UploadEnabled && uploadConfigured)
                {
                    SearchEffects.RunAutoUpload(services, matches, uploadedGameIdsRef.Current, summoner?.Puuid, SetNotice, pageCancellation.Current.Token);
                }

                return static () => { };
            },
            matches, requestVersion);

        // 列表卡片：ScrollViewer 自适应滚动并支持完全自定义的选中边框与胜负色，
        // 彻底消除 ListView 原生控件的蓝色选中矩形和焦点边框干扰。
        // 每项各自重新 Provide 上下文，保证卡片内的 LcuImage 正确获取服务容器。
        // 列表固定以 10 局为基准进行 Star 等分：
        // 达到 10 局时均匀占满全部垂直可用高度；不足 10 局时依然保持 1/10 等分行高，下方自然留白。
        int slotCount = Math.Max(10, visible.Count);
        var matchRows = new GridSize[slotCount];
        Array.Fill(matchRows, GridSize.Star());

        Element list = visible.Count == 0
            ? SearchChrome.ResourceState(historyPageResource, historyPage is not null, matches.Count > 0, palette, () => { requestSeqRef.Current++; setRequestVersion(requestSeqRef.Current); }, services.I18n)
            : Grid(
                [GridSize.Star()],
                matchRows,
                visible.Select((summary, i) =>
                {
                    bool isSelected = summary.GameId == currentGameId;
                    return MatchCardView.Render(
                        summary,
                        palette,
                        isSelected: isSelected,
                        onSelect: () =>
                        {
                            if (summary.GameId != selectedGameId) setSelectedGameId(summary.GameId);
                        })
                        .WithKey($"match_{summary.GameId}")
                        .Grid(row: i, column: 0)
                        .Provide(AppHost.Context, services);
                }).ToArray())
              .VAlign(VerticalAlignment.Stretch)
              .HAlign(HorizontalAlignment.Stretch);

        // 左列：列表吃剩余高度（自身由 10 局网格占满），分页条固定在底部。
        // Grid 的签名是 Grid(columns, rows, children)——第一个数组是**列**，写反会静默错乱（历史踩坑）。
        Element leftColumn = Grid(
                [GridSize.Star()],
                [GridSize.Star(), GridSize.Auto],
                [
                    list.Grid(row: 0, column: 0),
                    SearchChrome.Pager(pageIndex, hasMore, setPageIndex).Grid(row: 1, column: 0),
                ])
            .VAlign(VerticalAlignment.Stretch)
            .HAlign(HorizontalAlignment.Stretch);

        void OnCopyGameId(long gameId) => SearchEffects.CopyGameId(gameId, SetNotice);

        void OnClickPlayerPuuid(string puuid, SummonerProfile? preloaded) =>
            nav.Replace(new AppRoute.CareerRoute(puuid, preloaded));

        void OnClickPlayerSummonerId(long id) =>
            RunSearch(new SearchTargetInput(null, null, id, 0));

        Element content = summoner is null
            ? SearchChrome.SearchGuideEmpty(historyList, OnSelectHistory, OnRemoveHistory, services.I18n)
            : Grid(
                [GridSize.Px(ListWidth), GridSize.Star()],
                [GridSize.Star()],
                [
                    leftColumn
                        .Margin(0, 0, 8, 0)
                        .Grid(row: 0, column: 0),
                    (currentGameId > 0 && detailResource is AsyncValue<MatchDetail?>.Error
                        ? SearchChrome.RetryHint(services.I18n.T("search.detailFailed", "读取对局详情失败"), services.I18n.T("search.retry", "重试"), () => setDetailVersion(detailVersion + 1))
                        : currentGameId > 0 && detailResource is AsyncValue<MatchDetail?>.Loading
                        ? SearchChrome.Hint(services.I18n.T("search.detailLoading", "正在读取对局详情…"))
                        : Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()], [
                            (enrichmentResource is AsyncValue<SearchEnrichmentResult>.Error ||
                                enrichmentResource is AsyncValue<SearchEnrichmentResult>.Data { Value.HasFailures: true }
                                ? SearchChrome.RetryHint(services.I18n.T("search.enrichmentFailed", "段位和隐私状态读取失败"), services.I18n.T("search.retry", "重试"), () => setDetailVersion(detailVersion + 1))
                                : Empty()).Grid(row: 0, column: 0),
                            MatchDetailView.Render(
                                detail,
                                current,
                                catalog,
                                palette,
                                currentSearchPuuid: summoner.Puuid,
                                onCopyGameId: OnCopyGameId,
                                onClickPlayerPuuid: OnClickPlayerPuuid,
                                onClickPlayerSummonerId: OnClickPlayerSummonerId).Grid(row: 1, column: 0)]))
                    .Grid(row: 0, column: 1),
                ])
                .VAlign(VerticalAlignment.Stretch)
                .HAlign(HorizontalAlignment.Stretch);

        // 提交搜索：名字空时只更新提示，不清空当前已显示的战绩（否则一次手滑就把列表清空）。
        void SubmitSearch(string? targetPuuid = null, long targetGameId = 0)
        {
            string query = (draftRef.Current ?? string.Empty).Trim();
            if (query.Length == 0 && string.IsNullOrWhiteSpace(targetPuuid))
            {
                SetPersistentNotice("请输入召唤师名称（形如 名字#TAG）");
                return;
            }

            RunSearch(new SearchTargetInput(query, targetPuuid, null, targetGameId));
        }

        void OnSelectHistory(string item)
        {
            draftRef.Current = item;
            SubmitSearch();
        }

        void OnRemoveHistory(string item)
        {
            _ = removeHistoryMutation.RunAsync(item);
        }

        void OnClearHistory()
        {
            setClearHistoryOpen(true);
        }

        void OnCareer()
        {
            if (summoner is not null) nav.Replace(new AppRoute.CareerRoute(summoner.Puuid, summoner));
        }

        void OnSearchButton()
        {
            // 换人即重置上传去重集合：新召唤师的对局与上一人无关
            uploadedGameIdsRef.Current = [];
            SubmitSearch();
        }

        // 客户端未启动：整页替换为离线占位，与生涯、对局信息同一套表达
        // （旧版 Search.vue 的 <LcuOfflineState v-if="!store.isConnected" />）。
        // 放在末尾：上面的 UseState/UseEffect 必须每次渲染按同一顺序跑完，
        // 提前 return 会让 hook 数量随连接态变化。
        if (connState.Status != LcuConnectionStatus.Connected)
        {
            return LcuOfflineState.Render();
        }

        return (Grid(
                [GridSize.Star()],
                [GridSize.Auto, GridSize.Star()],
                [
                    Component<SearchToolbar, SearchToolbarProps>(
                        new SearchToolbarProps(
                            draftRef.Current ?? string.Empty,
                            draft => draftRef.Current = draft,
                            historyList,
                            OnSelectHistory,
                            OnRemoveHistory,
                            OnClearHistory,
                            OnSearchButton,
                            OnCareer,
                            summoner is not null,
                            filterLabels,
                            effectiveFilter,
                            value =>
                            {
                                if (value != filterIndex)
                                {
                                    setFilterIndex(value);
                                }
                            },
                            notice,
                            searchMutation.IsPending,
                            summoner,
                            uploadConfigured,
                            uploadOn,
                            enabled => SearchEffects.ApplyUploadToggle(services, uploadQueue, enabled, setUploadOn, SetPersistentNotice)))
                        .Grid(row: 0, column: 0),
                    content.Grid(row: 1, column: 0),
                    ConfirmationDialog.Render(
                        services.I18n.T("search.clearHistoryTitle", "清空搜索历史"),
                        Body(services.I18n.T("search.clearHistoryMessage", "确定清空搜索历史吗？")),
                        services.I18n.T("search.clearHistoryConfirm", "清空"),
                        services.I18n.T("search.cancel", "取消"), clearHistoryOpen,
                        () => { setClearHistoryOpen(false); _ = clearHistoryMutation.RunAsync(true); },
                        () => setClearHistoryOpen(false), palette),
                ]) with { RowSpacing = 8 })
            .Padding(12)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch);
    }
}
