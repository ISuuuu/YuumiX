using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Pages.Career;

internal static class CareerLayout
{
    internal static Element Render(
        Element header,
        Element tabBar,
        bool tabBarVisible,
        CareerMatchesSection.MatchesParts matches,
        Element content,
        CareerTab activeTab,
        Element? overlay)
    {
        const double HeaderGap = 24;
        const double TabBarGap = 16;

        bool showMatches = activeTab == CareerTab.Matches;

        // 动画 trigger 的去重依赖原生控件实例；切页时保持资料卡的祖先和战绩节点槽位稳定，避免重挂后重播。
        Element sharedHeader = VStack(0,
            header,
            tabBar.Margin(0, HeaderGap, 0, 0).IsVisible(tabBarVisible),
            matches.Header.Margin(0, tabBarVisible ? TabBarGap : HeaderGap, 0, 0).IsVisible(showMatches));

        Element listScroller = (ScrollViewer(matches.List) with
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        }).Margin(0, TabBarGap, 0, 0).IsVisible(showMatches);

        Element contentHost = Grid(
            [GridSize.Star()], [GridSize.Star()],
            [listScroller, content.Margin(0, tabBarVisible ? TabBarGap : HeaderGap, 0, 0).IsVisible(!showMatches)]);
        Element body = Grid(
            [GridSize.Star()], [GridSize.Auto, GridSize.Star()],
            [sharedHeader.Grid(row: 0), contentHost.Grid(row: 1)])
            .MaxWidth(CareerVisuals.ContentWidth)
            .HAlign(HorizontalAlignment.Center);
        Element pageBody = Grid(
            [GridSize.Star()], [GridSize.Star()],
            [body, !showMatches && overlay is not null ? overlay : Empty()]);

        // 战绩由内部滚动区吃剩余高度；其余页签恢复外层整页滚动。
        return (ScrollViewer(Border(pageBody).Padding(8, 16, 24, 16).HAlign(HorizontalAlignment.Stretch)) with
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = showMatches ? ScrollMode.Disabled : ScrollMode.Auto,
            VerticalScrollBarVisibility = showMatches ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
        }).IsTabStop(true);
    }
}
