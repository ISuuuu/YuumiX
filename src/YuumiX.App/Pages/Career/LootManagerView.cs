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

    private static readonly string[] TypeFilters =
    [
        "全部", "英雄", "皮肤", "表情", "守卫", "图标", "材料/宝箱"
    ];

    private static readonly string[] OwnedFilters =
    [
        "全部", "已拥有", "未拥有"
    ];

    private static readonly string[] ValueTypeFilters =
    [
        "分解收益", "商城售价"
    ];

    private static readonly string[] OperatorFilters =
    [
        "小于等于 (<=)", "大于等于 (>=)", "等于 (=)"
    ];

    public static Element Render(
        PlayerLootOverview loot,
        YuumiPalette palette,
        Action onRefresh,
        Action onOpenAll)
    {
        return VStack(14,
            RenderMaterialsSection(loot, palette, onRefresh, onOpenAll),
            RenderInventorySection(loot, palette, onRefresh));
    }

    /// <summary>区块一：材料（可开启战利品）卡片面板。</summary>
    private static Element RenderMaterialsSection(
        PlayerLootOverview loot,
        YuumiPalette palette,
        Action onRefresh,
        Action onOpenAll)
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
                    rowCards.Add(MaterialCard(items[index], palette)
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

    private static Element MaterialCard(OpenableLootItem item, YuumiPalette palette)
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
                Button(btnText, () => { })
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
        Action onRefresh)
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
                ComboBox(TypeFilters, 1, _ => { }).Width(110).AutomationName("碎片类型")),
            HStack(6,
                TextBlock("拥有状态").FontSize(11).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                ComboBox(OwnedFilters, 0, _ => { }).Width(100).AutomationName("拥有状态")),
            HStack(6,
                TextBlock("价值基准").FontSize(11).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                ComboBox(ValueTypeFilters, 0, _ => { }).Width(110).AutomationName("价值基准")),
            HStack(6,
                TextBlock("价值").FontSize(11).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                ComboBox(OperatorFilters, 0, _ => { }).Width(125).AutomationName("价值运算符"),
                TextBox().PlaceholderText("不限").Width(80).Height(30).AutomationName("价值阈值")))
            .VAlign(VerticalAlignment.Center);

        // 碎片列表卡片
        IReadOnlyList<LootInventoryItem> inventory = loot.InventoryItems.Count > 0
            ? loot.InventoryItems
            : PlayerLootOverview.Sample.InventoryItems;

        const int columns = 4;
        var rows = new List<Element>();
        for (int i = 0; i < inventory.Count; i += columns)
        {
            var rowCards = new List<Element>();
            for (int col = 0; col < columns; col++)
            {
                int index = i + col;
                if (index < inventory.Count)
                {
                    rowCards.Add(InventoryCard(inventory[index], palette)
                        .WithKey(inventory[index].LootId)
                        .Width(CardWidth));
                }
            }

            rows.Add(HStack(12, rowCards.ToArray()).WithKey($"inv_row_{i}"));
        }

        // 底部工具栏
        Element footer = HStack(0,
            HStack(12,
                Button("选择当前所有过滤项", () => { }).Height(30).Padding(12, 0).FontSize(11).AutomationName("选择当前所有过滤项"),
                Button("清空已选", () => { }).Height(30).Padding(12, 0).FontSize(11).AutomationName("清空已选"),
                TextBlock("已选中 0 个碎片").FontSize(12).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center))
                .Width(500)
                .VAlign(VerticalAlignment.Center),
            HStack(8,
                Button("分解", () => { }).Width(70).Height(30).FontSize(11).AutomationName("批量分解"),
                Button("重随", () => { }).Width(70).Height(30).FontSize(11).AutomationName("批量重随"),
                Button("升级 / 解锁", () => { }).Height(30).Padding(14, 0).FontSize(11).AutomationName("批量升级解锁"))
                .Width(416)
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Center));

        return CareerVisuals.Card(
            VStack(14,
                header,
                Border(filterBar)
                    .Padding(12, 8)
                    .CornerRadius(6)
                    .Background(CareerVisuals.WithAlpha(palette.Accent, 0x08))
                    .WithBorder(Theme.CardStroke, 1),
                VStack(10, rows.ToArray()),
                Divider(),
                footer),
            padding: 16);
    }

    private static Element InventoryCard(LootInventoryItem item, YuumiPalette palette)
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

        return Border(
            HStack(8,
                thumb.VAlign(VerticalAlignment.Center),
                rightContent.Width(130).VAlign(VerticalAlignment.Center)))
            .Padding(8)
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);
    }

    private static Element Divider() =>
        Border(Empty()).Height(1).Background(Theme.CardStroke).HAlign(HorizontalAlignment.Stretch);
}
