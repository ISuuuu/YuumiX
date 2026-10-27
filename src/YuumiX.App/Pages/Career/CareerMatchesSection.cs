using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.SavedPlayers.Models;

namespace YuumiX.App.Pages.Career;

/// <summary>
/// 「生涯战绩」页签的组装（自 <c>CareerPage</c> 拆出，R14）：
/// 排位表 + 近期对局看板（含下拉与最近队友）+ 战绩卡片列表。
/// </summary>
internal static class CareerMatchesSection
{
    /// <summary>
    /// 「生涯战绩」页签的两段内容：<see cref="Header"/>（排位表 + 近期对局看板，**固定在页面顶部**）、
    /// <see cref="List"/>（战绩卡片列表，**唯一可滚动的区域**）。
    ///
    /// 拆成两段而不是一个 VStack，是因为页面要求「滚动时锁定近期对局及以上内容，只滚战绩」：
    /// 调用方把 Header 放进固定行、List 包进 ScrollViewer 放进 Star 行。若这里继续返回整体，
    /// 页签切换后只能整页滚动，头部会被推走。
    /// </summary>
    internal readonly record struct MatchesParts(Element Header, Element List);

    internal static MatchesParts Render(
        RankedStats ranked,
        RecentFormSummary summary,
        GameDataCatalog catalog,
        YuumiPalette palette,
        string[] filterLabels,
        int filter,
        int queueFilter,
        bool loading,
        IReadOnlyList<MatchSummary> visibleMatches,
        RecentTeammatesResult? teammates,
        bool loadingTeammates,
        Action<int> onFilterChanged,
        Action onRecentTeammates,
        Action<RecentTeammate> onClickTeammate,
        Action<MatchSummary> onOpenMatchInSearch,
        string? lang = null)
    {
        // 旧版：排位表与近期看板各有 1rem（16）的下外边距。
        // 战绩卡片列表的步进是 92 = 卡片 82 + 行距 10（MatchHistoryTab.vue 的 VIRTUAL_ITEM_HEIGHT），
        // 10 不在 4px 网格上，因此拆成「VStack 8 + 行内 margin 2」凑出同一节奏。
        const double MatchRowGap = 10;
        const double StackGap = 8;

        bool isInitialLoad = (ranked.SoloDuo is null && ranked.Flex is null) || summary.IsEmpty;
        bool needSkeleton = loading && isInitialLoad;

        Element header = VStack(16,
            needSkeleton && ranked.SoloDuo is null && ranked.Flex is null
                ? CareerVisuals.SkeletonRankedTable()
                : RankedTableView.Render(ranked, palette),
            needSkeleton && summary.IsEmpty
                ? CareerVisuals.SkeletonRecentSummary()
                : RecentSummaryView.Render(
                    summary,
                    catalog,
                    palette,
                    filterLabels,
                    filter,
                    onFilterChanged,
                    onRecentTeammates: onRecentTeammates,
                    teammates: teammates,
                    loadingTeammates: loadingTeammates,
                    onClickTeammate: onClickTeammate));

        var i18n = Composition.AppHost.Services?.I18n;
        var emptyText = filter == 0
            ? (i18n?.T("career.emptyMatches", "没有可显示的对局（客户端未连接，或该账号近期没有对局）") ?? "没有可显示的对局（客户端未连接，或该账号近期没有对局）")
            : (i18n?.T("career.emptyQueueMatches", "该队列下没有近期对局") ?? "该队列下没有近期对局");

        Element list = visibleMatches.Count > 0
            ? VStack(StackGap, visibleMatches
                    .Select(match => Component<CareerMatchRow, CareerMatchRowProps>(
                        new CareerMatchRowProps(match, catalog, palette, () => onOpenMatchInSearch(match), lang))
                        .Margin(0, 0, 0, MatchRowGap - StackGap)
                        .WithKey(match.GameId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    .ToArray())
            : (needSkeleton || loading)
                // 骨架态：与真实卡片同高（82）同圆角同描边，数据到达时整列不跳。
                ? CareerVisuals.SkeletonMatchList()
                : CareerVisuals.Card(
                    Caption(emptyText),
                    padding: 16);

        return new MatchesParts(header, list);
    }
}
