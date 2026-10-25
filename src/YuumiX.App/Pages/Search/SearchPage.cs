using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Composition;
using YuumiX.App.Navigation;
using YuumiX.App.Theming;
using YuumiX.Core;
using YuumiX.Core.Assets;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.GameLifecycle;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Parsers;
using YuumiX.Core.Persistence;
using YuumiX.Core.Upload;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 战绩查询页：顶部搜索栏 + 左侧对局列表 + 右侧详情表格，数据全部来自 LCU。
///
/// 对应原 <c>Search.vue</c>（搜索栏 + 左侧战绩卡片流）与 <c>MatchDetailPanel.vue</c>（右侧双方表格）。
///
/// <b>数据通路</b>：<c>ISummonerService</c> 取当前账号的 puuid（或按名字搜索得到的 puuid）→
/// <c>IMatchService.GetMatchHistoryAsync</c> 取一页列表 → 选中项再取
/// <c>GetMatchDetailAsync</c>。三者都是异步且可能因为"客户端没开"直接返回空，
/// 因此页面维护多份状态：召唤师、列表、详情、状态快照、分页与筛选。
///
/// <b>为什么状态流要订阅而不是自己拼文案</b>：<c>MatchServiceStatus.Message</c> 里区分了
/// 「正在拉取第 N–M 场」与「正在读取对局 X 的详情」，列表加载与详情加载共用同一条流。
/// 页面若按 <c>State</c> 全量展示，选中一局后列表上方会突然冒出"正在读取对局 123 的详情"。
/// 因此这里只把**失败**透出到列表区的占位里（见 <see cref="Hint"/>），
/// 加载中的提示由骨架卡与详情面板各自承担。
///
/// <b>线程</b>：服务在后台线程发布状态、异步返回结果，所有 <c>UseState</c> 都必须带
/// <c>threadSafe: true</c>（ADR-07）。
/// </summary>
public sealed class SearchPage : Component
{
    /// <summary>
    /// 左列表宽度（DIP）。卡片 172 + <c>ListView</c> 项容器与滚动条的余量：
    /// 给足 18 的差值可以避免出现横向滚动条（项容器自带内边距）。
    /// </summary>
    private const double ListWidth = 190;

    /// <summary>
    /// 单页条数。每页 10 条对局。
    /// 前两页由 LCU 预拉取 20 局，用户点到第 2 页时触发腾讯 SGP 接口增量查询第 21 局起的数据。
    /// </summary>
    private const int PageSize = 10;

    /// <summary>加载中骨架卡数量：每页 10 张，避免数据到达时整列跳动。</summary>
    private const int SkeletonCount = 10;

    /// <summary>空战绩列表共享实例。</summary>
    private static readonly IReadOnlyList<MatchSummary> EmptyMatches = [];

    private static readonly IReadOnlyList<string> EmptyHistory = [];

    /// <summary>战绩查询游戏模式预置选项（对齐旧版 Search.vue 与客户端主流队列）。</summary>
    private static readonly (string Label, Func<MatchSummary, bool> Matcher)[] FilterQueueDefs =
    [
        ("全部", _ => true),
        ("匹配模式", m => m.QueueId == 430 || m.QueueName.Contains("匹配", StringComparison.Ordinal)),
        ("单双排位", m => m.QueueId == 420 || m.QueueName.Contains("单双", StringComparison.Ordinal)),
        ("灵活排位", m => m.QueueId == 440 || m.QueueName.Contains("灵活", StringComparison.Ordinal)),
        ("极地大乱斗", m => m.QueueId == 450 || m.QueueName.Contains("大乱斗", StringComparison.Ordinal)),
        ("海克斯大乱斗", m => m.QueueId == 2400),
        ("经典海斗", m => m.QueueId == 2450),
        ("斗魂竞技场", m => m.QueueId is 1700 or 1710 || m.QueueName.Contains("斗魂", StringComparison.Ordinal)),
        ("快速模式", m => m.QueueId is 480 or 490 || m.QueueName.Contains("快速", StringComparison.Ordinal)),
        ("人机对战", m => (m.QueueId >= 800 && m.QueueId <= 850) || m.QueueName.Contains("人机", StringComparison.Ordinal)),
        ("自定义模式", m => m.QueueId is 0 or 3100 || m.QueueName.Contains("自定义", StringComparison.Ordinal)),
    ];

    /// <summary>段位与私密状态跨选中缓存（TTL 5min / 10min）。</summary>
    private static readonly ConcurrentDictionary<string, (string? Tier, string? RankText, long Timestamp)> _rankCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, (bool IsPrivate, long Timestamp)> _privacyCache = new(StringComparer.Ordinal);

    private static readonly TimeSpan RankCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PrivacyCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>筛选下拉的选项缓存（含"全部"），仅在列表内容变化时重建。</summary>
    private string[]? _filterLabels;
    private IReadOnlyList<MatchSummary>? _filterSource;

    /// <summary>
    /// 筛选后的可见列表缓存。
    ///
    /// 必须缓存：卡片元素按"列表引用"判等，如果每帧都 <c>Where(...).ToArray()</c> 出一个新数组，
    /// 卡片会每帧重建，<c>ListView</c> 随之反复抛 SelectionChanged —— 正是改造前踩过的渲染自激。
    /// </summary>
    private IReadOnlyList<MatchSummary>? _visible;
    private IReadOnlyList<MatchSummary>? _visibleSource;
    private int _visibleFilter = -1;

    /// <summary>搜索框草稿。不做成 state：受控文本框在重渲染时会重置光标位置。</summary>
    private string? _searchDraft;

    /// <summary>
    /// 本轮已提交过上传的对局（对齐旧 Search.vue 的 <c>uploadedGameIds</c>）。
    /// 换人时清空：新召唤师的对局与上一人无关，不清空会误判为"已上传过"而整页跳过。
    /// </summary>
    private readonly HashSet<long> _uploadedGameIds = [];

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
        var (historyList, setHistoryList) = UseState(EmptyHistory, threadSafe: true);
        var (searching, setSearching) = UseState(false, threadSafe: true);
        var (uploadOn, setUploadOn) = UseState(services.Config.Functions.UploadEnabled, threadSafe: true);

        // 没配服务器地址时整个上传入口都不出现（对齐旧 Search.vue 的 v-if）：
        // 给了开关却没有地址，点了只会得到一串"未配置"的报错。
        bool uploadConfigured = !string.IsNullOrWhiteSpace(services.Config.General.UploadApiUrl);
        var uploadQueue = services.Services.GetRequiredService<UploadQueueService>();

        void OnUploadToggle(bool enabled)
        {
            setUploadOn(enabled);
            services.Config.Functions.UploadEnabled = enabled;
            try
            {
                services.ConfigStore.SaveWithBackup(services.Config);
            }
            catch (Exception)
            {
                setNotice("上传开关保存失败");
            }

            // 打开开关时把以前没传完的一起重试（对齐旧 config.rs 里配置变更触发重试的副作用）
            if (enabled)
            {
                _ = Task.Run(() => uploadQueue.TriggerPendingRetry());
            }
        }

        // 加载历史搜索
        UseEffect(
            () =>
            {
                _ = Task.Run(async () =>
                {
                    var list = await historyStore.GetHistoryAsync();
                    setHistoryList(list);
                });
                return static () => { };
            },
            0);

        // 对局结束自动静默刷新战绩
        UseEffect(
            () =>
            {
                if (lifecycle is null) return static () => { };
                var sub = lifecycle.GameEnded.Subscribe(time =>
                {
                    Task.Run(async () =>
                    {
                        setRequestVersion(requestVersion + 1);
                        await Task.Delay(2000);
                        setRequestVersion(requestVersion + 2);
                    });
                });
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
                        setRequestVersion(requestVersion + 1);
                    }
                });
                return () => subscription.Dispose();
            },
            0);

        // 资源映射表异步加载：客户端没启动时保持空表，图标退化为占位块而不是空白。
        var (catalog, setCatalog) = UseState(GameDataCatalog.Empty, threadSafe: true);
        UseEffect(
            () =>
            {
                var cancellation = new CancellationTokenSource();
                _ = LoadCatalogAsync(services, setCatalog, cancellation.Token);
                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                };
            },
            0);

        // 状态流：一次订阅，覆盖列表与详情两类请求（消息里自带是哪一个）。
        UseEffect(
            () =>
            {
                var subscription = matchService.Status.Subscribe(setStatus);
                return () => subscription.Dispose();
            },
            0);

        // 外部跳转（如生涯页点击最近队友、生涯资料卡点击战绩查询）传入的待查询目标
        UseEffect(
            () =>
            {
                var (pendingQuery, pendingPuuid, pendingGameId) = SearchNavigationPayload.Instance.Consume();
                if (!string.IsNullOrWhiteSpace(pendingQuery) || !string.IsNullOrWhiteSpace(pendingPuuid))
                {
                    if (!string.IsNullOrWhiteSpace(pendingQuery))
                    {
                        _searchDraft = pendingQuery;
                    }

                    long targetGameId = pendingGameId ?? 0;
                    _ = Task.Run(async () =>
                    {
                        // 若上一轮搜索还在进行中，等待空闲（防重入，最多 30s）
                        int waitCount = 0;
                        while (searching && waitCount < 600)
                        {
                            await Task.Delay(50);
                            waitCount++;
                        }

                        SubmitSearch(
                            summonerService,
                            historyStore,
                            setHistoryList,
                            setSummoner,
                            setSummonerResolved,
                            setMatches,
                            setSelectedGameId,
                            setPageIndex,
                            setNotice,
                            setSearching,
                            () => setRequestVersion(requestVersion + 1),
                            targetGameId,
                            pendingPuuid);
                    });
                }
                return static () => { };
            },
            0);

        // 首屏 / 搜索 / 翻页 / 刷新 共用一个入口：
        // 有 puuid 就拉这一页战绩；还没有召唤师就先解析当前登录账号，
        // setSummoner 会改变依赖键，下一轮再进来拉战绩（因此不会重复请求）。
        string historyKey = $"{summoner?.Puuid}|{pageIndex}|{requestVersion}|{connState.Status}";
        UseEffect(
            () =>
            {
                if (summoner is null)
                {
                    var resolveCancellation = new CancellationTokenSource();
                    _ = ResolveSummonerAsync(
                        summonerService,
                        setSummoner,
                        setSummonerResolved,
                        resolveCancellation.Token);

                    return () =>
                    {
                        resolveCancellation.Cancel();
                        resolveCancellation.Dispose();
                    };
                }

                var cancellation = new CancellationTokenSource();
                _ = LoadHistoryAsync(
                    services,
                    matchService,
                    summoner.Puuid,
                    pageIndex,
                    setMatches,
                    setHasMore,
                    cancellation.Token);

                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                };
            },
            historyKey);

        string[] filterLabels = ResolveFilterLabels(matches);
        int effectiveFilter = filterIndex < filterLabels.Length ? filterIndex : 0;
        IReadOnlyList<MatchSummary> visible = ResolveVisibleMatches(matches, filterLabels, effectiveFilter);

        int index = 0;
        if (selectedGameId > 0)
        {
            int found = IndexOf(visible, selectedGameId);
            index = found >= 0 ? found : 0;
        }
        else if (selectedGameId == -1)
        {
            index = -1;
        }

        MatchSummary? current = (index >= 0 && index < visible.Count) ? visible[index] : null;
        long currentGameId = current?.GameId ?? 0;

        // 详情：按当前选中的 gameId 加载。服务内部按 gameId 缓存，来回切换不会重复请求。
        UseEffect(
            () =>
            {
                if (currentGameId == 0)
                {
                    setDetail(null);
                    return static () => { };
                }

                // 先清空：否则新选中项会短暂顶着上一局的 10 人名单，看起来像数据错位
                setDetail(null);

                var cancellation = new CancellationTokenSource();
                _ = LoadDetailAsync(services, matchService, currentGameId, summoner?.Puuid, setDetail, cancellation.Token);
                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                };
            },
            currentGameId);

        // 载入一页后补传本页对局（对齐旧 Search.vue：翻页即补传，服务端已有会返回 exists）。
        // 依赖键取 matches 引用：换页或刷新会拿到新引用，纯筛选不会（筛选不改变要传的集合）。
        UseEffect(
            () =>
            {
                if (!uploadOn || !uploadConfigured)
                {
                    return static () => { };
                }

                long[] pending = matches.Select(m => m.GameId)
                    .Where(id => _uploadedGameIds.Add(id))
                    .ToArray();
                if (pending.Length == 0)
                {
                    return static () => { };
                }

                _ = UploadMatchesAsync(services, pending, summoner?.Puuid, setNotice);
                return static () => { };
            },
            matches,
            uploadOn);

        // 列表卡片：使用 ScrollView 自适应滚动并支持完全自定义的选中边框与胜负色，
        // 彻底消除 ListView 原生控件的蓝色选中矩形和焦点边框干扰。
        // 每项各自重新 Provide 上下文，保证卡片内的 LcuImage 正确获取服务容器。
        Element list = visible.Count == 0
            ? SkeletonList(status, summonerResolved, summoner is not null, palette)
            : ScrollViewer(VStack(0, visible.Select(summary =>
              {
                  bool isSelected = summary.GameId == currentGameId;
                  return MatchCardView.Render(
                      summary,
                      palette,
                      isSelected: isSelected,
                      onSelect: () =>
                      {
                          if (summary.GameId != selectedGameId)
                          {
                              setSelectedGameId(summary.GameId);
                          }
                      })
                      .Provide(AppHost.Context, services);
              }).ToArray()).Width(MatchCardView.CardWidth))
              .HAlign(HorizontalAlignment.Left);

        // 左列：列表吃剩余高度（自身滚动），分页条固定在底部。
        //
        // Grid 的签名是 Grid(columns, rows, children)——**第一个数组是列**，
        // 写反的后果不是编译错误而是静默错乱：本页曾经把行宽数组塞进列位，
        // 详情面板被放到不存在的第 1 列，整页挤在左上角一小块里。
        Element leftColumn = Grid(
                [GridSize.Star()],
                [GridSize.Star(), GridSize.Auto],
                [
                    list.Grid(row: 0, column: 0),
                    Pager(pageIndex, hasMore, setPageIndex, palette).Grid(row: 1, column: 0),
                ]);

        void OnCopyGameId(long gameId)
        {
            try
            {
                var package = new DataPackage();
                package.SetText(gameId.ToString(CultureInfo.InvariantCulture));
                Clipboard.SetContent(package);
                setNotice($"游戏 ID: {gameId} 已复制到剪贴板");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    setNotice(null);
                });
            }
            catch
            {
                setNotice("复制到剪贴板失败");
            }
        }

        void OnClickPlayerPuuid(string puuid, SummonerProfile? preloaded)
        {
            CareerNavigationPayload.Instance.Set(puuid, preloaded);
            nav.Replace(AppRoute.Career);
        }

        void OnClickPlayerSummonerId(long id)
        {
            _ = Task.Run(async () =>
            {
                var profile = await summonerService.FindBySummonerIdAsync(id);
                if (profile is not null)
                {
                    _searchDraft = profile.RiotId;
                    await historyStore.AddAsync(profile.RiotId);
                    var updated = await historyStore.GetHistoryAsync();
                    setHistoryList(updated);
                    setMatches(EmptyMatches);
                    setSelectedGameId(0);
                    setPageIndex(0);
                    setSummoner(profile);
                    setSummonerResolved(true);
                    setRequestVersion(requestVersion + 1);
                }
                else
                {
                    setNotice($"未找到召唤师 ID: {id}");
                }
            });
        }

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

        void OnSelectHistory(string item)
        {
            _searchDraft = item;
            SubmitSearch(
                summonerService,
                historyStore,
                setHistoryList,
                setSummoner,
                setSummonerResolved,
                setMatches,
                setSelectedGameId,
                setPageIndex,
                setNotice,
                setSearching,
                () => setRequestVersion(requestVersion + 1));
        }

        void OnCareer()
        {
            if (summoner is not null)
            {
                CareerNavigationPayload.Instance.Set(summoner.Puuid, summoner);
                nav.Replace(AppRoute.Career);
            }
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
                    Toolbar(
                            _searchDraft ?? summoner?.RiotId ?? string.Empty,
                            draft => _searchDraft = draft,
                            historyList,
                            OnSelectHistory,
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
                            () =>
                            {
                                // 换人即重置上传去重集合：新召唤师的对局与上一人无关
                                _uploadedGameIds.Clear();
                                SubmitSearch(
                                    summonerService,
                                    historyStore,
                                    setHistoryList,
                                    setSummoner,
                                    setSummonerResolved,
                                    setMatches,
                                    setSelectedGameId,
                                    setPageIndex,
                                    setNotice,
                                    setSearching,
                                    () => setRequestVersion(requestVersion + 1));
                            },
                            notice,
                            searching,
                            summoner,
                            palette,
                            uploadConfigured,
                            uploadOn,
                            OnUploadToggle)
                        .Grid(row: 0, column: 0),
                    content.Grid(row: 1, column: 0),
                ])
            .Padding(12)
            .HAlign(HorizontalAlignment.Stretch);
    }

    /// <summary>
    /// 补传一页对局（对齐旧 Search.vue 调 batch_upload_matches 后的三档提示）。
    ///
    /// 无论成败都给一次提示：静默失败会让人误以为传过了，静默成功又无从确认。
    /// </summary>
    private static async Task UploadMatchesAsync(
        AppServices services,
        long[] gameIds,
        string? currentSummonerPuuid,
        Action<string?> setNotice)
    {
        try
        {
            var batch = services.Services.GetRequiredService<UploadBatchService>();
            BatchUploadResult result = await batch
                .UploadAsync(gameIds, currentSummonerPuuid)
                .ConfigureAwait(false);

            setNotice(result switch
            {
                { Error: not null, SuccessCount: 0 } => $"上传失败: {result.Error}",
                { SuccessCount: > 0 } => $"已上传 {result.SuccessCount} 场对局",
                _ => "所有对局已存在，无需上传",
            });

            await Task.Delay(2500).ConfigureAwait(false);
            setNotice(null);
        }
        catch (Exception ex)
        {
            setNotice($"上传异常: {ex.Message}");
        }
    }

    /// <summary>顶部工具栏：搜索框带历史建议吃满剩余宽度，右侧为定宽功能按钮与筛选下拉。</summary>
    private static Element Toolbar(
        string query,
        Action<string> onQueryChanged,
        IReadOnlyList<string> historyList,
        Action<string> onSelectHistory,
        Action onCareer,
        bool hasSummoner,
        string[] filterLabels,
        int filterIndex,
        Action<int> onFilterChanged,
        Action onSearch,
        string? notice,
        bool searching,
        SummonerProfile? currentSummoner,
        YuumiPalette palette,
        bool uploadConfigured,
        bool uploadEnabled,
        Action<bool> onUploadToggle)
    {
        // 如果输入框内容为空、或与当前已展示的召唤师姓名一致（代表未输入新内容，仅仅是点击聚焦），则展示全部历史记录（对齐旧版 Search.vue）
        bool isDefaultQuery = string.IsNullOrWhiteSpace(query)
            || (currentSummoner is not null && (
                string.Equals(query.Trim(), currentSummoner.RiotId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(query.Trim(), currentSummoner.GameName, StringComparison.OrdinalIgnoreCase)));

        string[] filteredHistory = isDefaultQuery
            ? [.. historyList]
            : [.. historyList.Where(item => item.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))];

        Element searchBox = AutoSuggestBox(
                query,
                onQueryChanged,
                _ => onSearch())
            .PlaceholderText("输入召唤师名称进行搜索…")
            .AutomationName("召唤师名称搜索")
            .SuggestionChosen(chosen =>
            {
                if (!string.IsNullOrWhiteSpace(chosen))
                {
                    onSelectHistory(chosen);
                }
            })
            .Set(box =>
            {
                box.ItemsSource = filteredHistory;
                if (box.Tag is null)
                {
                    box.Tag = true;
                    box.GotFocus += (s, e) =>
                    {
                        if (s is Microsoft.UI.Xaml.Controls.AutoSuggestBox asb
                            && asb.ItemsSource is System.Collections.IEnumerable en
                            && en.GetEnumerator().MoveNext())
                        {
                            asb.IsSuggestionListOpen = true;
                        }
                    };
                    box.TextChanged += (s, e) =>
                    {
                        if (s is Microsoft.UI.Xaml.Controls.AutoSuggestBox asb
                            && e.Reason == Microsoft.UI.Xaml.Controls.AutoSuggestionBoxTextChangeReason.UserInput
                            && asb.ItemsSource is System.Collections.IEnumerable en
                            && en.GetEnumerator().MoveNext())
                        {
                            asb.IsSuggestionListOpen = true;
                        }
                    };
                    box.AddHandler(
                        Microsoft.UI.Xaml.UIElement.PointerPressedEvent,
                        new Microsoft.UI.Xaml.Input.PointerEventHandler((s, e) =>
                        {
                            if (s is Microsoft.UI.Xaml.Controls.AutoSuggestBox asb
                                && asb.ItemsSource is System.Collections.IEnumerable en
                                && en.GetEnumerator().MoveNext())
                            {
                                asb.IsSuggestionListOpen = true;
                            }
                        }),
                        handledEventsToo: true);
                }
            });

        Element bar = Grid(
            [GridSize.Star(), GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto],
            [GridSize.Auto],
            [
                searchBox.Grid(row: 0, column: 0),
                Button("查询", onSearch).IsEnabled(!searching).Grid(row: 0, column: 1),
                Button("生涯", onCareer).IsEnabled(hasSummoner).AutomationName("跳转生涯").Grid(row: 0, column: 2),
                ComboBox(filterLabels, filterIndex, onFilterChanged)
                    .AutomationName("队列筛选")
                    .Width(130)
                    .Set(cb =>
                    {
                        if (cb.SelectedIndex != filterIndex && filterIndex >= 0 && filterIndex < filterLabels.Length)
                        {
                            cb.SelectedIndex = filterIndex;
                        }
                    })
                    .Grid(row: 0, column: 3),
                // 上传开关只在配好服务器地址时出现（旧 Search.vue 的 v-if="General.UploadApiUrl"）
                uploadConfigured
                    ? CheckBox(uploadEnabled, onUploadToggle, "上传对局")
                        .AutomationName("上传对局开关")
                        .Grid(row: 0, column: 4)
                    : Empty().Grid(row: 0, column: 4),
            ]);

        // 提示行放在搜索框下方而不是列表上方：出错的是"搜索"，不是"列表"
        Element body = notice is null
            ? bar
            : VStack(6, bar, Caption(notice).Foreground(Theme.SecondaryText));

        return Border(body)
            .Padding(8)
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(HorizontalAlignment.Stretch);
    }

    /// <summary>底部翻页：只做前后各一页，页码即当前页序号（从 1 开始）。</summary>
    private static Element Pager(int pageIndex, bool hasMore, Action<int> setPageIndex, YuumiPalette palette)
    {
        if (pageIndex == 0 && !hasMore)
        {
            // 只有一页且无更多数据：不占用行高，也不显示一个点不动的翻页器
            return Empty();
        }

        Element previous = Button("‹", () => setPageIndex(pageIndex - 1)).Width(32)
            .AutomationName("上一页")
            .IsEnabled(pageIndex > 0);

        Element next = Button("›", () => setPageIndex(pageIndex + 1)).Width(32)
            .AutomationName("下一页")
            .IsEnabled(hasMore);

        Element content = HStack(8,
                previous,
                Caption((pageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .VAlign(VerticalAlignment.Center),
                next)
            .HAlign(HorizontalAlignment.Center);

        return Border(content)
            .Padding(4)
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(HorizontalAlignment.Stretch);
    }

    /// <summary>
    /// 列表为空时的占位。三种情形要分开：
    /// 召唤师还没解析完（骨架）→ 客户端确实没连上（骨架）→ 读取失败（服务给的原因）。
    /// 全部渲染浅色圆角占位卡（对齐 pic/战绩查询1.png）。
    /// </summary>
    private static Element SkeletonList(
        MatchServiceStatus status,
        bool summonerResolved,
        bool hasSummoner,
        YuumiPalette palette)
    {
        if (!summonerResolved || !hasSummoner)
        {
            return BuildSkeletons(palette);
        }

        if (status.State == MatchLoadState.Failed)
        {
            return Hint(status.Message ?? "读取战绩失败", palette);
        }

        return BuildSkeletons(palette);
    }

    /// <summary>列表区的说明块：与卡片同宽，避免右侧面板被拉宽。</summary>
    private static Element Hint(string text, YuumiPalette palette) =>
        Border(Caption(text).Foreground(Theme.SecondaryText).TextWrapping())
            .Padding(12)
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(HorizontalAlignment.Stretch);

    private static Element BuildSkeletons(YuumiPalette palette)
    {
        var skeletons = new Element[SkeletonCount];
        for (var i = 0; i < skeletons.Length; i++)
        {
            skeletons[i] = MatchCardView.Skeleton(palette);
        }

        // 骨架自带上下的间隙（见 MatchCardView.Skeleton），这里不再额外加间距
        return VStack(0, skeletons).Width(MatchCardView.CardWidth);
    }

    /// <summary>
    /// 筛选下拉的选项：预置主流模式（全部、匹配模式、单双排位、灵活排位、极地大乱斗等），
    /// 同时去重追加列表中出现的特殊/娱乐模式。
    /// </summary>
    private string[] ResolveFilterLabels(IReadOnlyList<MatchSummary> matches)
    {
        if (_filterLabels is not null && ReferenceEquals(_filterSource, matches))
        {
            return _filterLabels;
        }

        var labels = new List<string>(FilterQueueDefs.Length);
        foreach (var (label, _) in FilterQueueDefs)
        {
            labels.Add(label);
        }

        foreach (MatchSummary summary in matches)
        {
            if (!string.IsNullOrEmpty(summary.QueueName)
                && !labels.Contains(summary.QueueName, StringComparer.Ordinal)
                && !FilterQueueDefs.Any(def => def.Matcher(summary)))
            {
                labels.Add(summary.QueueName);
            }
        }

        _filterLabels = labels.ToArray();
        _filterSource = matches;
        return _filterLabels;
    }

    /// <summary>按队列筛选；结果按（源列表, 筛选序号）缓存，避免每帧产出新集合。</summary>
    private IReadOnlyList<MatchSummary> ResolveVisibleMatches(
        IReadOnlyList<MatchSummary> matches,
        string[] labels,
        int filterIndex)
    {
        if (_visible is not null
            && ReferenceEquals(_visibleSource, matches)
            && _visibleFilter == filterIndex)
        {
            return _visible;
        }

        _visible = FilterMatches(matches, labels, filterIndex);
        _visibleSource = matches;
        _visibleFilter = filterIndex;
        return _visible;
    }

    private static IReadOnlyList<MatchSummary> FilterMatches(
        IReadOnlyList<MatchSummary> matches,
        string[] labels,
        int filterIndex)
    {
        if (filterIndex <= 0 || filterIndex >= labels.Length)
        {
            return matches;
        }

        Func<MatchSummary, bool> predicate;
        if (filterIndex < FilterQueueDefs.Length)
        {
            predicate = FilterQueueDefs[filterIndex].Matcher;
        }
        else
        {
            string queueName = labels[filterIndex];
            predicate = m => string.Equals(m.QueueName, queueName, StringComparison.Ordinal);
        }

        var filtered = new List<MatchSummary>(matches.Count);
        foreach (MatchSummary summary in matches)
        {
            if (predicate(summary))
            {
                filtered.Add(summary);
            }
        }

        return filtered;
    }

    private static int IndexOf(IReadOnlyList<MatchSummary> matches, long gameId)
    {
        for (var i = 0; i < matches.Count; i++)
        {
            if (matches[i].GameId == gameId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 提交搜索：解析 Riot ID → 换召唤师 → 重新拉第一页。
    /// 名字空或查不到时只更新提示，不清空当前已显示的战绩（否则一次手滑就把列表清空）。
    /// </summary>
    private void SubmitSearch(
        ISummonerService summonerService,
        SearchHistoryStore historyStore,
        Action<IReadOnlyList<string>> applyHistory,
        Action<SummonerProfile?> applySummoner,
        Action<bool> applySummonerResolved,
        Action<IReadOnlyList<MatchSummary>> applyMatches,
        Action<long> applySelection,
        Action<int> applyPageIndex,
        Action<string?> applyNotice,
        Action<bool> applySearching,
        Action applyReload,
        long targetGameId = 0,
        string? targetPuuid = null)
    {
        string query = (_searchDraft ?? string.Empty).Trim();
        if (query.Length == 0 && string.IsNullOrWhiteSpace(targetPuuid))
        {
            applyNotice("请输入召唤师名称（形如 名字#TAG）");
            return;
        }

        applyNotice(null);
        applySearching(true);

        _ = SearchAsync(
            summonerService,
            historyStore,
            applyHistory,
            query,
            draft => _searchDraft = draft,
            applySummoner,
            applySummonerResolved,
            applyMatches,
            applySelection,
            applyPageIndex,
            applyNotice,
            applySearching,
            applyReload,
            targetGameId,
            targetPuuid);
    }

    /// <summary>
    /// 解析被搜索的召唤师并切换过去。**不在这里拉战绩**：
    /// 换人会改变调用方的取数依赖键，由那一侧统一拉第一页——
    /// 否则同一页会请求两次（本方法一次 + effect 一次）。
    /// </summary>
    private static async Task SearchAsync(
        ISummonerService summonerService,
        SearchHistoryStore historyStore,
        Action<IReadOnlyList<string>> applyHistory,
        string query,
        Action<string> applyDraft,
        Action<SummonerProfile?> applySummoner,
        Action<bool> applySummonerResolved,
        Action<IReadOnlyList<MatchSummary>> applyMatches,
        Action<long> applySelection,
        Action<int> applyPageIndex,
        Action<string?> applyNotice,
        Action<bool> applySearching,
        Action applyReload,
        long targetGameId,
        string? targetPuuid = null)
    {
        try
        {
            SummonerProfile? found = null;
            if (!string.IsNullOrWhiteSpace(targetPuuid))
            {
                found = await summonerService
                    .FindByPuuidAsync(targetPuuid)
                    .ConfigureAwait(false);
            }

            if (found is null && !string.IsNullOrWhiteSpace(query))
            {
                found = await summonerService
                    .FindByNameAsync(query)
                    .ConfigureAwait(false);
            }

            if (found is null)
            {
                applyNotice($"未找到召唤师：{(!string.IsNullOrEmpty(query) ? query : targetPuuid)}");
                return;
            }

            if (!string.IsNullOrEmpty(found.RiotId))
            {
                applyDraft(found.RiotId);
            }

            // 保存到搜索历史
            string historyName = !string.IsNullOrEmpty(found.TagLine)
                ? $"{found.GameName}#{found.TagLine}"
                : found.DisplayName;
            await historyStore.AddAsync(historyName).ConfigureAwait(false);
            var updated = await historyStore.GetHistoryAsync().ConfigureAwait(false);
            applyHistory(updated);

            // 先清空列表再换人：否则新召唤师的头像旁边会短暂挂着上一个人的战绩
            applyMatches(EmptyMatches);
            applySelection(targetGameId);
            applyPageIndex(0);
            applySummoner(found);
            applySummonerResolved(true);

            // 搜到的是同一个人时依赖键不变（puuid、页号都没动），必须显式触发一次重取
            applyReload();
        }
        catch (OperationCanceledException)
        {
            // 页面已卸载
        }
        finally
        {
            applySearching(false);
        }
    }

    /// <summary>解析当前登录召唤师（首屏 / 刷新）。失败保持 <c>null</c>，界面显示"未连接"。</summary>
    private static async Task ResolveSummonerAsync(
        ISummonerService summonerService,
        Action<SummonerProfile?> apply,
        Action<bool> applyResolved,
        CancellationToken cancellationToken)
    {
        try
        {
            SummonerProfile? profile = await summonerService
                .GetCurrentSummonerAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!cancellationToken.IsCancellationRequested)
            {
                apply(profile);
                applyResolved(true);
            }
        }
        catch (OperationCanceledException)
        {
            // 页面已卸载
        }
    }

    /// <summary>拉一页战绩。任何失败都只是让页面停在空列表，不抛给 UI。</summary>
    private static async Task LoadHistoryAsync(
        AppServices services,
        IMatchService matchService,
        string puuid,
        int pageIndex,
        Action<IReadOnlyList<MatchSummary>> applyMatches,
        Action<bool> applyHasMore,
        CancellationToken cancellationToken)
    {
        try
        {
            int begin = pageIndex * PageSize;
            MatchHistoryPage page = await matchService
                .GetMatchHistoryAsync(puuid, begin, begin + PageSize - 1, cancellationToken)
                .ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // 预热本页英雄头像：在交付渲染前确保本地已有图片缓存，
            // 使得卡片挂载首帧即可同步命中 Image(file://)，彻底杜绝空白头像。
            if (page.Games.Count > 0)
            {
                var assets = services.Services.GetRequiredService<AssetService>();
                var preloadTasks = page.Games.Select(g =>
                    assets.GetCachedPathAsync(SearchVisuals.ChampionIcon(g.ChampionId), cancellationToken));
                await Task.WhenAll(preloadTasks).ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                applyMatches(page.Games);
                applyHasMore(page.HasMore);
            }
        }
        catch (OperationCanceledException)
        {
            // 页面已卸载或翻页过快
        }
    }

    /// <summary>加载单局详情；失败保持 <c>null</c>，面板会显示"读取中/未取到"。</summary>
    private static async Task LoadDetailAsync(
        AppServices services,
        IMatchService matchService,
        long gameId,
        string? puuid,
        Action<MatchDetail?> apply,
        CancellationToken cancellationToken)
    {
        try
        {
            MatchDetail? loaded = await matchService
                .GetMatchDetailAsync(gameId, puuid, cancellationToken)
                .ConfigureAwait(false);

            if (!cancellationToken.IsCancellationRequested)
            {
                apply(loaded);
                if (loaded is not null)
                {
                    _ = LoadParticipantRanksAndPrivacyAsync(services, loaded, apply, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 选中项已切换
        }
    }

    /// <summary>后台渐进加载对局 10 人名单的段位与战绩隐私状态。</summary>
    private static async Task LoadParticipantRanksAndPrivacyAsync(
        AppServices services,
        MatchDetail detail,
        Action<MatchDetail?> apply,
        CancellationToken cancellationToken)
    {
        var careerService = services.Services.GetService<ICareerService>();
        var lcu = services.Services.GetService<LcuConnection>();
        var config = services.Services.GetService<AppConfig>();
        if (careerService is null || lcu is null) return;

        var allPlayers = detail.Blue.Players.Concat(detail.Red.Players).Where(p => !string.IsNullOrEmpty(p.Puuid)).ToList();
        if (allPlayers.Count == 0) return;

        // 「对局详情中显示段位」关闭时跳过段位拉取（Tier 为空即不渲染勋章，
        // 对照旧 Search.vue loadRanksInBackground 的 showTier 判定）
        bool showTier = services.Config.Functions.ShowTierInGameInfo;

        using var semaphore = new SemaphoreSlim(3, 3);
        var rankMap = new ConcurrentDictionary<string, (string? Tier, string? RankText)>(StringComparer.Ordinal);
        var privacyMap = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

        long now = Environment.TickCount64;

        var tasks = allPlayers.Select(async p =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // 1. 段位（仅在配置开启时加载，TTL 5min 缓存，solo 优先、flex 兜底）
                if (showTier)
                {
                    if (_rankCache.TryGetValue(p.Puuid, out var cachedRank) && now - cachedRank.Timestamp < RankCacheTtl.TotalMilliseconds)
                    {
                        if (!string.IsNullOrEmpty(cachedRank.Tier))
                        {
                            rankMap[p.Puuid] = (cachedRank.Tier, cachedRank.RankText);
                        }
                    }
                    else
                    {
                        var stats = await careerService.GetRankedStatsAsync(p.Puuid, cancellationToken: cancellationToken).ConfigureAwait(false);
                        var entry = stats.SoloDuo;
                        if (entry is null || string.IsNullOrEmpty(entry.Tier) || entry.Tier == "NONE" || entry.Tier == "UNRANKED")
                        {
                            entry = stats.Flex;
                        }

                        if (entry is not null && !string.IsNullOrEmpty(entry.Tier) && entry.Tier != "NONE" && entry.Tier != "UNRANKED")
                        {
                            rankMap[p.Puuid] = (entry.Tier, entry.TierText);
                            _rankCache[p.Puuid] = (entry.Tier, entry.TierText, now);
                        }
                        else
                        {
                            _rankCache[p.Puuid] = (null, null, now);
                        }
                    }
                }

                // 2. 隐私状态（TTL 10min 缓存，仅缓存 PRIVATE）
                if (_privacyCache.TryGetValue(p.Puuid, out var cachedPriv) && now - cachedPriv.Timestamp < PrivacyCacheTtl.TotalMilliseconds)
                {
                    if (cachedPriv.IsPrivate)
                    {
                        privacyMap[p.Puuid] = true;
                    }
                }
                else
                {
                    var transport = lcu.Transport;
                    if (transport is not null)
                    {
                        var resp = await transport.SendAsync(
                            LcuRequest.Get($"/lol-summoner/v2/summoners/puuid/{p.Puuid}"),
                            cancellationToken).ConfigureAwait(false);
                        if (resp.IsSuccess && !string.IsNullOrWhiteSpace(resp.Body))
                        {
                            using var doc = JsonDocument.Parse(resp.Body);
                            string? privacy = doc.RootElement.TryGetProperty("privacy", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.String
                                ? pEl.GetString()
                                : null;
                            bool isPriv = string.Equals(privacy, "PRIVATE", StringComparison.OrdinalIgnoreCase);
                            if (isPriv)
                            {
                                privacyMap[p.Puuid] = true;
                                _privacyCache[p.Puuid] = (true, now);
                            }
                            else
                            {
                                _privacyCache[p.Puuid] = (false, now);
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested) return;

        MatchDetailPlayer MapPlayer(MatchDetailPlayer player)
        {
            var rank = rankMap.GetValueOrDefault(player.Puuid);
            bool isPrivate = privacyMap.GetValueOrDefault(player.Puuid, false);
            return player with
            {
                Tier = rank.Tier ?? player.Tier,
                RankText = rank.RankText ?? player.RankText,
                IsPrivate = isPrivate,
            };
        }

        var updatedBlue = detail.Blue with { Players = detail.Blue.Players.Select(MapPlayer).ToList() };
        var updatedRed = detail.Red with { Players = detail.Red.Players.Select(MapPlayer).ToList() };
        apply(detail with { Blue = updatedBlue, Red = updatedRed });
    }

    /// <summary>
    /// 加载游戏资源映射表。失败不提示：客户端未启动是常态，图标占位已经足够表达。
    /// </summary>
    private static async Task LoadCatalogAsync(
        AppServices services,
        Action<GameDataCatalog> apply,
        CancellationToken cancellationToken)
    {
        try
        {
            GameDataCatalog loaded = await services.Services
                .GetRequiredService<GameDataService>()
                .GetAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!cancellationToken.IsCancellationRequested)
            {
                apply(loaded);
            }
        }
        catch (OperationCanceledException)
        {
            // 页面已卸载
        }
        catch (Exception)
        {
            // 资源表缺失只影响图标，不影响布局
        }
    }
}
