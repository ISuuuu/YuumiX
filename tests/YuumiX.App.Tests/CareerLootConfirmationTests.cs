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
        var loot = new PlayerLootOverview([], [
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
