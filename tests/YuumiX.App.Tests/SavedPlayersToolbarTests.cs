using Microsoft.UI.Reactor.Core;
using Xunit;
using YuumiX.App.Pages.SavedPlayers;

namespace YuumiX.App.Tests;

public sealed class SavedPlayersToolbarTests
{
    [Fact]
    public void FilterButtons_KeepIdentityAndDispatchTheirOwnFilterAcrossSelections()
    {
        var selectedFilter = string.Empty;
        ButtonElement[] Render(string filter)
        {
            return new[] { "all", "tagged", "multiple" }
                .Select(key => SavedPlayersVisuals.RenderFilterButton(key, key, filter, value => selectedFilter = value))
                .ToArray();
        }

        var all = Render("all");
        var multiple = Render("multiple");
        Assert.Equal(3, all.Length);
        Assert.Equal(3, multiple.Length);
        Assert.Equal(all.Select(button => button.Key), multiple.Select(button => button.Key));
        var selectedResources = all[0].ResourceOverrides;
        var unselectedResources = multiple[0].ResourceOverrides;
        Assert.NotNull(selectedResources);
        Assert.NotNull(unselectedResources);
        Assert.Equal("AccentButtonBackgroundPressed", selectedResources.ThemeRefs["ButtonBackgroundPressed"].ResourceKey);
        Assert.Equal("ButtonBackgroundPressed", unselectedResources.ThemeRefs["ButtonBackgroundPressed"].ResourceKey);

        var expected = new[] { "all", "tagged", "multiple" };
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.IsType<Action>(all[i].OnClick)();
            Assert.Equal(expected[i], selectedFilter);
            Assert.IsType<Action>(multiple[i].OnClick)();
            Assert.Equal(expected[i], selectedFilter);
        }
    }
}
