using Xunit;
using YuumiX.App.Pages.Opgg;

namespace YuumiX.App.Tests;

public sealed class OpggVisualsTests
{
    [Fact]
    public void Tab_SelectionKeepsIdentityAndDisabledState()
    {
        var clicks = 0;
        var selected = OpggPage.RenderTab("出装", "出装", true, true, () => clicks++);
        var unselected = OpggPage.RenderTab("出装", "出装", false, true, () => clicks++);
        var disabled = OpggPage.RenderTab("出装", "出装", false, false, () => clicks++);

        Assert.Equal(selected.Key, unselected.Key);
        Assert.NotNull(selected.Modifiers);
        Assert.NotNull(disabled.Modifiers);
        Assert.NotNull(selected.ResourceOverrides);
        Assert.NotNull(unselected.ResourceOverrides);
        Assert.True(selected.Modifiers.IsEnabled);
        Assert.False(disabled.Modifiers.IsEnabled);
        Assert.Equal("AccentButtonBackgroundPressed", selected.ResourceOverrides.ThemeRefs["ButtonBackgroundPressed"].ResourceKey);
        Assert.Equal("ButtonBackgroundPressed", unselected.ResourceOverrides.ThemeRefs["ButtonBackgroundPressed"].ResourceKey);
        Assert.IsType<Action>(unselected.OnClick)();
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void TierColors_DarkMode_DistinguishTierLevels()
    {
        var backgrounds = new[] { "1", "2", "3", "4" }
            .Select(tier => OpggVisuals.GetTierCardBackground(tier, true))
            .ToArray();
        var borders = new[] { "1", "2", "3", "4" }
            .Select(tier => OpggVisuals.GetTierCardBorder(tier, true))
            .ToArray();

        Assert.Equal(4, backgrounds.Distinct().Count());
        Assert.Equal(4, borders.Distinct().Count());
        Assert.All(backgrounds, color => Assert.Matches("^#[0-9A-Fa-f]{6}$", color));
        Assert.All(borders, color => Assert.Matches("^#[0-9A-Fa-f]{6}$", color));
        Assert.Equal(backgrounds[0], OpggVisuals.GetTierCardBackground("0", true));
        Assert.Equal(backgrounds[3], OpggVisuals.GetTierCardBackground("5", true));
    }

    [Fact]
    public void TierColors_LightMode_PreserveExistingValues()
    {
        Assert.Equal("#CDE5F8", OpggVisuals.GetTierCardBackground("1", false));
        Assert.Equal("#CDECEA", OpggVisuals.GetTierCardBackground("2", false));
        Assert.Equal("transparent", OpggVisuals.GetTierCardBackground(null, false));
    }
}
