using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using Xunit;
using YuumiX.App.Pages.Career;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Career.Models;

namespace YuumiX.App.Tests;

public sealed class CareerLootConfirmationTests
{
    private static OpenableLootItem Item(string id = "CHEST", int count = 5, bool needKey = false, int keys = 0) =>
        new(id, "物品", count, "recipe", needKey, keys, null);

    [Theory]
    [InlineData(ContentDialogResult.None)]
    [InlineData(ContentDialogResult.Secondary)]
    public void QuantityDismiss_DoesNotConsume(ContentDialogResult result)
    {
        int calls = 0;
        bool dismissed = false;
        var request = new LootQuantityDialog(Item(), 5, _ => calls++);
        var dialog = Assert.IsType<ContentDialogElement>(CareerLootOverlay.Render(
            request, 3, null, YuumiTheme.Default, _ => { }, value => dismissed = value is null, _ => { }));

        Assert.NotNull(dialog.OnClosed);
        dialog.OnClosed(result);

        Assert.True(dismissed);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void QuantityConfirm_UsesDisplayedTargetAndQuantity()
    {
        OpenableLootItem original = Item();
        OpenableLootItem? consumed = null;
        int amount = 0;
        var request = new LootQuantityDialog(original, 5, n => { consumed = original; amount = n; });
        var dialog = Assert.IsType<ContentDialogElement>(CareerLootOverlay.Render(
            request, 3, null, YuumiTheme.Default, _ => { }, _ => { }, _ => { }));

        Assert.NotNull(dialog.OnClosed);
        dialog.OnClosed(ContentDialogResult.Primary);

        Assert.Same(original, consumed);
        Assert.Equal(3, amount);
    }

    [Theory]
    [InlineData("MATERIAL_KEY_FRAGMENT", 2, false, 0, 0)]
    [InlineData("MATERIAL_KEY_FRAGMENT", 7, false, 0, 2)]
    [InlineData("CHEST", 5, true, 2, 2)]
    [InlineData("ORB", 5, false, 0, 5)]
    public void MaximumQuantity_RespectsIngredients(string id, int count, bool needsKey, int keys, int expected)
    {
        var item = Item(id, count, needsKey, keys);
        Assert.Equal(expected, LootManagerView.MaxOpenQuantity(item));
        var dialog = Assert.IsType<ContentDialogElement>(LootManagerView.RenderOpenQuantityDialog(
            item, expected, YuumiTheme.Default, _ => { }, () => { }, () => { }));
        Assert.Equal(expected > 0, dialog.IsPrimaryButtonEnabled);
    }

    [Fact]
    public void BatchDismiss_DoesNotExecute_ConfirmUsesCapturedPlan()
    {
        var selected = new HashSet<string> { "first" };
        var loot = new PlayerLootOverview([], (LootInventoryItem[])[
            new LootInventoryItem("first", "一", "CHAMPION", 2, 0, 10, "", null),
            new LootInventoryItem("second", "二", "CHAMPION", 1, 0, 10, "", null)], 0, 0, 0);
        var (items, kinds, blue, orange) = CareerLootDialogs.PlanDisenchant(loot, selected);
        IReadOnlyList<(string LootId, int Count)>? consumed = null;
        var request = CareerLootDialogs.DisenchantDialog(kinds, blue, orange, () => consumed = items);
        bool dismissed = false;
        var dialog = Assert.IsType<ContentDialogElement>(LootManagerView.RenderConfirmDialog(request, YuumiTheme.Default, () => dismissed = true));
        selected.Clear();
        selected.Add("second");

        Assert.NotNull(dialog.OnClosed);
        dialog.OnClosed(ContentDialogResult.None);
        Assert.True(dismissed);
        Assert.Null(consumed);
        dialog.OnClosed(ContentDialogResult.Primary);
        Assert.Equal(new[] { ("first", 2) }, consumed);
    }

    /// <summary>
    /// 重铸分组按碎片类别（displayCategories）而不是 lootId：客户端的重随配方槽位是同类道具池，
    /// 3 件不同的皮肤碎片就能凑一组；按 lootId 分组会要求同一件碎片有 3 个。
    /// </summary>
    [Fact]
    public void PlanReroll_GroupsByCategoryAndKeepsRemainder()
    {
        var inventory = (LootInventoryItem[])[
            new LootInventoryItem("SKIN_RENTAL_1", "皮肤一", "SKIN", 1, 0, 100, "", null),
            new LootInventoryItem("SKIN_RENTAL_2", "皮肤二", "SKIN", 1, 0, 100, "", null),
            new LootInventoryItem("SKIN_RENTAL_3", "皮肤三", "SKIN", 1, 0, 100, "", null),
            new LootInventoryItem("SKIN_RENTAL_4", "皮肤四", "SKIN", 1, 0, 100, "", null),
            new LootInventoryItem("EMOTE_1", "表情", "EMOTE", 2, 0, 10, "", null),
            new LootInventoryItem("CURRENCY_mythic", "神话精粹", "CURRENCY", 5, 0, 0, "", null),
        ];
        var selected = new HashSet<string>(inventory.Select(i => i.LootId));

        var (lootIds, groups, consumed, remaining) = LootManagerView.PlanReroll(inventory, selected);

        // 皮肤 4 个 → 1 组（余 1）；表情 2 个 → 不组（余 2）；货币被排除
        Assert.Equal(3, consumed);
        Assert.Equal(1, groups);
        Assert.Equal(3, remaining);
        Assert.Equal(new[] { "SKIN_RENTAL_1", "SKIN_RENTAL_2", "SKIN_RENTAL_3" }, lootIds);
    }

    /// <summary>
    /// 碎片库存默认按价值降序，排序基准跟「价值基准」联动：
    /// 0=分解收益按 DisenchantValue，1=商城售价按 Value；同值保持原顺序。
    /// </summary>
    [Fact]
    public void FilterInventory_SortsBySelectedValueBasis()
    {
        var items = (LootInventoryItem[])[
            new LootInventoryItem("cheapButRich", "价低分解高", "SKIN", 1, 100, 900, "", null),
            new LootInventoryItem("priceyButPoor", "价高分解低", "SKIN", 1, 900, 100, "", null),
            new LootInventoryItem("worst", "都低", "SKIN", 1, 50, 10, "", null),
        ];

        var byDisenchant = LootManagerView.FilterInventory(items, 0, 0, 0, 0, string.Empty);
        Assert.Equal(new[] { "cheapButRich", "priceyButPoor", "worst" }, byDisenchant.Select(i => i.LootId));

        var byPrice = LootManagerView.FilterInventory(items, 0, 0, 1, 0, string.Empty);
        Assert.Equal(new[] { "priceyButPoor", "cheapButRich", "worst" }, byPrice.Select(i => i.LootId));
    }

    [Fact]
    public void PendingOperation_RejectsOverlapAndAllowsRetryAfterCompletion()
    {
        var gate = new LootOperationGate();
        Assert.True(gate.TryBegin());
        Assert.False(gate.TryBegin());
        gate.End();
        Assert.True(gate.TryBegin());
    }
}
