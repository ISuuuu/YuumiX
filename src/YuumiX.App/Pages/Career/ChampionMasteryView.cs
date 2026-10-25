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
        Action onRefresh,
        string searchQuery = "",
        Action<string>? onSearchChanged = null,
        int chestFilterIndex = 0,
        Action<int>? onChestFilterChanged = null,
        int levelFilterIndex = 0,
        Action<int>? onLevelFilterChanged = null,
        int sortIndex = 0,
        Action<int>? onSortChanged = null)
    {
        // D1：概览为空给普通空态；"读取失败"的提示（获取英雄熟练度失败）由页面在概览为 null 时给出。
        if (overview.Champions.Count == 0)
        {
            return CareerVisuals.Card(
                VStack(10,
                    Body("该账号暂无英雄熟练度记录")
                        .Foreground(Theme.SecondaryText),
                    Button(Caption("重试"), onRefresh).Height(30).Padding(14, 0).AutomationName("重试加载熟练度")),
                padding: 24);
        }

        IReadOnlyList<ChampionMasteryItem> champions = FilterAndSort(
            overview.Champions,
            searchQuery,
            chestFilterIndex,
            levelFilterIndex,
            sortIndex,
            catalog);

        int totalCount = overview.TotalChampions;
        long totalPoints = overview.TotalPoints;
        ChampionMasteryItem? maxLevel = overview.MaxLevelHero;
        int eligibleChests = overview.EligibleChestCount;
        int grantedChests = overview.GrantedChestCount;

        return VStack(14,
            RenderSummaryCards(totalCount, totalPoints, maxLevel, eligibleChests, grantedChests, catalog, palette),
            RenderToolbar(
                onRefresh,
                searchQuery,
                onSearchChanged,
                chestFilterIndex,
                onChestFilterChanged,
                levelFilterIndex,
                onLevelFilterChanged,
                sortIndex,
                onSortChanged),
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
                Title($"{totalCount}"),
                Caption("位").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Bottom).Margin(0, 0, 0, 4)),
            "累计拥有熟练度记录");

        Element card2 = StatCard(
            "熟练度总分",
            Title($"{totalPoints:N0}")
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
                BodyLarge(heroName).SemiBold().VAlign(VerticalAlignment.Center),
                Border(Caption($"Lv. {heroLevel}").SemiBold().Foreground(Theme.Accent))
                    .Padding(6, 2)
                    .CornerRadius(4)
                    .Background(Theme.SubtleFill)
                    .VAlign(VerticalAlignment.Center)),
            $"{heroPoints:N0} 分");

        Element card4 = StatCard(
            "里程碑宝箱",
            HStack(4,
                Title($"{grantedChests}"),
                BodyLarge("/").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                BodyStrong($"+{eligibleChests} 可得").Foreground(CareerVisuals.WinText).VAlign(VerticalAlignment.Center)),
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
                Caption(title).Foreground(Theme.SecondaryText),
                value,
                Caption(subtitle).Foreground(Theme.SecondaryText)),
            padding: 14);
    }

    /// <summary>搜索与筛选工具栏。</summary>
    private static Element RenderToolbar(
        Action onRefresh,
        string searchQuery,
        Action<string>? onSearchChanged,
        int chestFilterIndex,
        Action<int>? onChestFilterChanged,
        int levelFilterIndex,
        Action<int>? onLevelFilterChanged,
        int sortIndex,
        Action<int>? onSortChanged)
    {
        Element leftToolbar = HStack(10,
            TextBox(searchQuery, text => onSearchChanged?.Invoke(text), "搜索英雄名称...")
                .Width(170)
                .Height(30)
                .AutomationName("搜索英雄名称"),
            ComboBox(ChestOptions, chestFilterIndex, idx => onChestFilterChanged?.Invoke(idx))
                .Width(130)
                .AutomationName("宝箱状态"),
            ComboBox(LevelOptions, levelFilterIndex, idx => onLevelFilterChanged?.Invoke(idx))
                .Width(110)
                .AutomationName("英雄等级"))
            .VAlign(VerticalAlignment.Center);

        Element rightToolbar = HStack(8,
            Caption("排序:").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
            ComboBox(SortOptions, sortIndex, idx => onSortChanged?.Invoke(idx))
                .Width(125)
                .AutomationName("排序方式"),
            Button(Caption("刷新"), onRefresh).Height(30).Padding(14, 0).AutomationName("刷新熟练度"))
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

    public static IReadOnlyList<ChampionMasteryItem> FilterAndSort(
        IReadOnlyList<ChampionMasteryItem> items,
        string searchQuery,
        int chestFilterIndex,
        int levelFilterIndex,
        int sortIndex,
        GameDataCatalog catalog)
    {
        IEnumerable<ChampionMasteryItem> result = items;

        // 英雄名称搜索
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            string q = searchQuery.Trim();
            result = result.Where(i =>
            {
                string name = catalog.Champions.TryGetValue(i.ChampionId, out string? realName) && !string.IsNullOrWhiteSpace(realName)
                    ? realName
                    : i.ChampionName;
                return name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                       i.ChampionId.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(q);
            });
        }

        // 宝箱状态: 0:全部, 1:可获取 (chestGranted == false), 2:已获取 (chestGranted == true)
        if (chestFilterIndex == 1)
        {
            result = result.Where(i => !i.ChestGranted);
        }
        else if (chestFilterIndex == 2)
        {
            result = result.Where(i => i.ChestGranted);
        }

        // 等级区间: 0:全部, 1:>=10, 2:7-9, 3:4-6, 4:1-3
        if (levelFilterIndex == 1)
        {
            result = result.Where(i => i.ChampionLevel >= 10);
        }
        else if (levelFilterIndex == 2)
        {
            result = result.Where(i => i.ChampionLevel >= 7 && i.ChampionLevel <= 9);
        }
        else if (levelFilterIndex == 3)
        {
            result = result.Where(i => i.ChampionLevel >= 4 && i.ChampionLevel <= 6);
        }
        else if (levelFilterIndex == 4)
        {
            result = result.Where(i => i.ChampionLevel >= 1 && i.ChampionLevel <= 3);
        }

        // 排序方式: 0:熟练度分数, 1:熟练度等级, 2:赛段里程碑, 3:最近对局时间
        result = sortIndex switch
        {
            1 => result.OrderByDescending(i => i.ChampionLevel).ThenByDescending(i => i.ChampionPoints),
            2 => result.OrderByDescending(i => i.ChampionSeasonMilestone).ThenByDescending(i => i.ChampionPoints),
            3 => result.OrderByDescending(i => i.LastPlayTime).ThenByDescending(i => i.ChampionPoints),
            _ => result.OrderByDescending(i => i.ChampionPoints),
        };

        return result.ToList();
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
                Border(Caption(item.ChampionLevel.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .SemiBold()
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
                BodyStrong(champName),
                HStack(3,
                    BodyStrong($"{item.ChampionPoints:N0}")
                        .Foreground(CareerVisuals.WinText),
                    Caption("分").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Bottom)))
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
                ? Border(Empty()).Width(emptyWidth).Height(6).CornerRadius(3).Background(Theme.SubtleFill)
                : Border(Empty()).Width(0))
            .Height(6);

        Element progressBar = VStack(4,
            progressTrack,
            HStack(0,
                Caption($"{item.ProgressPercentage}%")
                    .Width(70)
                    .Foreground(Theme.SecondaryText),
                Caption(item.ChampionPointsUntilNextLevel > 0
                        ? $"距下一级还需 {item.ChampionPointsUntilNextLevel:N0}"
                        : "已满级")
                    .Width(122)
                    .Foreground(Theme.SecondaryText)
                    .TextAlignment(TextAlignment.Right)
                    .HAlign(HorizontalAlignment.Right)));

        // 里程碑与评级标签
        var badges = new List<Element>
        {
            Border(Caption($"里程碑 {item.ChampionSeasonMilestone}").Foreground(Theme.SecondaryText))
                .Padding(5, 1)
                .CornerRadius(3)
                .Background(Theme.SubtleFill)
                .WithBorder(Theme.CardStroke, 1),
        };

        foreach (string grade in item.MilestoneGrades)
        {
            badges.Add(Border(Caption(grade)
                    .Foreground(CareerVisuals.GoldColor))
                .Padding(4, 1)
                .CornerRadius(3)
                .Background(CareerVisuals.WithAlpha(CareerVisuals.GoldColor, 0x1F)));
        }

        Element chestBtn = Border(HStack(3,
                Caption("可获宝箱").Foreground(Theme.Accent),
                Caption("📦")))
            .Padding(6, 2)
            .CornerRadius(4)
            .Background(Theme.SubtleFill)
            .WithBorder(Theme.CardStroke, 1);

        Element footer = HStack(0,
            HStack(4, badges.ToArray()).Width(110).VAlign(VerticalAlignment.Center),
            Border(chestBtn).Width(82).HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Center));

        return CareerVisuals.Card(
            VStack(10, header, progressBar, footer),
            padding: 14);
    }
}
