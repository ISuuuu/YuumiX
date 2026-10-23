using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Career;

/// <summary>
/// 近期对局看板，对应原 <c>MatchHistoryTab.vue</c> 的 <c>.recent-summary-bar</c>。
/// 严格对齐 docs/pic/生涯.png：
/// [近期对局（最近 20 场） 胜:13 负:7 KDA: 84 / 77 / 101 (2.4)] | [常玩位置: 上路 53% 中路 24%] | [常用英雄头像] | 右侧：[最近队友] [全部对局下拉]
/// </summary>
internal static class RecentSummaryView
{
    private const double ChampionSize = 32;
    private const int TopPositionCount = 2;

    public static Element Render(
        RecentFormSummary summary,
        GameDataCatalog catalog,
        YuumiPalette palette,
        string[] queueLabels,
        int currentQueueIndex,
        Action<int> onSelectQueue,
        Action? onRecentTeammates = null)
    {
        const double TotalBarWidth = 920;
        const double RightSectionWidth = 220;
        double leftSectionWidth = TotalBarWidth - RightSectionWidth;

        if (summary.IsEmpty)
        {
            return CareerVisuals.Card(
                HStack(0,
                    Caption("最近没有可统计的对局（重开局不计入统计）。")
                        .Width(leftSectionWidth)
                        .VAlign(VerticalAlignment.Center),
                    ComboBox(queueLabels, currentQueueIndex, onSelectQueue)
                        .Width(120)
                        .HAlign(HorizontalAlignment.Right)
                        .VAlign(VerticalAlignment.Center)),
                padding: 12);
        }

        // 左侧 1：近期战绩与 KDA 概览
        Element statsCol = HStack(8,
            TextBlock($"近期对局（最近 {summary.Games} 场）")
                .FontSize(12)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .Foreground(Theme.PrimaryText)
                .VAlign(VerticalAlignment.Center),
            CareerVisuals.Stat("胜:", summary.Wins.ToString(System.Globalization.CultureInfo.InvariantCulture), CareerVisuals.WinText),
            CareerVisuals.Stat("负:", summary.Losses.ToString(System.Globalization.CultureInfo.InvariantCulture), CareerVisuals.LoseText),
            KdaStat(summary));

        var leftItems = new List<Element> { statsCol };

        // 左侧 2：常玩位置
        if (summary.PositionGames > 0)
        {
            leftItems.Add(HStack(6,
                TextBlock("常玩位置:")
                    .FontSize(11)
                    .Foreground(Theme.SecondaryText)
                    .VAlign(VerticalAlignment.Center),
                HStack(4, summary.Positions
                    .Take(TopPositionCount)
                    .Select(pos => PositionChip(pos, palette)
                        .WithKey(pos.Position.ToString()))
                    .ToArray())));
        }

        // 左侧 3：常用英雄（取前 4 名）
        if (summary.TopChampions.Count > 0)
        {
            leftItems.Add(HStack(4, summary.TopChampions
                .Take(4)
                .Select(champ => ChampionCell(champ, palette)
                    .WithKey(champ.ChampionId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray()));
        }

        Element leftSection = HStack(12, leftItems.ToArray())
            .Width(leftSectionWidth)
            .VAlign(VerticalAlignment.Center);

        // 右侧操作：最近队友按钮 + 队列下拉
        Element rightSection = HStack(8,
            Button("最近队友", () => onRecentTeammates?.Invoke())
                .Height(30)
                .Padding(10, 0)
                .FontSize(11)
                .AutomationName("最近队友"),
            ComboBox(queueLabels, currentQueueIndex, onSelectQueue)
                .Width(110)
                .AutomationName("队列筛选"))
            .Width(RightSectionWidth)
            .VAlign(VerticalAlignment.Center)
            .HAlign(HorizontalAlignment.Right);

        return CareerVisuals.Card(
            HStack(0, leftSection, rightSection),
            padding: 10);
    }

    private static Element KdaStat(RecentFormSummary summary)
    {
        Element deaths = TextBlock(summary.Deaths.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .FontSize(12)
            .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
            .Foreground(CareerVisuals.LoseText);

        return HStack(4,
            TextBlock("KDA:").FontSize(11).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
            TextBlock($"{summary.Kills} /").FontSize(12)
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
            deaths,
            TextBlock($"/ {summary.Assists}").FontSize(12)
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
            TextBlock($"({summary.KdaText})").FontSize(11).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center));
    }

    private static Element PositionChip(RecentPositionStat position, YuumiPalette palette) =>
        Border(TextBlock($"{CareerVisuals.PositionText(position.Position)} {position.Percentage}%")
                .FontSize(11)
                .Foreground(palette.Accent))
            .Padding(7, 2)
            .CornerRadius(4)
            .Background(CareerVisuals.WithAlpha(palette.Accent, 0x14))
            .WithBorder(CareerVisuals.WithAlpha(palette.Accent, 0x2A), 1);

    private static Element ChampionCell(RecentChampionStat champion, YuumiPalette palette)
    {
        double wr = champion.Games > 0 ? (champion.Wins * 100.0 / champion.Games) : 0;
        string badgeBg = wr >= 60
            ? CareerVisuals.WithAlpha(CareerVisuals.WinText, 0x33)
            : (wr < 45
                ? CareerVisuals.WithAlpha(CareerVisuals.LoseText, 0x33)
                : CareerVisuals.WithAlpha(palette.Accent, 0x14));

        string badgeFg = wr >= 60
            ? CareerVisuals.WinText
            : (wr < 45
                ? CareerVisuals.LoseText
                : palette.Accent);

        return Grid(
            [GridSize.Auto],
            [GridSize.Auto],
            [
                Search.SearchVisuals.Icon(
                    Search.SearchVisuals.ChampionIcon(champion.ChampionId),
                    ChampionSize,
                    ChampionSize / 2),
                Border(TextBlock(champion.Games.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .FontSize(9)
                        .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                        .Foreground(badgeFg)
                        .HAlign(HorizontalAlignment.Center))
                    .Padding(3, 0)
                    .CornerRadius(3)
                    .Background(badgeBg)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Bottom),
            ])
            .Width(ChampionSize)
            .Height(ChampionSize);
    }
}
