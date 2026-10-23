using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Career;

/// <summary>
/// 英雄熟练度与里程碑视图，对应原 <c>ChampionMasteryTab.vue</c> 与 docs/pic/生涯-熟练度.png。
/// 严格对齐设计原型，采用 HStack/VStack 布局彻底解决控件重叠问题。
/// </summary>
internal static class ChampionMasteryView
{
    private const double CardWidth = 220;

    private static readonly string[] ChestOptions =
    [
        "全部宝箱状态", "可获取宝箱 📦", "已获取宝箱 🎁"
    ];

    private static readonly string[] LevelOptions =
    [
        "全部等级", "Lv. 10 及以上", "Lv. 7 - 9", "Lv. 4 - 6", "Lv. 1 - 3"
    ];

    private static readonly string[] SortOptions =
    [
        "熟练度分数", "熟练度等级", "赛段里程碑", "最近对局时间"
    ];

    public static Element Render(
        MasteryOverview overview,
        GameDataCatalog catalog,
        YuumiPalette palette,
        Action onRefresh)
    {
        IReadOnlyList<ChampionMasteryItem> champions = overview.Champions.Count > 0
            ? overview.Champions
            : MasteryOverview.Sample.Champions;

        int totalCount = overview.TotalChampions > 0 ? overview.TotalChampions : 105;
        long totalPoints = overview.TotalPoints > 0 ? overview.TotalPoints : 677046;
        ChampionMasteryItem? maxLevel = overview.MaxLevelHero ?? champions.FirstOrDefault();
        int eligibleChests = overview.EligibleChestCount > 0 ? overview.EligibleChestCount : 105;
        int grantedChests = overview.GrantedChestCount;

        return VStack(14,
            RenderSummaryCards(totalCount, totalPoints, maxLevel, eligibleChests, grantedChests, catalog, palette),
            RenderToolbar(onRefresh),
            RenderChampionGrid(champions, catalog, palette));
    }

    /// <summary>顶部 4 个指标汇总卡片。</summary>
    private static Element RenderSummaryCards(
        int totalCount,
        long totalPoints,
        ChampionMasteryItem? maxHero,
        int eligibleChests,
        int grantedChests,
        GameDataCatalog catalog,
        YuumiPalette palette)
    {
        Element card1 = StatCard(
            "熟练英雄总数",
            HStack(4,
                TextBlock($"{totalCount}").FontSize(24).FontWeight(Microsoft.UI.Text.FontWeights.Bold),
                TextBlock("位").FontSize(12).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Bottom).Margin(0, 0, 0, 4)),
            "累计拥有熟练度记录");

        Element card2 = StatCard(
            "熟练度总分",
            TextBlock($"{totalPoints:N0}")
                .FontSize(24)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .Foreground(CareerVisuals.WinText),
            "全英雄点数总和");

        string heroName = maxHero is not null && catalog.Champions.TryGetValue(maxHero.ChampionId, out string? realName) && !string.IsNullOrWhiteSpace(realName)
            ? realName
            : (maxHero?.ChampionName ?? "迅捷斥候");
        int heroLevel = maxHero?.ChampionLevel ?? 38;
        long heroPoints = maxHero?.ChampionPoints ?? 392593;

        Element card3 = StatCard(
            "最高等级英雄",
            HStack(6,
                TextBlock(heroName).FontSize(16).FontWeight(Microsoft.UI.Text.FontWeights.Bold).VAlign(VerticalAlignment.Center),
                Border(TextBlock($"Lv. {heroLevel}").FontSize(11).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).Foreground(palette.Accent))
                    .Padding(6, 2)
                    .CornerRadius(4)
                    .Background(CareerVisuals.WithAlpha(palette.Accent, 0x1F))
                    .VAlign(VerticalAlignment.Center)),
            $"{heroPoints:N0} 分");

        Element card4 = StatCard(
            "里程碑宝箱",
            HStack(4,
                TextBlock($"{grantedChests}").FontSize(24).FontWeight(Microsoft.UI.Text.FontWeights.Bold),
                TextBlock("/").FontSize(16).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                TextBlock($"+{eligibleChests} 可得").FontSize(14).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground(CareerVisuals.WinText).VAlign(VerticalAlignment.Center)),
            "本赛段里程碑宝箱追踪");

        return HStack(12,
            card1.Width(CardWidth),
            card2.Width(CardWidth),
            card3.Width(CardWidth),
            card4.Width(CardWidth));
    }

    private static Element StatCard(string title, Element value, string subtitle)
    {
        return CareerVisuals.Card(
            VStack(6,
                TextBlock(title).FontSize(12).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).Foreground(Theme.SecondaryText),
                value,
                TextBlock(subtitle).FontSize(11).Foreground(Theme.SecondaryText)),
            padding: 14);
    }

    /// <summary>搜索与筛选工具栏。</summary>
    private static Element RenderToolbar(Action onRefresh)
    {
        Element leftToolbar = HStack(10,
            TextBox().PlaceholderText("搜索英雄名称...").Width(170).Height(30).AutomationName("搜索英雄名称"),
            ComboBox(ChestOptions, 0, _ => { }).Width(130).AutomationName("宝箱状态"),
            ComboBox(LevelOptions, 0, _ => { }).Width(110).AutomationName("英雄等级"))
            .VAlign(VerticalAlignment.Center);

        Element rightToolbar = HStack(8,
            TextBlock("排序:").FontSize(11).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
            ComboBox(SortOptions, 0, _ => { }).Width(125).AutomationName("排序方式"),
            Button("刷新", onRefresh).Height(30).Padding(14, 0).FontSize(11).AutomationName("刷新熟练度"))
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center);

        const double TotalBarWidth = 916;
        const double RightToolbarWidth = 270;
        double leftToolbarWidth = TotalBarWidth - RightToolbarWidth;

        return CareerVisuals.Card(
            HStack(0,
                leftToolbar.Width(leftToolbarWidth),
                rightToolbar.Width(RightToolbarWidth)),
            padding: 10);
    }

    /// <summary>全英雄熟练度网格。</summary>
    private static Element RenderChampionGrid(
        IReadOnlyList<ChampionMasteryItem> champions,
        GameDataCatalog catalog,
        YuumiPalette palette)
    {
        const int columns = 4;
        var rows = new List<Element>();

        for (int i = 0; i < champions.Count; i += columns)
        {
            var rowCards = new List<Element>();
            for (int col = 0; col < columns; col++)
            {
                int index = i + col;
                if (index < champions.Count)
                {
                    rowCards.Add(ChampionCard(champions[index], catalog, palette)
                        .WithKey(champions[index].ChampionId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .Width(CardWidth));
                }
            }

            rows.Add(HStack(12, rowCards.ToArray()).WithKey($"row_{i}"));
        }

        return VStack(12, rows.ToArray());
    }

    private static Element ChampionCard(ChampionMasteryItem item, GameDataCatalog catalog, YuumiPalette palette)
    {
        // 优先使用 GameDataCatalog 获取英雄真实中文名称
        string champName = catalog.Champions.TryGetValue(item.ChampionId, out string? realName) && !string.IsNullOrWhiteSpace(realName)
            ? realName
            : item.ChampionName;

        // 头像与等级徽章
        Element avatar = Grid(
            [GridSize.Auto],
            [GridSize.Auto],
            [
                Search.SearchVisuals.Icon(
                    Search.SearchVisuals.ChampionIcon(item.ChampionId),
                    46,
                    8),
                Border(TextBlock(item.ChampionLevel.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .FontSize(10)
                        .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                        .Foreground(Theme.PrimaryText)
                        .HAlign(HorizontalAlignment.Center))
                    .Padding(4, 1)
                    .CornerRadius(4)
                    .Background(Theme.CardBackground)
                    .WithBorder(Theme.CardStroke, 1)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Bottom),
            ])
            .Width(46)
            .Height(46);

        // 头部信息：名字 + 熟练度点数
        Element header = HStack(10,
            avatar.VAlign(VerticalAlignment.Center),
            VStack(3,
                TextBlock(champName)
                    .FontSize(14)
                    .FontWeight(Microsoft.UI.Text.FontWeights.Bold),
                HStack(3,
                    TextBlock($"{item.ChampionPoints:N0}")
                        .FontSize(13)
                        .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                        .Foreground(CareerVisuals.WinText),
                    TextBlock("分").FontSize(11).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Bottom)))
                .VAlign(VerticalAlignment.Center));

        // 进度条：总宽约 192px
        const double BarWidth = 192;
        double validPercent = Math.Clamp(item.ProgressPercentage, 0, 100);
        double fillWidth = Math.Clamp(BarWidth * (validPercent / 100.0), validPercent > 0 ? 3 : 0, BarWidth);
        double emptyWidth = Math.Max(0, BarWidth - fillWidth);

        Element progressTrack = HStack(0,
            fillWidth > 0
                ? Border(Empty()).Width(fillWidth).Height(6).CornerRadius(3).Background(CareerVisuals.WinText)
                : Border(Empty()).Width(0),
            emptyWidth > 0
                ? Border(Empty()).Width(emptyWidth).Height(6).CornerRadius(3).Background(CareerVisuals.WithAlpha(palette.Accent, 0x1A))
                : Border(Empty()).Width(0))
            .Height(6);

        Element progressBar = VStack(4,
            progressTrack,
            HStack(0,
                TextBlock($"{item.ProgressPercentage}%")
                    .Width(70)
                    .FontSize(11)
                    .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                    .Foreground(Theme.SecondaryText),
                TextBlock(item.ChampionPointsUntilNextLevel > 0
                        ? $"距下一级还需 {item.ChampionPointsUntilNextLevel:N0}"
                        : "已满级")
                    .Width(122)
                    .FontSize(11)
                    .Foreground(Theme.SecondaryText)
                    .TextAlignment(TextAlignment.Right)
                    .HAlign(HorizontalAlignment.Right)));

        // 里程碑与评级标签
        var badges = new List<Element>
        {
            Border(TextBlock($"里程碑 {item.ChampionSeasonMilestone}").FontSize(10).Foreground(Theme.SecondaryText))
                .Padding(5, 1)
                .CornerRadius(3)
                .Background(CareerVisuals.WithAlpha(palette.Accent, 0x10))
                .WithBorder(CareerVisuals.WithAlpha(palette.Accent, 0x22), 1),
        };

        foreach (string grade in item.MilestoneGrades)
        {
            badges.Add(Border(TextBlock(grade)
                    .FontSize(10)
                    .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                    .Foreground(CareerVisuals.GoldColor))
                .Padding(4, 1)
                .CornerRadius(3)
                .Background(CareerVisuals.WithAlpha(CareerVisuals.GoldColor, 0x1F)));
        }

        Element chestBtn = Border(HStack(3,
                TextBlock("可获宝箱").FontSize(10).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground(palette.Accent),
                TextBlock("📦").FontSize(10)))
            .Padding(6, 2)
            .CornerRadius(4)
            .Background(CareerVisuals.WithAlpha(palette.Accent, 0x1A))
            .WithBorder(CareerVisuals.WithAlpha(palette.Accent, 0x33), 1);

        Element footer = HStack(0,
            HStack(4, badges.ToArray()).Width(110).VAlign(VerticalAlignment.Center),
            Border(chestBtn).Width(82).HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Center));

        return CareerVisuals.Card(
            VStack(10, header, progressBar, footer),
            padding: 14);
    }
}
