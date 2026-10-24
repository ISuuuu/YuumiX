using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Career.Models;

namespace YuumiX.App.Pages.Career;

/// <summary>
/// 批量操作确认弹窗的描述数据（分解 / 重铸 / 升级共用）。
/// 弹窗本身由 <see cref="LootManagerView.RenderConfirmDialog"/> 渲染，
/// 明细行（种类数、预计精粹、组数、保留未配对数、操作说明）由页面按操作类型装配。
/// </summary>
public sealed record LootConfirmDialog(
    string Title,
    string Message,
    IReadOnlyList<(string Label, string Value)> Details,
    Action OnConfirm);

/// <summary>
/// 战利品管理视图，对应原 <c>LootManagerTab.vue</c> 与 docs/pic/生涯-战利品.png。
/// 包含两块主要面板：
/// 1. 材料面板：宝箱/法球/钥匙碎片开启与合成（点击卡片弹数量选择弹窗，对齐旧版 openLootModal）；
/// 2. 碎片库存管理面板：类型/拥有状态/价值过滤与批量操作栏。
/// 单件数量弹窗与批量确认弹窗的渲染也在本类（<see cref="RenderOpenQuantityDialog"/> /
/// <see cref="RenderConfirmDialog"/>），由页面在正文之上覆盖一层遮罩承载。
/// </summary>
internal static class LootManagerView
{
    private const double CardWidth = 220;

    /// <summary>类型筛选项，顺序对齐旧版：全部/英雄/皮肤/表情/守卫/图标/永恒星碑/材料宝箱（裁决：补 ETERNAL）。</summary>
    public static readonly string[] TypeFilters =
    [
        "全部", "英雄", "皮肤", "表情", "守卫", "图标", "永恒星碑", "材料/宝箱"
    ];

    /// <summary>永恒星碑在 TypeFilters 中的下标（FilterInventory 同步使用）。</summary>
    public const int TypeFilterEternal = 6;

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
            RenderMaterialsSection(loot, palette, onRefresh, onOpenAll, onOpenItem),
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
        Action<OpenableLootItem>? onOpenItem)
    {
        const double TotalWidth = 916;
        const double RightBtnWidth = 110;
        double leftHeaderWidth = TotalWidth - RightBtnWidth;

        Element leftHeader = HStack(12,
            BodyStrong("📦 材料")
                .VAlign(VerticalAlignment.Center),
            Button(Caption("刷新列表"), onRefresh).Height(30).Padding(12, 0).AutomationName("刷新材料列表"),
            Border(HStack(4,
                    Caption("🔑"),
                    Caption($"x{loot.TotalKeys}").FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).Foreground(CareerVisuals.GoldColor))
                .VAlign(VerticalAlignment.Center))
                .Padding(8, 3)
                .CornerRadius(4)
                .Background(CareerVisuals.WithAlpha(CareerVisuals.GoldColor, 0x1F))
                .WithBorder(CareerVisuals.WithAlpha(CareerVisuals.GoldColor, 0x33), 1)
                .VAlign(VerticalAlignment.Center))
            .VAlign(VerticalAlignment.Center);

        Element rightHeader = Button(Caption("全部开启"), onOpenAll)
            .Width(RightBtnWidth)
            .Height(30)
            .Padding(16, 0)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center)
            .AutomationName("全部开启战利品");

        Element header = HStack(0,
            leftHeader.Width(leftHeaderWidth),
            rightHeader);

        // D1：不再回退样例数据，空态直接说明"没有可开启的材料"。
        IReadOnlyList<OpenableLootItem> items = loot.OpenableItems;

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
                    rowCards.Add(MaterialCard(items[index], palette, onOpenItem)
                        .WithKey(items[index].LootId)
                        .Width(CardWidth));
                }
            }

            rows.Add(HStack(12, rowCards.ToArray()).WithKey($"mat_row_{i}"));
        }

        if (rows.Count == 0)
        {
            rows.Add(Border(
                Caption("没有可开启的宝箱、法球或钥匙碎片")
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center))
                .Padding(20));
        }

        return CareerVisuals.Card(
            VStack(14, header, VStack(10, rows.ToArray())),
            padding: 16);
    }

    private static Element MaterialCard(
        OpenableLootItem item,
        YuumiPalette palette,
        Action<OpenableLootItem>? onOpenItem)
    {
        Element thumb = !string.IsNullOrWhiteSpace(item.TilePath)
            ? Component<LcuImage, LcuImageProps>(new LcuImageProps(item.TilePath, 50, 8))
            : Border(
                Title(item.IsKeyFragment ? "🔑" : "📦")
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center))
                .Width(50)
                .Height(50)
                .CornerRadius(8)
                .Background(CareerVisuals.WithAlpha(palette.Accent, 0x14))
                .WithBorder(CareerVisuals.WithAlpha(palette.Accent, 0x2A), 1);

        Element badge = item.IsKeyFragment
            ? Border(Caption("3合1合成").FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).Foreground(CareerVisuals.WinText))
                .Padding(5, 2)
                .CornerRadius(4)
                .Background(CareerVisuals.WithAlpha(CareerVisuals.WinText, 0x1F))
            : item.NeedKey
                ? Border(Caption("需要钥匙").FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).Foreground(CareerVisuals.GoldColor))
                    .Padding(5, 2)
                    .CornerRadius(4)
                    .Background(CareerVisuals.WithAlpha(CareerVisuals.GoldColor, 0x1F))
                : Border(Caption("无需钥匙").Foreground(Theme.SecondaryText))
                    .Padding(5, 2)
                    .CornerRadius(4)
                    .Background(CareerVisuals.WithAlpha(palette.Accent, 0x10));

        Element rightContent = VStack(6,
            HStack(0,
                Caption(item.Name)
                    .Width(95)
                    .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                    .VAlign(VerticalAlignment.Center),
                Border(Caption($"x{item.Count}")
                        .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                        .Foreground(palette.Accent))
                    .Padding(5, 1)
                    .CornerRadius(3)
                    .Background(CareerVisuals.WithAlpha(palette.Accent, 0x1F))
                    .Width(35)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Center)),
            HStack(0,
                badge.Width(75).VAlign(VerticalAlignment.Center),
                Caption(item.IsKeyFragment ? "点击合成" : "点击开启")
                    .Foreground(Theme.SecondaryText)
                    .Width(55)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Center)));

        // 对齐旧版：点击整张卡片弹出数量选择弹窗（openLootModal），而不是直接执行开启。
        Element cardContent = HStack(10,
            thumb.VAlign(VerticalAlignment.Center),
            rightContent.Width(130).VAlign(VerticalAlignment.Center));

        return Border(
                Button(cardContent, () => onOpenItem?.Invoke(item))
                    .HAlign(HorizontalAlignment.Stretch)
                    .Background(Theme.CardBackground)
                    .AutomationName($"{(item.IsKeyFragment ? "合成" : "开启")} {item.Name}"))
            .Padding(4)
            .CornerRadius(10)
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
            BodyStrong("💎 碎片库存管理")
                .VAlign(VerticalAlignment.Center),
            Button(Caption("刷新库存"), onRefresh).Height(30).Padding(12, 0).AutomationName("刷新碎片库存"))
            .VAlign(VerticalAlignment.Center);

        Element rightCurrency = HStack(10,
            Border(HStack(6,
                    Caption("🔵"),
                    Caption(loot.BlueEssence.ToString(CultureInfo.InvariantCulture)).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).Foreground(CareerVisuals.BlueEssenceColor))
                .VAlign(VerticalAlignment.Center))
                .Padding(8, 4)
                .CornerRadius(6)
                .Background(CareerVisuals.WithAlpha(CareerVisuals.BlueEssenceColor, 0x1A))
                .WithBorder(CareerVisuals.WithAlpha(CareerVisuals.BlueEssenceColor, 0x33), 1),
            Border(HStack(6,
                    Caption("🔶"),
                    Caption(loot.OrangeEssence.ToString(CultureInfo.InvariantCulture)).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).Foreground(CareerVisuals.OrangeEssenceColor))
                .VAlign(VerticalAlignment.Center))
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
                Caption("碎片类型").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                ComboBox(TypeFilters, typeFilterIndex, idx => onTypeFilterChanged?.Invoke(idx)).Width(110).AutomationName("碎片类型")),
            HStack(6,
                Caption("拥有状态").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                ComboBox(OwnedFilters, ownedFilterIndex, idx => onOwnedFilterChanged?.Invoke(idx)).Width(100).AutomationName("拥有状态")),
            HStack(6,
                Caption("价值基准").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                ComboBox(ValueTypeFilters, valueTypeIndex, idx => onValueTypeChanged?.Invoke(idx)).Width(110).AutomationName("价值基准")),
            HStack(6,
                Caption("价值").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                ComboBox(OperatorFilters, operatorIndex, idx => onOperatorChanged?.Invoke(idx)).Width(125).AutomationName("价值运算符"),
                TextBox(valueThreshold, text => onValueThresholdChanged?.Invoke(text), "不限").Width(80).Height(30).AutomationName("价值阈值")))
            .VAlign(VerticalAlignment.Center);

        // D1：不再回退样例数据。
        IReadOnlyList<LootInventoryItem> rawInventory = loot.InventoryItems;

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
            ? Caption(selectionDesc).Foreground(palette.Accent).VAlign(VerticalAlignment.Center)
            : Caption(selectionDesc).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center);

        // 重铸按钮：与旧版 canReroll 同口径——存在可重随的选中项即可点；
        // 点下去却凑不齐一组时由页面给出「无法重随」提示（裁决：不再静默 return）。
        bool canReroll = CanReroll(rawInventory, selectedLootIds ?? new HashSet<string>(StringComparer.Ordinal));

        // 底部工具栏
        Element footer = HStack(0,
            HStack(12,
                Button(Caption("选择当前所有过滤项"), () => onSelectAllFiltered?.Invoke()).Height(30).Padding(12, 0).AutomationName("选择当前所有过滤项"),
                Button(Caption("清空已选"), () => onClearSelection?.Invoke()).Height(30).Padding(12, 0).AutomationName("清空已选"),
                countText)
                .Width(500)
                .VAlign(VerticalAlignment.Center),
            HStack(8,
                Button(Caption("分解"), () => onDisenchant?.Invoke())
                    .Width(70)
                    .Height(30)
                    .IsEnabled(selectedCount > 0)
                    .AutomationName("批量分解"),
                Button(Caption("重铸 (3合1)"), () => onReroll?.Invoke())
                    .Width(90)
                    .Height(30)
                    .IsEnabled(canReroll)
                    .AutomationName("批量重随"),
                Button(Caption("升级 / 解锁"), () => onUpgrade?.Invoke())
                    .Height(30)
                    .Padding(14, 0)
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
                    Caption($"正在处理... ({progress.Current}/{progress.Total}) {progress.ItemName}: {progress.RewardDesc}")
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
                Caption(operationNotice)
                    .TextWrapping()
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
                Caption("没有符合当前过滤条件的碎片")
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
        Element thumb = !string.IsNullOrWhiteSpace(item.TilePath)
            ? Component<LcuImage, LcuImageProps>(new LcuImageProps(item.TilePath, 44, 6))
            : Border(
                Subtitle(item.IsChampion ? "🛡️" : "🎨")
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
                Caption(item.ItemDesc)
                    .Width(95)
                    .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                    .VAlign(VerticalAlignment.Center),
                Border(Caption($"x{item.Count}")
                        .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                        .Foreground(palette.Accent))
                    .Padding(4, 1)
                    .CornerRadius(3)
                    .Background(CareerVisuals.WithAlpha(palette.Accent, 0x1F))
                    .Width(35)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Center)),
            HStack(0,
                Border((item.IsOwned
                        ? Caption("已拥有").Foreground(CareerVisuals.WinText)
                        : Caption("未拥有").Foreground(Theme.SecondaryText))
                        .HAlign(HorizontalAlignment.Center))
                    .Padding(4, 1)
                    .CornerRadius(3)
                    .Background(CareerVisuals.WithAlpha(item.IsOwned ? CareerVisuals.WinText : palette.Accent, 0x14))
                    .Width(50)
                    .VAlign(VerticalAlignment.Center),
                HStack(3,
                        Caption(valueIcon),
                        Caption(item.DisenchantValue.ToString(CultureInfo.InvariantCulture))
                            .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                            .Foreground(valueColor))
                    .Width(80)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Center)));

        Element checkBadge = isSelected
            ? Border(Caption("✓").FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).Foreground(CareerVisuals.WinText))
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
                // 永恒星碑：对齐旧版口径，lootType 含 STATSTONE 即命中
                TypeFilterEternal => result.Where(i => i.IsStatstone),
                7 => result.Where(i => i.DisplayCategories.Contains("MATERIAL", StringComparison.OrdinalIgnoreCase) || i.DisplayCategories.Contains("CHEST", StringComparison.OrdinalIgnoreCase)),
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

    /// <summary>批量操作是否排除该物品（材料/宝箱/法球/货币，与旧版 canUpgrade / canReroll 的排除口径一致）。</summary>
    private static bool IsBulkExcluded(LootInventoryItem item)
    {
        string category = item.DisplayCategories.ToUpperInvariant();
        return category.Contains("MATERIAL", StringComparison.Ordinal)
            || category.Contains("CHEST", StringComparison.Ordinal)
            || category.Contains("ORB", StringComparison.Ordinal)
            || category.Contains("CURRENCY", StringComparison.Ordinal);
    }

    /// <summary>
    /// 是否存在可重随的选中项（旧版 <c>canReroll</c> 同口径）：
    /// 至少一个选中项既不是永恒星碑、也不是材料/宝箱类。
    /// </summary>
    public static bool CanReroll(IReadOnlyList<LootInventoryItem> inventory, IReadOnlySet<string> selectedLootIds)
    {
        foreach (var item in inventory)
        {
            if (selectedLootIds.Contains(item.LootId) && !item.IsStatstone && !IsBulkExcluded(item))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 重铸计划：按 lootId 分组后每组凑 3 的倍数（与 <c>CareerService.RerollLootsAsync</c> 同一语义），
    /// 返回送审的 (lootId, count) 列表与「组数 / 消耗总数 / 保留未配对数」。
    /// </summary>
    public static (IReadOnlyList<(string LootId, int Count)> Items, int Groups, int Consumed, int Remaining) PlanReroll(
        IReadOnlyList<LootInventoryItem> inventory,
        IReadOnlySet<string> selectedLootIds)
    {
        var items = new List<(string LootId, int Count)>();
        int groups = 0;
        int remaining = 0;
        foreach (var group in inventory
            .Where(i => selectedLootIds.Contains(i.LootId) && !i.IsStatstone && !IsBulkExcluded(i))
            .GroupBy(i => i.LootId, StringComparer.Ordinal))
        {
            int count = group.Sum(i => i.Count);
            items.Add((group.Key, count));
            groups += count / 3;
            remaining += count % 3;
        }

        return (items, groups, groups * 3, remaining);
    }

    /// <summary>
    /// 单件数量选择弹窗（裁决：对齐旧版 openLootModal）——
    /// 预览 + 名称 + 拥有数；needKey 时显示钥匙行；数量范围按物品类型限定，
    /// 最大值 ≤ 0 显示不足提示；确认按钮文案「开始合成 (N) / 开始开启 (N)」。
    /// </summary>
    public static Element RenderOpenQuantityDialog(
        OpenableLootItem item,
        int quantity,
        YuumiPalette palette,
        Action<int>? onQuantityChanged,
        Action onConfirm,
        Action onCancel)
    {
        bool isFragment = item.IsKeyFragment;
        int maxQuantity = MaxOpenQuantity(item);

        string keyName = item.KeyLootId == "MATERIAL_key_premium" ? "杰作钥匙" : "海克斯科技钥匙";
        string title = isFragment ? $"批量合成 - {item.Name}" : $"批量开启 - {item.Name}";
        string confirmText = isFragment
            ? $"开始合成 ({quantity.ToString(CultureInfo.InvariantCulture)})"
            : $"开始开启 ({quantity.ToString(CultureInfo.InvariantCulture)})";
        string insufficientHint = maxQuantity <= 0
            ? isFragment
                ? "钥匙碎片不足 3 个，无法合成"
                : item.NeedKey
                    ? "钥匙不足，无法开启"
                    : string.Empty
            : string.Empty;
        var bodyChildren = new List<Element>
        {
            HStack(12,
                // 预览图：tilePath 走 LcuImage（ADR-01）；缺失时 Icon 给占位块
                Search.SearchVisuals.Icon(item.TilePath, 50, 8).VAlign(VerticalAlignment.Center),
                VStack(4,
                    BodyStrong(item.Name),
                    Caption($"拥有: x{item.Count.ToString(CultureInfo.InvariantCulture)}")
                        .Foreground(Theme.SecondaryText))
                    .VAlign(VerticalAlignment.Center)),
        };

        if (item.NeedKey && item.KeyLootId is not null)
        {
            bodyChildren.Add(HStack(8,
                Caption("需要钥匙").Foreground(Theme.SecondaryText),
                Caption($"{keyName} x{quantity.ToString(CultureInfo.InvariantCulture)}（拥有：{item.KeyCount.ToString(CultureInfo.InvariantCulture)}）")
                    .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                    .Foreground(CareerVisuals.GoldColor)));
        }

        bodyChildren.Add(HStack(8,
            Caption(isFragment ? "合成次数" : "开启数量").Foreground(Theme.SecondaryText),
            NumberBox(quantity, v =>
            {
                if (double.IsNaN(v))
                {
                    return;
                }

                // NumberBox 不暴露范围属性，范围在回调里 Math.Clamp（既有坑表约定）
                int next = (int)Math.Clamp(v, 1, Math.Max(maxQuantity, 1));
                if (next == quantity)
                {
                    return;
                }

                onQuantityChanged?.Invoke(next);
            }).Width(120).AutomationName("开启数量")));

        if (maxQuantity <= 0)
        {
            bodyChildren.Add(Caption(insufficientHint)
                .Foreground(CareerVisuals.LoseText));
        }

        bodyChildren.Add(HStack(12,
            Button("取消", onCancel)
                .Width(90)
                .Height(32),
            Button(confirmText, onConfirm)
                .Width(130)
                .Height(32)
                .IsEnabled(maxQuantity > 0)
                .AutomationName(isFragment ? "确认合成钥匙" : "确认开启战利品"))
            .HAlign(HorizontalAlignment.Right));

        return DialogMask(VStack(12, bodyChildren.ToArray()), palette, title, 380, onCancel);
    }

    /// <summary>单件可开启的最大数量：钥匙碎片 = ⌊count/3⌋，needKey = min(count, 钥匙数)，其余 = count。</summary>
    public static int MaxOpenQuantity(OpenableLootItem item) =>
        item.IsKeyFragment
            ? item.Count / 3
            : item.NeedKey
                ? Math.Min(item.Count, item.KeyCount)
                : item.Count;

    /// <summary>批量操作确认弹窗：标题 + 说明 + 明细行（Label : Value）+ 取消 / 确定执行。</summary>
    public static Element RenderConfirmDialog(
        LootConfirmDialog dialog,
        YuumiPalette palette,
        Action onCancel)
    {
        var bodyChildren = new List<Element>
        {
            Body(dialog.Message).TextWrapping(),
        };

        foreach (var (label, value) in dialog.Details)
        {
            bodyChildren.Add(HStack(8,
                Caption(label).Foreground(Theme.SecondaryText),
                Caption(value).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).HAlign(HorizontalAlignment.Right))
                .HAlign(HorizontalAlignment.Stretch));
        }

        bodyChildren.Add(HStack(12,
            Button("取消", onCancel)
                .Width(90)
                .Height(32),
            Button("确定执行", dialog.OnConfirm)
                .Width(110)
                .Height(32)
                .AutomationName("确定执行批量操作"))
            .HAlign(HorizontalAlignment.Right));

        return DialogMask(VStack(12, bodyChildren.ToArray()), palette, dialog.Title, 420, onCancel);
    }

    /// <summary>弹窗遮罩：半透明黑底 + 居中卡片（标题 + ✕ + 内容）。</summary>
    private static Element Divider() =>
        Border(Empty()).Height(1).Background(Theme.CardStroke).HAlign(HorizontalAlignment.Stretch);

    private static Element DialogMask(Element content, YuumiPalette palette, string title, double cardWidth, Action onCancel) =>
        Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                [
                    Border(Empty())
                        .Background("#88000000")
                        .HAlign(HorizontalAlignment.Stretch)
                        .VAlign(VerticalAlignment.Stretch),
                    Border(VStack(12,
                            HStack(8,
                                BodyStrong(title)
                                    .HAlign(HorizontalAlignment.Left),
                                Button(Caption("✕"), onCancel)
                                    .Width(28)
                                    .Height(28)
                                    .Padding(0)
                                    .HAlign(HorizontalAlignment.Right)
                                    .AutomationName("关闭弹窗"))
                                .HAlign(HorizontalAlignment.Stretch),
                            content))
                        .Width(cardWidth)
                        .Padding(20)
                        .CornerRadius(10)
                        .Background(Theme.CardBackground)
                        .WithBorder(Theme.CardStroke, 1)
                        .VAlign(VerticalAlignment.Center)
                        .HAlign(HorizontalAlignment.Center),
                ]);
}
