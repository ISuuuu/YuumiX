using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Career.Models;

namespace YuumiX.App.Pages.Career;

/// <summary>
/// 排位段位表，对应原 <c>MatchHistoryTab.vue</c> 的 <c>.rank-table</c>。
/// 采用精准宽度 HStack 布局，严格对齐 docs/pic/生涯.png 设计，杜绝多列重叠。
/// </summary>
internal static class RankedTableView
{
    private const double ColTypeWidth = 105;
    private const double ColGamesWidth = 85;
    private const double ColWinRateWidth = 85;
    private const double ColWinsWidth = 80;
    private const double ColLossesWidth = 80;
    private const double ColTierWidth = 150;
    private const double ColLpWidth = 85;
    private const double ColHighestWidth = 115;
    private const double ColPreviousWidth = 115;

    private static Element HeaderCell(string text, double width) =>
        Caption(text)
            .Width(width)
            .Foreground(Theme.SecondaryText)
            .TextAlignment(TextAlignment.Center)
            .VAlign(VerticalAlignment.Center);

    private static Element DataCell(string text, double width, ThemeRef foreground)
    {
        return Caption(text)
            .Width(width)
            .TextAlignment(TextAlignment.Center)
            .VAlign(VerticalAlignment.Center)
            .Foreground(foreground);
    }

    private static Element DataCell(string text, double width, string? foreground = null)
    {
        var tb = Caption(text)
            .Width(width)
            .TextAlignment(TextAlignment.Center)
            .VAlign(VerticalAlignment.Center);

        return foreground is null ? tb : tb.Foreground(foreground);
    }

    /// <summary>分隔线：1 DIP 的浅色横线。</summary>
    private static Element Divider() =>
        Border(Empty()).Height(1).Background(Theme.CardStroke).HAlign(HorizontalAlignment.Stretch);

    public static Element Render(RankedStats stats, YuumiPalette palette)
    {
        Element header = HStack(0,
            HeaderCell("类型", ColTypeWidth),
            HeaderCell("总场次", ColGamesWidth),
            HeaderCell("胜率", ColWinRateWidth),
            HeaderCell("胜场", ColWinsWidth),
            HeaderCell("负场", ColLossesWidth),
            HeaderCell("段位", ColTierWidth),
            HeaderCell("胜点", ColLpWidth),
            HeaderCell("赛季最高", ColHighestWidth),
            HeaderCell("上赛季结算", ColPreviousWidth))
            .Padding(12, 10);

        return CareerVisuals.Card(
            VStack(0,
                header,
                Divider(),
                Row("单双排位", stats.SoloDuo, palette),
                Divider(),
                Row("灵活排位", stats.Flex, palette)),
            padding: 8);
    }

    private static Element Row(string label, RankedQueueEntry? queue, YuumiPalette palette)
    {
        string games = (queue?.TotalGames ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string winRate = queue?.WinRateText ?? "--";
        string wins = (queue?.Wins ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string losses = (queue?.Losses ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string tier = queue?.TierText ?? "--";
        string leaguePoints = (queue?.LeaguePoints ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string highest = queue?.HighestTierText ?? "--";
        string previous = queue?.PreviousSeasonTierText ?? "--";

        string? medalUri = CareerVisuals.RankedMedalUri(queue?.Tier);
        Element tierCell = medalUri is null
            ? Caption(tier)
                .Width(ColTierWidth)
                .Foreground(Theme.Accent)
                .TextAlignment(TextAlignment.Center)
                .VAlign(VerticalAlignment.Center)
            : HStack(6,
                    CareerVisuals.RankedMedal(queue?.Tier, 20),
                    Caption(tier)
                        .Foreground(Theme.Accent)
                        .VAlign(VerticalAlignment.Center))
                .Width(ColTierWidth)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center);

        return HStack(0,
            DataCell(label, ColTypeWidth),
            DataCell(games, ColGamesWidth),
            DataCell(winRate, ColWinRateWidth, foreground: queue?.TotalGames > 0 ? CareerVisuals.WinText : null),
            DataCell(wins, ColWinsWidth),
            DataCell(losses, ColLossesWidth),
            tierCell,
            DataCell(leaguePoints, ColLpWidth),
            DataCell(highest, ColHighestWidth, foreground: Theme.SecondaryText),
            DataCell(previous, ColPreviousWidth, foreground: Theme.SecondaryText))
            .Padding(12, 10);
    }
}
