using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Theming;
using YuumiX.Core;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 战绩查询页骨架：左侧对局列表 + 右侧详情面板。
///
/// 对应原 <c>Search.vue</c>（列表 + 召唤师信息）与 <c>MatchDetailPanel.vue</c>（详情）。
/// 本阶段的范围是**把布局与数据通路立起来**，因此：
/// <list type="bullet">
///   <item>数据来自随程序分发的夹具（<see cref="SearchFixtures"/>），不是 LCU 接口；</item>
///   <item>没有召唤师搜索栏、没有 ROFL 回放——这两项仍在 <c>PageCatalog</c> 的待迁移清单里；</item>
///   <item>列表用 <c>ListView</c> 承担选中与滚动，避免自己写指针事件与虚拟化。</item>
/// </list>
///
/// 接真实数据时只改两处：<see cref="SearchFixtures.All"/> → <c>IMatchService.GetMatchHistoryAsync</c>，
/// <see cref="SearchFixtures.LoadDetail"/> → <c>GetMatchDetailAsync</c>，并把同步读改成
/// <c>UseEffect</c> + <c>UseState</c> 的异步加载（状态流已有 <c>MatchServiceStatus</c>）。
/// </summary>
public sealed class SearchPage : Component
{
    /// <summary>列表宽度（DIP）。与详情面板的 300 DIP 队伍块配合，在 1280 宽度下不挤压。</summary>
    private const double ListWidth = 540;

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);

        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = theme.Subscribe(setPalette);
                return () => subscription.Dispose();
            },
            0);

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

        IReadOnlyList<MatchSummary> matches = SearchFixtures.All;
        int index = selectedIndex >= 0 && selectedIndex < matches.Count ? selectedIndex : 0;
        MatchSummary? current = matches.Count > 0 ? matches[index] : null;
        MatchDetail? detail = current is null ? null : SearchFixtures.LoadDetail(current.GameId);

        // 列表项必须各自重新 Provide 上下文：ListView 的项由原生控件托管渲染，
        // 不在页面的 Provide 作用域内，否则卡片里的 LcuImage 取 UseContext 会拿到 null
        // （表现为整行渲染异常，已被 Reactor 的错误边界替换成红字堆栈）。
        Element[] cards = matches
            .Select((summary, position) => MatchCardView.Render(summary, catalog, palette, position == index)
                .Provide(AppHost.Context, services))
            .ToArray();

        Element list = matches.Count == 0
            ? Border(Caption("没有可显示的夹具数据")).Padding(20)
                .CornerRadius(8)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke, 1)
            : ListView((int?)index, setSelectedIndex, cards).Width(ListWidth);

        Element header = VStack(4,
            Heading("战绩查询"),
            Caption($"对应原实现：src/views/Search.vue · {SearchFixtures.SourceNote}"));

        return HStack(16,
                VStack(12, header, list).Width(ListWidth),
                MatchDetailView.Render(detail, current, catalog, palette))
            .Padding(24)
            .HAlign(HorizontalAlignment.Stretch);
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
