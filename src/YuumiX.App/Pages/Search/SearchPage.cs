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
/// <b>为什么状态流要订阅而不是自己拼文案</b>：<c>MatchServiceStatus.Message</c> 里区分了
/// 「正在拉取第 N–M 场」与「正在读取对局 X 的详情」，列表加载与详情加载共用同一条流，
/// 因此这里只把**失败**透出到列表区的占位里，加载中的提示由骨架卡与详情面板各自承担。
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
    /// 左列表宽度（DIP）。卡片 172 + <c>ListView</c> 项容器与滚动条的余量：
    /// 给足 18 的差值可以避免出现横向滚动条（项容器自带内边距）。
    /// </summary>
    private const double ListWidth = 190;

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

        var (summoner, setSummoner) = UseState<SummonerProfile?>(null, threadSafe: true);
        var (summonerResolved, setSummonerResolved) = UseState(false, threadSafe: true);
        var (matches, setMatches) = UseState(EmptyMatches, threadSafe: true);
        var (status, setStatus) = UseState(MatchServiceStatus.Idle, threadSafe: true);
        var (detail, setDetail) = UseState<MatchDetail?>(null, threadSafe: true);
        var (selectedGameId, setSelectedGameId) = UseState(0L, threadSafe: true);
        var (pageIndex, setPageIndex) = UseState(0, threadSafe: true);
        var (hasMore, setHasMore) = UseState(false, threadSafe: true);
        var (filterIndex, setFilterIndex) = UseState(0, threadSafe: true);
        var (requestVersion, setRequestVersion) = UseState(0, threadSafe: true);
        var (notice, setNotice) = UseState<string?>(null, threadSafe: true);
        var (uploadOn, setUploadOn) = UseState(services.Config.Functions.UploadEnabled, threadSafe: true);

        // R6：非响应式数据走 UseRef——
        // 搜索框草稿刻意不进 UseState：受控文本框在重渲染时会重置光标位置（类字段时代的既有决定）；
        // 上传去重集合只在 effect 里读写；requestSeq 让"踢一次重取"的 token 严格递增，
        // 避免 effect/事件闭包固化旧 requestVersion 后 +1 得到重复值而不触发刷新。
        var draftRef = UseRef<string?>(null);
        var uploadedGameIdsRef = UseRef<HashSet<long>?>(null);
        uploadedGameIdsRef.Current ??= [];
        var requestSeqRef = UseRef(0);

        // 没配服务器地址时整个上传入口都不出现（对齐旧 Search.vue 的 v-if）：
        // 给了开关却没有地址，点了只会得到一串"未配置"的报错。
        bool uploadConfigured = !string.IsNullOrWhiteSpace(services.Config.General.UploadApiUrl);
        var uploadQueue = services.Services.GetRequiredService<UploadQueueService>();

        // R7：搜索命令（写操作）——解析目标 + 写历史 + 换人；IsPending 接管"查询中"按钮态，
        // 历史列表由 InvalidateKeys 驱动下方 UseResource 重拉。
        var searchMutation = UseMutation<SearchTargetInput, SummonerProfile?>(
            mutator: (input, ct) => SearchLoaders.ResolveTargetAsync(summonerService, historyStore, input, ct),
            options: new MutationOptions<SearchTargetInput, SummonerProfile?>(
                OnOptimistic: _ => setNotice(null),
                OnSuccess: (found, input) =>
                {
                    if (found is null)
                    {
                        setNotice(input.NotFoundMessage);
                        return;
                    }

                    if (!string.IsNullOrEmpty(found.RiotId))
                    {
                        draftRef.Current = found.RiotId;
                    }

                    // 先清空列表再换人：否则新召唤师的头像旁边会短暂挂着上一个人的战绩
                    setMatches(EmptyMatches);
                    setSelectedGameId(input.TargetGameId);
                    setPageIndex(0);
                    setSummoner(found);
                    setSummonerResolved(true);

                    // 搜到的是同一个人时取数依赖键不变（puuid、页号都没动），必须显式触发一次重取
                    requestSeqRef.Current += 1;
                    setRequestVersion(requestSeqRef.Current);
                },
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

        // 对局结束自动静默刷新战绩
        UseEffect(
            () =>
            {
                if (lifecycle is null) return static () => { };
                var sub = lifecycle.GameEnded.Subscribe(time =>
                    _ = SearchEffects.DelayedRefreshAsync(requestSeqRef, setRequestVersion));
                return () => sub.Dispose();
            },
            0);

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
                        setSummoner(null);
                        setSummonerResolved(false);
                        requestSeqRef.Current += 1;
                        setRequestVersion(requestSeqRef.Current);
                    }
                });
                return () => subscription.Dispose();
            },
            0);

        // 资源映射表（读）：客户端没启动时静默失败，保持空表，图标退化为占位块而不是空白。
        var catalogResource = UseResource(
            ct => services.Services.GetRequiredService<GameDataService>().GetAsync(ct),
            deps: new object[] { 0 });
        GameDataCatalog catalog = catalogResource switch
        {
            AsyncValue<GameDataCatalog>.Data d => d.Value,
            _ => GameDataCatalog.Empty,
        };

        // 状态流：一次订阅，覆盖列表与详情两类请求（消息里自带是哪一个）。
        UseEffect(
            () =>
            {
                var subscription = matchService.Status.Subscribe(setStatus);
                return () => subscription.Dispose();
            },
            0);

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

                    _ = SearchEffects.RunRoutedSearchAsync(searchMutation, Props.Query, Props.Puuid, Props.GameId ?? 0);
                }
                return static () => { };
            },
            Props.Query ?? "",
            Props.Puuid ?? "",
            Props.GameId ?? 0L);

        // 首屏 / 搜索 / 翻页 / 刷新 共用一个入口（实现见 SearchEffects.BeginHistoryLoad）：
        // setSummoner 会改变依赖键，下一轮再进来拉战绩（因此不会重复请求）。
        string historyKey = $"{summoner?.Puuid}|{pageIndex}|{requestVersion}|{connState.Status}";
        UseEffect(
            () => SearchEffects.BeginHistoryLoad(
                services,
                matchService,
                summonerService,
                summoner,
                pageIndex,
                setSummoner,
                setSummonerResolved,
                setMatches,
                setHasMore),
            historyKey);

        // 筛选：结果按（源列表, 筛选序号）UseMemo 缓存引用——
        // 卡片元素按"列表引用"判等，每帧新数组会让卡片反复重建（改造前踩过的渲染自激）。
        string[] filterLabels = UseMemo(() => SearchFilters.BuildLabels(matches), matches);
        int effectiveFilter = filterIndex < filterLabels.Length ? filterIndex : 0;
        IReadOnlyList<MatchSummary> visible = UseMemo(
            () => SearchFilters.Apply(matches, filterLabels, effectiveFilter),
            matches,
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

        // 详情：按当前选中的 gameId 加载（实现见 SearchEffects.BeginDetailLoad）。
        UseEffect(
            () => SearchEffects.BeginDetailLoad(services, matchService, currentGameId, summoner?.Puuid, setDetail),
            currentGameId);

        // 载入一页后补传本页对局（对齐旧 Search.vue：翻页即补传，服务端已有会返回 exists）。
        // 依赖键取 matches 引用：换页或刷新会拿到新引用，纯筛选不会（筛选不改变要传的集合）。
        UseEffect(
            () =>
            {
                if (uploadOn && uploadConfigured)
                {
                    SearchEffects.RunAutoUpload(services, matches, uploadedGameIdsRef.Current, summoner?.Puuid, setNotice);
                }

                return static () => { };
            },
            matches,
            uploadOn);

        // 列表卡片：ScrollViewer 自适应滚动并支持完全自定义的选中边框与胜负色，
        // 彻底消除 ListView 原生控件的蓝色选中矩形和焦点边框干扰。
        // 每项各自重新 Provide 上下文，保证卡片内的 LcuImage 正确获取服务容器。
        Element list = visible.Count == 0
            ? SearchChrome.SkeletonList(status, summonerResolved, summoner is not null, palette)
            : ScrollViewer(VStack(0, visible.Select(summary =>
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
                      .Provide(AppHost.Context, services);
              }).ToArray()).Width(MatchCardView.CardWidth))
              .HAlign(HorizontalAlignment.Left);

        // 左列：列表吃剩余高度（自身滚动），分页条固定在底部。
        // Grid 的签名是 Grid(columns, rows, children)——第一个数组是**列**，写反会静默错乱（历史踩坑）。
        Element leftColumn = Grid(
                [GridSize.Star()],
                [GridSize.Star(), GridSize.Auto],
                [
                    list.Grid(row: 0, column: 0),
                    SearchChrome.Pager(pageIndex, hasMore, setPageIndex).Grid(row: 1, column: 0),
                ]);

        void OnCopyGameId(long gameId) => SearchEffects.CopyGameId(gameId, setNotice);

        void OnClickPlayerPuuid(string puuid, SummonerProfile? preloaded) =>
            nav.Replace(new AppRoute.CareerRoute(puuid, preloaded));

        void OnClickPlayerSummonerId(long id) =>
            _ = searchMutation.RunAsync(new SearchTargetInput(null, null, id, 0));

        Element content = Grid(
            [GridSize.Px(ListWidth), GridSize.Star()],
            [GridSize.Star()],
            [
                leftColumn.Grid(row: 0, column: 0),
                MatchDetailView.Render(
                    detail,
                    current,
                    catalog,
                    palette,
                    currentSearchPuuid: summoner?.Puuid,
                    onCopyGameId: OnCopyGameId,
                    onClickPlayerPuuid: OnClickPlayerPuuid,
                    onClickPlayerSummonerId: OnClickPlayerSummonerId)
                .Grid(row: 0, column: 1),
            ]);

        // 提交搜索：名字空时只更新提示，不清空当前已显示的战绩（否则一次手滑就把列表清空）。
        void SubmitSearch(string? targetPuuid = null, long targetGameId = 0)
        {
            string query = (draftRef.Current ?? string.Empty).Trim();
            if (query.Length == 0 && string.IsNullOrWhiteSpace(targetPuuid))
            {
                setNotice("请输入召唤师名称（形如 名字#TAG）");
                return;
            }

            _ = searchMutation.RunAsync(new SearchTargetInput(query, targetPuuid, null, targetGameId));
        }

        void OnSelectHistory(string item)
        {
            draftRef.Current = item;
            SubmitSearch();
        }

        void OnCareer()
        {
            if (summoner is not null) nav.Replace(new AppRoute.CareerRoute(summoner.Puuid, summoner));
        }

        void OnSearchButton()
        {
            // 换人即重置上传去重集合：新召唤师的对局与上一人无关
            uploadedGameIdsRef.Current.Clear();
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

        return Grid(
                [GridSize.Star()],
                [GridSize.Auto, GridSize.Star()],
                [
                    Component<SearchToolbar, SearchToolbarProps>(
                        new SearchToolbarProps(
                            draftRef.Current ?? summoner?.RiotId ?? string.Empty,
                            draft => draftRef.Current = draft,
                            historyList,
                            OnSelectHistory,
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
                            enabled => SearchEffects.ApplyUploadToggle(services, uploadQueue, enabled, setUploadOn, setNotice)))
                        .Grid(row: 0, column: 0),
                    content.Grid(row: 1, column: 0),
                ])
            .Padding(12)
            .HAlign(HorizontalAlignment.Stretch);
    }
}
