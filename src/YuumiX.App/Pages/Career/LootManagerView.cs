using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Career.Models;

namespace YuumiX.App.Pages.Career;

/// <summary>
/// 战利品管理视图，对应原 <c>LootManagerTab.vue</c> 与 docs/pic/生涯-战利品.png。
/// 包含两块主要面板：
/// 1. 材料面板：宝箱/法球/钥匙碎片开启与合成；
/// 2. 碎片库存管理面板：类型/拥有状态/价值过滤与批量操作栏。
/// 彻底解决多列与头部重叠问题。
/// </summary>
internal static class LootManagerView
{
    private const double CardWidth = 220;

    public static readonly string[] TypeFilters =
    [
        "全部", "英雄", "皮肤", "表情", "守卫", "图标", "材料/宝箱"
    ];

    public static readonly string[] OwnedFilters =
    [
        "全部", "已拥有", "未拥有"
    ];

    public static readonly string[] ValueTypeFilters =
    [
        "分解收益", "商城售价"
    ];

    public static readonly string[] OperatorFilters =
    [
        "小于等于 (<=)", "大于等于 (>=)", "等于 (=)"
    ];

    public static Element Render(
        PlayerLootOverview loot,
        YuumiPalette palette,
        Action onRefresh,
        Action onOpenAll,
        Action<OpenableLootItem>? onOpenItem = null,
        Action? onForgeKey = null,
        int typeFilterIndex = 0,
        Action<int>? onTypeFilterChanged = null,
        int ownedFilterIndex = 0,
        Action<int>? onOwnedFilterChanged = null,
        int valueTypeIndex = 0,
        Action<int>? onValueTypeChanged = null,
        int operatorIndex = 0,
        Action<int>? onOperatorChanged = null,
        string valueThreshold = "",
        Action<string>? onValueThresholdChanged = null,
        IReadOnlySet<string>? selectedLootIds = null,
        Action<string>? onToggleSelect = null,
        Action? onSelectAllFiltered = null,
        Action? onClearSelection = null,
        Action? onDisenchant = null,
        Action? onReroll = null,
        Action? onUpgrade = null,
        LootOperationProgress? progress = null,
        string? operationNotice = null)
    {
        return VStack(14,
            RenderMaterialsSection(loot, palette, onRefresh, onOpenAll, onOpenItem, onForgeKey),
            RenderInventorySection(
                loot,
                palette,
                onRefresh,
                typeFilterIndex,
                onTypeFilterChanged,
                ownedFilterIndex,
                onOwnedFilterChanged,
                valueTypeIndex,
                onValueTypeChanged,
                operatorIndex,
                onOperatorChanged,
                valueThreshold,
                onValueThresholdChanged,
                selectedLootIds,
                onToggleSelect,
                onSelectAllFiltered,
                onClearSelection,
                onDisenchant,
                onReroll,
                onUpgrade,
                progress,
                operationNotice));
    }

    /// <summary>区块一：材料（可开启战利品）卡片面板。</summary>
    private static Element RenderMaterialsSection(
        PlayerLootOverview loot,
        YuumiPalette palette,
        Action onRefresh,
        Action onOpenAll,
        Action<OpenableLootItem>? onOpenItem,
        Action? onForgeKey)
    {
        const double TotalWidth = 916;
        const double RightBtnWidth = 110;
        double leftHeaderWidth = TotalWidth - RightBtnWidth;

        Element leftHeader = HStack(12,
            TextBlock("📦 材料")
                .FontSize(15)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .VAlign(VerticalAlignment.Center),
            Button("刷新列表", onRefresh).Height(30).Padding(12, 0).FontSize(11).AutomationName("刷新材料列表"),
            Border(HStack(4,
                    TextBlock("🔑").FontSize(11),
                    TextBlock($"x{loot.TotalKeys}").FontSize(11).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground(CareerVisuals.GoldColor)))
                .Padding(8, 3)
                .CornerRadius(4)
                .Background(CareerVisuals.WithAlpha(CareerVisuals.GoldColor, 0x1F))
                .WithBorder(CareerVisuals.WithAlpha(CareerVisuals.GoldColor, 0x33), 1)
                .VAlign(VerticalAlignment.Center))
            .VAlign(VerticalAlignment.Center);

        Element rightHeader = Button("全部开启", onOpenAll)
            .Width(RightBtnWidth)
            .Height(30)
            .Padding(16, 0)
            .FontSize(11)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center)
            .AutomationName("全部开启战利品");

        Element header = HStack(0,
            leftHeader.Width(leftHeaderWidth),
            rightHeader);

        IReadOnlyList<OpenableLootItem> items = loot.OpenableItems.Count > 0
            ? loot.OpenableItems
            : PlayerLootOverview.Sample.OpenableItems;

        const int columns = 4;
        var rows = new List<Element>();
        for (int i = 0; i < items.Count; i += columns)
        {
            var rowCards = new List<Element>();
            for (int col = 0; col < columns; col++)
            {
                int index = i + col;
                if (index < items.Count)
                {
                    rowCards.Add(MaterialCard(items[index], palette, onOpenItem, onForgeKey)
                        .WithKey(items[index].LootId)
                        .Width(CardWidth));
                }
            }

            rows.Add(HStack(12, rowCards.ToArray()).WithKey($"mat_row_{i}"));
        }

        return CareerVisuals.Card(
            VStack(14, header, VStack(10, rows.ToArray())),
            padding: 16);
    }

    private static Element MaterialCard(
        OpenableLootItem item,
        YuumiPalette palette,
        Action<OpenableLootItem>? onOpenItem,
        Action? onForgeKey)
    {
        Element thumb = Border(
            TextBlock(item.IsKeyFragment ? "🔑" : "📦")
                .FontSize(24)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center))
            .Width(50)
            .Height(50)
            .CornerRadius(8)
            .Background(CareerVisuals.WithAlpha(palette.Accent, 0x14))
            .WithBorder(CareerVisuals.WithAlpha(palette.Accent, 0x2A), 1);

        Element badge = item.IsKeyFragment
            ? Border(TextBlock("3合1合成").FontSize(10).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).Foreground(CareerVisuals.WinText))
                .Padding(5, 2)
                .CornerRadius(4)
                .Background(CareerVisuals.WithAlpha(CareerVisuals.WinText, 0x1F))
            : item.NeedKey
                ? Border(TextBlock("需要钥匙").FontSize(10).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).Foreground(CareerVisuals.GoldColor))
                    .Padding(5, 2)
                    .CornerRadius(4)
                    .Background(CareerVisuals.WithAlpha(CareerVisuals.GoldColor, 0x1F))
                : Border(TextBlock("无需钥匙").FontSize(10).Foreground(Theme.SecondaryText))
                    .Padding(5, 2)
                    .CornerRadius(4)
                    .Background(CareerVisuals.WithAlpha(palette.Accent, 0x10));

        string btnText = item.IsKeyFragment ? "合成" : "开启";
        Action onBtnClick = item.IsKeyFragment
            ? () => onForgeKey?.Invoke()
            : () => onOpenItem?.Invoke(item);

        Element rightContent = VStack(6,
            HStack(0,
                TextBlock(item.Name)
                    .Width(95)
                    .FontSize(12)
                    .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                    .VAlign(VerticalAlignment.Center),
                Border(TextBlock($"x{item.Count}")
                        .FontSize(10)
                        .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                        .Foreground(palette.Accent))
                    .Padding(5, 1)
                    .CornerRadius(3)
                    .Background(CareerVisuals.WithAlpha(palette.Accent, 0x1F))
                    .Width(35)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Center)),
            HStack(0,
                badge.Width(75).VAlign(VerticalAlignment.Center),
                Button(btnText, onBtnClick)
                    .Width(55)
                    .Height(24)
                    .Padding(8, 0)
                    .FontSize(10)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Center)
                    .AutomationName(btnText)));

        return Border(
            HStack(10,
                thumb.VAlign(VerticalAlignment.Center),
                rightContent.Width(130).VAlign(VerticalAlignment.Center)))
            .Padding(10)
            .CornerRadius(10)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);
    }

    /// <summary>区块二：碎片库存管理面板。</summary>
    private static Element RenderInventorySection(
        PlayerLootOverview loot,
        YuumiPalette palette,
        Action onRefresh,
        int typeFilterIndex,
        Action<int>? onTypeFilterChanged,
        int ownedFilterIndex,
        Action<int>? onOwnedFilterChanged,
        int valueTypeIndex,
        Action<int>? onValueTypeChanged,
        int operatorIndex,
        Action<int>? onOperatorChanged,
        string valueThreshold,
        Action<string>? onValueThresholdChanged,
        IReadOnlySet<string>? selectedLootIds,
        Action<string>? onToggleSelect,
        Action? onSelectAllFiltered,
        Action? onClearSelection,
        Action? onDisenchant,
        Action? onReroll,
        Action? onUpgrade,
        LootOperationProgress? progress,
        string? operationNotice)
    {
        const double TotalWidth = 916;
        const double CurrencyWidth = 320;
        double leftHeaderWidth = TotalWidth - CurrencyWidth;

        // 头部：标题与精粹余额左右分离
        Element leftHeader = HStack(12,
            TextBlock("💎 碎片库存管理")
                .FontSize(15)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .VAlign(VerticalAlignment.Center),
            Button("刷新库存", onRefresh).Height(30).Padding(12, 0).FontSize(11).AutomationName("刷新碎片库存"))
            .VAlign(VerticalAlignment.Center);

        Element rightCurrency = HStack(10,
            Border(HStack(6,
                    TextBlock("🔵").FontSize(11),
                    TextBlock($"{loot.BlueEssence}").FontSize(12).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground(CareerVisuals.BlueEssenceColor)))
                .Padding(8, 4)
                .CornerRadius(6)
                .Background(CareerVisuals.WithAlpha(CareerVisuals.BlueEssenceColor, 0x1A))
                .WithBorder(CareerVisuals.WithAlpha(CareerVisuals.BlueEssenceColor, 0x33), 1),
            Border(HStack(6,
                    TextBlock("🔶").FontSize(11),
                    TextBlock($"{loot.OrangeEssence}").FontSize(12).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground(CareerVisuals.OrangeEssenceColor)))
                .Padding(8, 4)
                .CornerRadius(6)
                .Background(CareerVisuals.WithAlpha(CareerVisuals.OrangeEssenceColor, 0x1A))
                .WithBorder(CareerVisuals.WithAlpha(CareerVisuals.OrangeEssenceColor, 0x33), 1))
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center);

        Element header = HStack(0,
            leftHeader.Width(leftHeaderWidth),
            rightCurrency.Width(CurrencyWidth));

        // 水平紧凑筛选工具栏
        Element filterBar = HStack(16,
            HStack(6,
                TextBlock("碎片类型").FontSize(11).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                ComboBox(TypeFilters, typeFilterIndex, idx => onTypeFilterChanged?.Invoke(idx)).Width(110).AutomationName("碎片类型")),
            HStack(6,
                TextBlock("拥有状态").FontSize(11).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                ComboBox(OwnedFilters, ownedFilterIndex, idx => onOwnedFilterChanged?.Invoke(idx)).Width(100).AutomationName("拥有状态")),
            HStack(6,
                TextBlock("价值基准").FontSize(11).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                ComboBox(ValueTypeFilters, valueTypeIndex, idx => onValueTypeChanged?.Invoke(idx)).Width(110).AutomationName("价值基准")),
            HStack(6,
                TextBlock("价值").FontSize(11).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                ComboBox(OperatorFilters, operatorIndex, idx => onOperatorChanged?.Invoke(idx)).Width(125).AutomationName("价值运算符"),
                TextBox(valueThreshold, text => onValueThresholdChanged?.Invoke(text), "不限").Width(80).Height(30).AutomationName("价值阈值")))
            .VAlign(VerticalAlignment.Center);

        // 碎片列表过滤
        IReadOnlyList<LootInventoryItem> rawInventory = loot.InventoryItems.Count > 0
            ? loot.InventoryItems
            : PlayerLootOverview.Sample.InventoryItems;

        IReadOnlyList<LootInventoryItem> filteredInventory = FilterInventory(
            rawInventory,
            typeFilterIndex,
            ownedFilterIndex,
            valueTypeIndex,
            operatorIndex,
            valueThreshold);

        const int columns = 4;
        var rows = new List<Element>();
        for (int i = 0; i < filteredInventory.Count; i += columns)
        {
            var rowCards = new List<Element>();
            for (int col = 0; col < columns; col++)
            {
                int index = i + col;
                if (index < filteredInventory.Count)
                {
                    bool isSelected = selectedLootIds?.Contains(filteredInventory[index].LootId) == true;
                    rowCards.Add(InventoryCard(filteredInventory[index], isSelected, palette, onToggleSelect)
                        .WithKey(filteredInventory[index].LootId)
                        .Width(CardWidth));
                }
            }

            rows.Add(HStack(12, rowCards.ToArray()).WithKey($"inv_row_{i}"));
        }

        // 统计选中项
        int selectedCount = 0;
        int estBlue = 0;
        int estOrange = 0;
        if (selectedLootIds is not null && selectedLootIds.Count > 0)
        {
            foreach (var item in rawInventory)
            {
                if (selectedLootIds.Contains(item.LootId))
                {
                    selectedCount += item.Count;
                    if (item.IsChampion)
                    {
                        estBlue += item.DisenchantValue * item.Count;
                    }
                    else
                    {
                        estOrange += item.DisenchantValue * item.Count;
                    }
                }
            }
        }

        string selectionDesc = selectedCount > 0
            ? $"已选 {selectedCount} 个 (预计可分解: {estBlue} 🔵, {estOrange} 🔶)"
            : "未选择任何碎片";

        Element countText = selectedCount > 0
            ? TextBlock(selectionDesc).FontSize(12).Foreground(palette.Accent).VAlign(VerticalAlignment.Center)
            : TextBlock(selectionDesc).FontSize(12).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center);

        // 底部工具栏
        Element footer = HStack(0,
            HStack(12,
                Button("选择当前所有过滤项", () => onSelectAllFiltered?.Invoke()).Height(30).Padding(12, 0).FontSize(11).AutomationName("选择当前所有过滤项"),
                Button("清空已选", () => onClearSelection?.Invoke()).Height(30).Padding(12, 0).FontSize(11).AutomationName("清空已选"),
                countText)
                .Width(500)
                .VAlign(VerticalAlignment.Center),
            HStack(8,
                Button("分解", () => onDisenchant?.Invoke())
                    .Width(70)
                    .Height(30)
                    .FontSize(11)
                    .IsEnabled(selectedCount > 0)
                    .AutomationName("批量分解"),
                Button("重铸 (3合1)", () => onReroll?.Invoke())
                    .Width(90)
                    .Height(30)
                    .FontSize(11)
                    .IsEnabled(selectedCount >= 3 && selectedCount % 3 == 0)
                    .AutomationName("批量重随"),
                Button("升级 / 解锁", () => onUpgrade?.Invoke())
                    .Height(30)
                    .Padding(14, 0)
                    .FontSize(11)
                    .IsEnabled(selectedCount > 0)
                    .AutomationName("批量升级解锁"))
                .Width(416)
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Center));

        // 操作进度指示条
        Element? progressBanner = null;
        if (progress is not null)
        {
            progressBanner = Border(
                HStack(12,
                    TextBlock($"正在处理... ({progress.Current}/{progress.Total}) {progress.ItemName}: {progress.RewardDesc}")
                        .FontSize(12)
                        .Foreground(palette.Accent)
                        .VAlign(VerticalAlignment.Center)))
                .Padding(10, 6)
                .CornerRadius(6)
                .Background(CareerVisuals.WithAlpha(palette.Accent, 0x1E))
                .WithBorder(palette.Accent, 1);
        }
        else if (!string.IsNullOrWhiteSpace(operationNotice))
        {
            progressBanner = Border(
                TextBlock(operationNotice)
                    .FontSize(12)
                    .Foreground(CareerVisuals.WinText))
                .Padding(10, 6)
                .CornerRadius(6)
                .Background(CareerVisuals.WithAlpha(CareerVisuals.WinText, 0x1A))
                .WithBorder(CareerVisuals.WinText, 1);
        }

        var sectionChildren = new List<Element>
        {
            header,
            Border(filterBar)
                .Padding(12, 8)
                .CornerRadius(6)
                .Background(CareerVisuals.WithAlpha(palette.Accent, 0x08))
                .WithBorder(Theme.CardStroke, 1),
        };

        if (progressBanner is not null)
        {
            sectionChildren.Add(progressBanner);
        }

        if (rows.Count > 0)
        {
            sectionChildren.Add(VStack(10, rows.ToArray()));
        }
        else
        {
            sectionChildren.Add(Border(
                TextBlock("没有符合当前过滤条件的碎片")
                    .FontSize(12)
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center))
                .Padding(20));
        }

        sectionChildren.Add(Divider());
        sectionChildren.Add(footer);

        return CareerVisuals.Card(
            VStack(14, sectionChildren.ToArray()),
            padding: 16);
    }

    private static Element InventoryCard(
        LootInventoryItem item,
        bool isSelected,
        YuumiPalette palette,
        Action<string>? onToggleSelect)
    {
        Element thumb = Border(
            TextBlock(item.IsChampion ? "🛡️" : "🎨")
                .FontSize(20)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center))
            .Width(44)
            .Height(44)
            .CornerRadius(6)
            .Background(CareerVisuals.WithAlpha(palette.Accent, 0x14))
            .WithBorder(CareerVisuals.WithAlpha(palette.Accent, 0x2A), 1);

        string valueColor = item.IsChampion
            ? CareerVisuals.BlueEssenceColor
            : CareerVisuals.OrangeEssenceColor;

        string valueIcon = item.IsChampion ? "🔵" : "🔶";

        Element rightContent = VStack(4,
            HStack(0,
                TextBlock(item.ItemDesc)
                    .Width(95)
                    .FontSize(11)
                    .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                    .VAlign(VerticalAlignment.Center),
                Border(TextBlock($"x{item.Count}")
                        .FontSize(9)
                        .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                        .Foreground(palette.Accent))
                    .Padding(4, 1)
                    .CornerRadius(3)
                    .Background(CareerVisuals.WithAlpha(palette.Accent, 0x1F))
                    .Width(35)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Center)),
            HStack(0,
                Border((item.IsOwned
                        ? TextBlock("已拥有").FontSize(9).Foreground(CareerVisuals.WinText)
                        : TextBlock("未拥有").FontSize(9).Foreground(Theme.SecondaryText))
                        .HAlign(HorizontalAlignment.Center))
                    .Padding(4, 1)
                    .CornerRadius(3)
                    .Background(CareerVisuals.WithAlpha(item.IsOwned ? CareerVisuals.WinText : palette.Accent, 0x14))
                    .Width(50)
                    .VAlign(VerticalAlignment.Center),
                HStack(3,
                        TextBlock(valueIcon).FontSize(9),
                        TextBlock($"{item.DisenchantValue}")
                            .FontSize(11)
                            .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                            .Foreground(valueColor))
                    .Width(80)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Center)));

        Element checkBadge = isSelected
            ? Border(TextBlock("✓").FontSize(10).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground(CareerVisuals.WinText))
                .Padding(4, 1)
                .CornerRadius(3)
                .Background(CareerVisuals.WithAlpha(CareerVisuals.WinText, 0x33))
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Top)
            : Empty();

        Element cardContent = Grid(
            [GridSize.Auto],
            [GridSize.Auto],
            [
                HStack(8,
                    thumb.VAlign(VerticalAlignment.Center),
                    rightContent.Width(130).VAlign(VerticalAlignment.Center)),
                checkBadge,
            ]);

        Element cardBorder = Border(
            Button(cardContent, () => onToggleSelect?.Invoke(item.LootId))
                .HAlign(HorizontalAlignment.Stretch)
                .Background(Theme.CardBackground)
                .AutomationName($"选择碎片 {item.ItemDesc}"))
            .Padding(4)
            .CornerRadius(8);

        return isSelected
            ? cardBorder.WithBorder(palette.Accent, 2)
            : cardBorder.WithBorder(Theme.CardStroke, 1);
    }

    public static IReadOnlyList<LootInventoryItem> FilterInventory(
        IReadOnlyList<LootInventoryItem> items,
        int typeFilterIndex,
        int ownedFilterIndex,
        int valueTypeIndex,
        int operatorIndex,
        string valueThreshold)
    {
        IEnumerable<LootInventoryItem> result = items;

        if (typeFilterIndex > 0)
        {
            result = typeFilterIndex switch
            {
                1 => result.Where(i => i.DisplayCategories.Contains("CHAMPION", StringComparison.OrdinalIgnoreCase)),
                2 => result.Where(i => i.DisplayCategories.Contains("SKIN", StringComparison.OrdinalIgnoreCase)),
                3 => result.Where(i => i.DisplayCategories.Contains("EMOTE", StringComparison.OrdinalIgnoreCase)),
                4 => result.Where(i => i.DisplayCategories.Contains("WARD", StringComparison.OrdinalIgnoreCase)),
                5 => result.Where(i => i.DisplayCategories.Contains("ICON", StringComparison.OrdinalIgnoreCase) || i.DisplayCategories.Contains("SUMMONERICON", StringComparison.OrdinalIgnoreCase)),
                6 => result.Where(i => i.DisplayCategories.Contains("MATERIAL", StringComparison.OrdinalIgnoreCase) || i.DisplayCategories.Contains("CHEST", StringComparison.OrdinalIgnoreCase)),
                _ => result,
            };
        }

        if (ownedFilterIndex == 1)
        {
            result = result.Where(i => i.IsOwned);
        }
        else if (ownedFilterIndex == 2)
        {
            result = result.Where(i => !i.IsOwned);
        }

        if (int.TryParse(valueThreshold, out int threshold))
        {
            result = result.Where(i =>
            {
                int val = valueTypeIndex == 1 ? i.Value : i.DisenchantValue;
                return operatorIndex switch
                {
                    0 => val <= threshold,
                    1 => val >= threshold,
                    2 => val == threshold,
                    _ => true,
                };
            });
        }

        return result.ToList();
    }

    private static Element Divider() =>
        Border(Empty()).Height(1).Background(Theme.CardStroke).HAlign(HorizontalAlignment.Stretch);
}
