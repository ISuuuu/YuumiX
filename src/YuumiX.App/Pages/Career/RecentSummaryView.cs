using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.SavedPlayers.Models;
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
        Action? onRecentTeammates = null,
        RecentTeammatesResult? teammates = null,
        bool loadingTeammates = false,
        Action<RecentTeammate>? onClickTeammate = null)
    {
        if (summary.IsEmpty)
        {
            return CareerVisuals.Card(
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    [
                        Caption("最近没有可统计的对局（重开局不计入统计）。")
                            .VAlign(VerticalAlignment.Center)
                            .Grid(row: 0, column: 0),
                        ComboBox(queueLabels, currentQueueIndex, onSelectQueue)
                            .Width(140)
                            .HAlign(HorizontalAlignment.Right)
                            .VAlign(VerticalAlignment.Center)
                            .Set(cb =>
                            {
                                if (cb.SelectedIndex != currentQueueIndex && currentQueueIndex >= 0 && currentQueueIndex < queueLabels.Length)
                                {
                                    cb.SelectedIndex = currentQueueIndex;
                                }
                            })
                            .Grid(row: 0, column: 1),
                    ]),
                padding: 12);
        }

        // 左侧 1：近期战绩与 KDA 概览
        Element statsCol = HStack(8,
            Caption($"近期对局（最近 {summary.Games} 场）")
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                .Foreground(Theme.PrimaryText)
                .VAlign(VerticalAlignment.Center),
            CareerVisuals.Stat("胜:", summary.Wins.ToString(System.Globalization.CultureInfo.InvariantCulture), CareerVisuals.WinText),
            CareerVisuals.Stat("负:", summary.Losses.ToString(System.Globalization.CultureInfo.InvariantCulture), CareerVisuals.LoseText),
            KdaStat(summary));

        var leftItems = new List<Element> { statsCol };

        // 左侧 2：常玩位置
        if (summary.PositionGames > 0)
        {
            Element positionsContainer = HStack(6,
                Caption("常玩位置:")
                    .Foreground(Theme.SecondaryText)
                    .VAlign(VerticalAlignment.Center),
                HStack(4, summary.Positions
                    .Take(TopPositionCount)
                    .Select(pos => PositionChip(pos, palette)
                        .WithKey(pos.Position.ToString()))
                    .ToArray()));

            leftItems.Add(positionsContainer.WithToolTip(
                VStack(4,
                    Caption("常玩位置分布")
                        .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
                    VStack(2, summary.Positions.Select(pos =>
                        HStack(8,
                            Caption(CareerVisuals.PositionText(pos.Position))
                                .Width(40),
                            Caption($"{pos.Games} 场")
                                .Foreground(Theme.SecondaryText)
                                .Width(40),
                            Caption($"{pos.Percentage}%")
                                .Foreground(palette.Accent)
                                .Width(40)
                        ).WithKey(pos.Position.ToString())).ToArray())
                )));
        }

        // 左侧 3：常用英雄（取前 6 名）
        if (summary.TopChampions.Count > 0)
        {
            leftItems.Add(HStack(4, summary.TopChampions
                .Take(6)
                .Select(champ => ChampionCell(champ, catalog, palette)
                    .WithKey(champ.ChampionId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray()));
        }

        Element leftSection = HStack(12, leftItems.ToArray())
            .VAlign(VerticalAlignment.Center);

        // 右侧操作：最近队友按钮（带弹层） + 队列下拉
        Element teammatesButton = Button(Caption("最近队友"), () => onRecentTeammates?.Invoke())
            .Height(30)
            .Padding(10, 0)
            .AutomationName("最近队友");

        Element flyoutButton = Flyout(
            teammatesButton,
            TeammatesFlyout(teammates, loadingTeammates, palette, onClickTeammate))
            .Opened(() => onRecentTeammates?.Invoke());

        Element rightSection = HStack(8,
            flyoutButton,
            ComboBox(queueLabels, currentQueueIndex, onSelectQueue)
                .Width(140)
                .AutomationName("队列筛选")
                .Set(cb =>
                {
                    if (cb.SelectedIndex != currentQueueIndex && currentQueueIndex >= 0 && currentQueueIndex < queueLabels.Length)
                    {
                        cb.SelectedIndex = currentQueueIndex;
                    }
                }))
            .VAlign(VerticalAlignment.Center)
            .HAlign(HorizontalAlignment.Right);

        Element barGrid = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                leftSection.Grid(row: 0, column: 0),
                rightSection.Grid(row: 0, column: 1),
            ]);

        return CareerVisuals.Card(barGrid, padding: 10);
    }

    private static Element TeammatesFlyout(
        RecentTeammatesResult? teammates,
        bool loadingTeammates,
        YuumiPalette palette,
        Action<RecentTeammate>? onClickTeammate)
    {
        Element inner;
        if (loadingTeammates)
        {
            inner = Caption("正在计算最近 20 局同队队友…")
                .Foreground(Theme.SecondaryText)
                .HAlign(HorizontalAlignment.Center);
        }
        else if (teammates is null || teammates.Summoners.Count == 0)
        {
            inner = Caption("暂无最近同队玩家数据（需在近期对局中存在同队玩家）。")
                .Foreground(Theme.SecondaryText);
        }
        else
        {
            inner = VStack(6,
                teammates.Summoners.Select(tm =>
                    TeammateCard(tm, palette, onClickTeammate)
                        .WithKey(tm.Puuid))
                    .ToArray());
        }

        return Border(
            VStack(10,
                HStack(8,
                    BodyStrong("最近同队玩家"),
                    Caption("点击可查看战绩")
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)),
                inner))
            .Width(380)
            .Padding(12);
    }

    private static Element TeammateCard(
        RecentTeammate tm,
        YuumiPalette palette,
        Action<RecentTeammate>? onClickTeammate)
    {
        string playTimeText = tm.LastPlayTime > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(tm.LastPlayTime).ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : "--";

        Element leftAvatar = Search.SearchVisuals.Icon(
            CareerVisuals.ProfileIcon(tm.ProfileIconId),
            36, 18).Grid(row: 0, column: 0);

        Element centerInfo = VStack(2,
            HStack(6,
                Caption(tm.Name)
                    .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                    .Foreground(Theme.PrimaryText),
                tm.Tag is not null
                    ? Border(Caption(tm.Tag).Foreground(palette.Accent))
                        .Padding(4, 1)
                        .CornerRadius(3)
                        .Background(CareerVisuals.WithAlpha(palette.Accent, 0x1E))
                    : Empty()
            ),
            Caption($"最近相遇: {playTimeText}")
                .Foreground(Theme.SecondaryText)
        ).Margin(8, 0, 0, 0).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 1);

        Element rightStats = HStack(6,
            CareerVisuals.Stat("总:", tm.Total.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            CareerVisuals.Stat("胜:", tm.Wins.ToString(System.Globalization.CultureInfo.InvariantCulture), CareerVisuals.WinText),
            CareerVisuals.Stat("负:", tm.Losses.ToString(System.Globalization.CultureInfo.InvariantCulture), CareerVisuals.LoseText)
        ).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 2);

        return Border(
            Button(
                Grid(
                    [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    [leftAvatar, centerInfo, rightStats])
                    .HAlign(HorizontalAlignment.Stretch),
                () => onClickTeammate?.Invoke(tm))
                .HAlign(HorizontalAlignment.Stretch)
                .Background(Theme.CardBackground)
                .AutomationName($"查看 {tm.Name} 战绩"))
            .CornerRadius(6)
            .WithBorder(Theme.CardStroke, 1);
    }

    private static Element KdaStat(RecentFormSummary summary)
    {
        Element deaths = Caption(summary.Deaths.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
            .Foreground(CareerVisuals.LoseText);

        return HStack(4,
            Caption("KDA:").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
            Caption($"{summary.Kills} /")
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
            deaths,
            Caption($"/ {summary.Assists}")
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
            Caption($"({summary.KdaText})").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center));
    }

    private static Element PositionChip(RecentPositionStat position, YuumiPalette palette) =>
        Border(Caption($"{CareerVisuals.PositionText(position.Position)} {position.Percentage}%")
                .Foreground(palette.Accent))
            .Padding(7, 2)
            .CornerRadius(4)
            .Background(CareerVisuals.WithAlpha(palette.Accent, 0x14))
            .WithBorder(CareerVisuals.WithAlpha(palette.Accent, 0x2A), 1);

    /// <summary>低胜率徽标色（裁决 D5）：旧版用灰蓝 #94A3B8 而不是失败红——低胜率不是"失败"，只是平淡。</summary>
    private const string LowWinRateColor = "#FF94A3B8";

    private static Element ChampionCell(RecentChampionStat champion, GameDataCatalog catalog, YuumiPalette palette)
    {
        double wr = champion.Games > 0 ? (champion.Wins * 100.0 / champion.Games) : 0;
        string badgeBg = wr >= 60
            ? CareerVisuals.WithAlpha(CareerVisuals.WinText, 0x33)
            : (wr < 45
                ? CareerVisuals.WithAlpha(LowWinRateColor, 0x33)
                : CareerVisuals.WithAlpha(palette.Accent, 0x14));

        string badgeFg = wr >= 60
            ? CareerVisuals.WinText
            : (wr < 45
                ? LowWinRateColor
                : palette.Accent);

        string champName = catalog.Champions.TryGetValue(champion.ChampionId, out string? name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : $"英雄 {champion.ChampionId}";
        string posText = champion.PrimaryPosition is { } p ? CareerVisuals.PositionText(p) : "--";

        Element cell = Grid(
            [GridSize.Auto],
            [GridSize.Auto],
            [
                Search.SearchVisuals.Icon(
                    Search.SearchVisuals.ChampionIcon(champion.ChampionId),
                    ChampionSize,
                    ChampionSize / 2),
                Border(Caption(champion.Games.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
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

        return cell.WithToolTip(
            VStack(4,
                Caption(champName)
                    .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
                Caption($"出场 {champion.Games} 次 · 主位置 {posText}")
                    .Foreground(Theme.SecondaryText),
                HStack(6,
                    Caption($"胜率: {champion.WinRateText}")
                        .Foreground(wr >= 60 ? CareerVisuals.WinText : (wr < 45 ? LowWinRateColor : palette.Accent)),
                    Caption($"({champion.Wins}胜 {champion.Losses}负)")
                        .Foreground(Theme.SecondaryText)),
                Caption($"KDA: {champion.KdaText} ({champion.Kills}/{champion.Deaths}/{champion.Assists})")
                    .Foreground(palette.Accent)));
    }
}
