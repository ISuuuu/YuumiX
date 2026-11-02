using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Pages.Career;
using Xunit;

namespace YuumiX.App.Tests;

public sealed class CareerLayoutTests
{
    [Theory]
    [InlineData((int)CareerTab.Matches)]
    [InlineData((int)CareerTab.Loot)]
    [InlineData((int)CareerTab.Mastery)]
    public void Tabs_KeepSharedHeaderAndMatchesAtStablePaths(int tabValue)
    {
        var tab = (CareerTab)tabValue;
        var header = Body("资料卡");
        var tabs = Body("标签栏");
        var summary = Body("排位与近期看板");
        var list = Body("战绩列表");
        var content = Body("当前子页内容");
        var root = Assert.IsType<ScrollViewerElement>(CareerLayout.Render(
            header, tabs, true, new(summary, list), content, tab, null));
        var padding = Assert.IsType<BorderElement>(root.Child);
        var overlayHost = Assert.IsType<GridElement>(padding.Child);
        var body = Assert.IsType<GridElement>(overlayHost.Children[0]);
        var sharedHeader = Assert.IsType<StackElement>(body.Children[0]);
        Assert.Same(header, sharedHeader.Children[0]);
        Assert.Equal(tabs.Content, Assert.IsType<TextBlockElement>(sharedHeader.Children[1]).Content);
        var summaryContent = Assert.IsType<TextBlockElement>(sharedHeader.Children[2]);
        Assert.Equal(summary.Content, summaryContent.Content);
        Assert.NotNull(summaryContent.Modifiers);
        Assert.Equal(tab == CareerTab.Matches, summaryContent.Modifiers.IsVisible);

        var contentHost = Assert.IsType<GridElement>(body.Children[1]);
        var matchesScroller = Assert.IsType<ScrollViewerElement>(contentHost.Children[0]);
        Assert.Same(list, matchesScroller.Child);
        Assert.NotNull(matchesScroller.Modifiers);
        Assert.Equal(tab == CareerTab.Matches, matchesScroller.Modifiers.IsVisible);
        var tabContent = contentHost.Children[1];
        Assert.NotNull(tabContent.Modifiers);
        Assert.Equal(tab != CareerTab.Matches, tabContent.Modifiers.IsVisible);
    }

    [Theory]
    [InlineData((int)CareerTab.Matches, ScrollMode.Disabled, ScrollBarVisibility.Disabled)]
    [InlineData((int)CareerTab.Loot, ScrollMode.Auto, ScrollBarVisibility.Auto)]
    [InlineData((int)CareerTab.Mastery, ScrollMode.Auto, ScrollBarVisibility.Auto)]
    public void Tabs_PreserveTheirScrollingBehavior(int tabValue, ScrollMode mode, ScrollBarVisibility bars)
    {
        var tab = (CareerTab)tabValue;
        var root = Assert.IsType<ScrollViewerElement>(CareerLayout.Render(
            Body("资料卡"), Body("标签栏"), true, new(Body("看板"), Body("战绩")), Body("子页"), tab, null));
        Assert.Equal(mode, root.VerticalScrollMode);
        Assert.Equal(bars, root.VerticalScrollBarVisibility);
        Assert.Equal(ScrollMode.Disabled, root.HorizontalScrollMode);
        Assert.Equal(ScrollBarVisibility.Disabled, root.HorizontalScrollBarVisibility);
        Assert.NotNull(root.Modifiers);
        Assert.True(root.Modifiers.IsTabStop);
    }

    [Fact]
    public void Overlay_DoesNotChangeTheSharedHeaderPath()
    {
        var header = Body("资料卡");
        var overlay = Body("数量确认");
        foreach (bool isOpen in new[] { false, true })
        {
            var root = Assert.IsType<ScrollViewerElement>(CareerLayout.Render(
                header, Body("标签栏"), true, new(Body("看板"), Body("战绩")), Body("战利品"),
                CareerTab.Loot, isOpen ? overlay : null));
            var padding = Assert.IsType<BorderElement>(root.Child);
            var overlayHost = Assert.IsType<GridElement>(padding.Child);
            Assert.Equal(isOpen ? 2 : 1, overlayHost.Children.Length);
            var body = Assert.IsType<GridElement>(overlayHost.Children[0]);
            var sharedHeader = Assert.IsType<StackElement>(body.Children[0]);
            Assert.Same(header, sharedHeader.Children[0]);
            if (isOpen) Assert.Same(overlay, overlayHost.Children[1]);
        }
    }
}
