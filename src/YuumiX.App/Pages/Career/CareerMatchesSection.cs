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
    internal static Element Render(
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
        Action<MatchSummary> onOpenMatchInSearch)
    {
        // 旧版：排位表与近期看板各有 1rem（16）的下外边距，卡片列表之间 gap 10（4px 网格取 8）
        return VStack(16,
            RankedTableView.Render(ranked, palette),
            RecentSummaryView.Render(
                summary,
                catalog,
                palette,
                filterLabels,
                filter,
                onFilterChanged,
                onRecentTeammates: onRecentTeammates,
                teammates: teammates,
                loadingTeammates: loadingTeammates,
                onClickTeammate: onClickTeammate),
            visibleMatches.Count > 0
                ? VStack(8, visibleMatches
                    .Select(match => Component<CareerMatchRow, CareerMatchRowProps>(
                        new CareerMatchRowProps(match, catalog, palette, () => onOpenMatchInSearch(match)))
                        .WithKey(match.GameId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    .ToArray())
                : CareerVisuals.Card(
                    Caption(loading
                        ? "正在读取最近对局…"
                        : filter == 0
                            ? "没有可显示的对局（客户端未连接，或该账号近期没有对局）"
                            : "该队列下没有近期对局"),
                    padding: 16));
    }
}
