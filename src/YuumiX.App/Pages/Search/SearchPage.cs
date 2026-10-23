using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Theming;
using YuumiX.Core;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 战绩查询页：左侧对局列表 + 右侧详情面板，数据全部来自 LCU。
///
/// 对应原 <c>Search.vue</c>（列表 + 召唤师信息）与 <c>MatchDetailPanel.vue</c>（详情）。
///
/// <b>数据通路</b>：<c>ISummonerService</c> 取当前账号的 puuid →
/// <c>IMatchService.GetMatchHistoryAsync</c> 取一页列表 → 选中项再取
/// <c>GetMatchDetailAsync</c>。三者都是异步且可能因为"客户端没开"直接返回空，
/// 因此页面维护四份状态：召唤师、列表、详情、状态快照。
///
/// <b>为什么状态流要订阅而不是自己拼文案</b>：<c>MatchServiceStatus.Message</c> 里区分了
/// 「正在拉取第 N–M 场」与「正在读取对局 X 的详情」，列表加载与详情加载共用同一条流。
/// 页面若按 <c>State</c> 全量展示，选中一局后列表上方会突然冒出"正在读取对局 123 的详情"。
/// 因此这里只把**失败**透出到列表上方（见 <see cref="StatusLine"/>），
/// 加载中的提示由空列表占位与详情面板各自承担。
///
/// <b>线程</b>：服务在后台线程发布状态、异步返回结果，所有 <c>UseState</c> 都必须带
/// <c>threadSafe: true</c>（ADR-07）。
/// </summary>
public sealed class SearchPage : Component
{
    /// <summary>列表宽度（DIP）。与详情面板的 300 DIP 队伍块配合，在 1280 宽度下不挤压。</summary>
    private const double ListWidth = 540;

    /// <summary>
    /// 单页条数。LCU 的 <c>matches</c> 接口在一次登录期间只缓存最近 20 局，
    /// 取更多也不会返回（<c>gameCount</c> 恒为 20），因此按一页取满。
    /// 要突破这个上限只能走 SGP，属后续工作。
    /// </summary>
    private const int PageSize = 20;

    /// <summary>
    /// 空列表的共享实例。<c>UseState</c> 的初值只读一次，但写成 <c>[]</c> 会在每次渲染
    /// 新建一个集合（REACTOR_HOOKS_013），因此用静态实例而不是集合表达式。
    /// </summary>
    private static readonly IReadOnlyList<MatchSummary> EmptyMatches = [];

    /// <summary>卡片元素缓存，仅在列表内容 / 资源表 / 配色变化时重建（见 <c>ResolveCards</c>）。</summary>
    private Element[]? _cards;
    private IReadOnlyList<MatchSummary>? _cardsMatches;
    private GameDataCatalog? _cardsCatalog;
    private YuumiPalette? _cardsPalette;

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var matchService = services.Services.GetRequiredService<IMatchService>();
        var summonerService = services.Services.GetRequiredService<ISummonerService>();

        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = theme.Subscribe(setPalette);
                return () => subscription.Dispose();
            },
            0);

        var (summoner, setSummoner) = UseState<SummonerProfile?>(null, threadSafe: true);
        var (matches, setMatches) = UseState(EmptyMatches, threadSafe: true);
        var (status, setStatus) = UseState(MatchServiceStatus.Idle, threadSafe: true);
        var (detail, setDetail) = UseState<MatchDetail?>(null, threadSafe: true);
        var (selectedIndex, setSelectedIndex) = UseState(0, threadSafe: true);

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

        // 首屏：召唤师 → 第一页战绩。客户端未登录时两步都会静默返回 null/空页，
        // 界面按"未连接"渲染，不弹错误对话框。
        UseEffect(
            () =>
            {
                var cancellation = new CancellationTokenSource();
                _ = LoadHistoryAsync(summonerService, matchService, setSummoner, setMatches, cancellation.Token);
                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                };
            },
            0);

        int index = selectedIndex >= 0 && selectedIndex < matches.Count ? selectedIndex : 0;
        MatchSummary? current = matches.Count > 0 ? matches[index] : null;
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
                _ = LoadDetailAsync(matchService, currentGameId, summoner?.Puuid, setDetail, cancellation.Token);
                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                };
            },
            currentGameId);

        // 列表项必须各自重新 Provide 上下文：ListView 的项由原生控件托管渲染，
        // 不在页面的 Provide 作用域内，否则卡片里的 LcuImage 取 UseContext 会拿到 null
        // （表现为整行渲染异常，已被 Reactor 的错误边界替换成红字堆栈）。
        //
        // 卡片**不能依赖选中索引**：一旦随选中重建，ListView 会收到一批全新的项元素，
        // 进而再次抛出 SelectionChanged，形成「选中 → 重建 → 再选中」的自激循环，
        // 表现为整片列表持续闪烁（每个卡片里的 LcuImage 都被反复重置成占位图）。
        // 因此卡片只随（列表内容、资源表、配色）变化重建，选中态交给 ListView 自身的高亮。
        //
        // 缓存放在字段而不是 UseMemo：UseMemo 的依赖若是每次渲染新建的元组，
        // 比较永远不等（REACTOR_HOOKS_004），等于没缓存。
        Element[] ResolveCards()
        {
            if (_cards is not null
                && ReferenceEquals(_cardsMatches, matches)
                && ReferenceEquals(_cardsCatalog, catalog)
                && Equals(_cardsPalette, palette))
            {
                return _cards;
            }

            _cards = matches
                .Select(summary => MatchCardView.Render(summary, catalog, palette)
                    .Provide(AppHost.Context, services))
                .ToArray();
            _cardsMatches = matches;
            _cardsCatalog = catalog;
            _cardsPalette = palette;
            return _cards;
        }

        Element[] cards = ResolveCards();

        Element list = matches.Count == 0
            ? EmptyHint(status)
            : ListView((int?)index, next =>
            {
                // 值相同则直接丢弃：ListView 在项重建后会重复抛出同一个索引，
                // 不比对就会把「同一次选中」当成新事件触发重渲染。
                if (next >= 0 && next != selectedIndex)
                {
                    setSelectedIndex(next);
                }
            }, cards).Width(ListWidth);

        Element header = VStack(4,
            Heading("战绩查询"),
            Caption(SummonerLine(summoner)),
            StatusLine(status));

        return HStack(16,
                VStack(12, header, list).Width(ListWidth),
                MatchDetailView.Render(detail, current, catalog, palette))
            .Padding(24)
            .HAlign(HorizontalAlignment.Stretch);
    }

    /// <summary>召唤师一行：名字 + 等级；未连接时说明原因而不是留空。</summary>
    private static string SummonerLine(SummonerProfile? summoner) => summoner is null
        ? "未读取到当前召唤师（客户端未登录）"
        : $"{summoner.RiotId} · 等级 {summoner.SummonerLevel}";

    /// <summary>
    /// 列表上方的状态行：**只显示失败**。
    ///
    /// 状态流同时承载列表与详情两类请求，直接按 <c>State</c> 展示会让选中一局后
    /// 顶部突然出现"正在读取对局 123 的详情"，与列表无关。加载中的提示已经由
    /// 空列表占位与详情面板各自承担，这里只保留需要用户知道原因的失败信息。
    /// </summary>
    private static Element StatusLine(MatchServiceStatus status)
    {
        if (status.State != MatchLoadState.Failed || status.Message is null)
        {
            return TextBlock(string.Empty).FontSize(11);
        }

        return TextBlock(status.Message).FontSize(11).Foreground(Theme.SecondaryText);
    }

    /// <summary>列表为空时的占位：区分"还没连上"与"这个账号确实没有对局"。</summary>
    private static Element EmptyHint(MatchServiceStatus status)
    {
        string text = status.State switch
        {
            MatchLoadState.Loading => "正在读取战绩…",
            MatchLoadState.Failed => status.Message ?? "读取战绩失败",
            _ => "没有可显示的对局（客户端未连接，或该账号近期没有对局）",
        };

        return Border(Caption(text)).Padding(20)
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);
    }

    /// <summary>召唤师 → 第一页战绩。任何一步失败都只是让页面停在空列表，不抛给 UI。</summary>
    private static async Task LoadHistoryAsync(
        ISummonerService summonerService,
        IMatchService matchService,
        Action<SummonerProfile?> applySummoner,
        Action<IReadOnlyList<MatchSummary>> applyMatches,
        CancellationToken cancellationToken)
    {
        try
        {
            SummonerProfile? profile = await summonerService
                .GetCurrentSummonerAsync(cancellationToken)
                .ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            applySummoner(profile);
            if (profile is null)
            {
                applyMatches(EmptyMatches);
                return;
            }

            MatchHistoryPage page = await matchService
                .GetMatchHistoryAsync(profile.Puuid, 0, PageSize - 1, cancellationToken)
                .ConfigureAwait(false);

            if (!cancellationToken.IsCancellationRequested)
            {
                applyMatches(page.Games);
            }
        }
        catch (OperationCanceledException)
        {
            // 页面已卸载
        }
    }

    /// <summary>加载单局详情；失败保持 <c>null</c>，面板会显示"读取中/未取到"。</summary>
    private static async Task LoadDetailAsync(
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
            }
        }
        catch (OperationCanceledException)
        {
            // 选中项已切换
        }
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
