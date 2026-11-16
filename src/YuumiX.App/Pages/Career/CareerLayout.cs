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
        Element? overlay,
        double viewportWidth = 0,
        Action<double>? setViewportWidth = null)
    {
        const double HeaderGap = 24;
        const double TabBarGap = 16;
        const double HorizontalPadding = 8 + 24;
        double minViewportWidth = CareerVisuals.ContentWidth + HorizontalPadding;
        bool needsHorizontalScroll = viewportWidth > 0 && viewportWidth < minViewportWidth;

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
            .Width(CareerVisuals.ContentWidth)
            .MaxWidth(CareerVisuals.ContentWidth)
            .HAlign(HorizontalAlignment.Center);
        Element pageBody = Grid(
            [GridSize.Star()], [GridSize.Star()],
            [body, !showMatches && overlay is not null ? overlay : Empty()]);

        // 战绩由内部滚动区吃剩余高度；其余页签恢复外层整页滚动；窄窗口下启用横向滚动兜底避免裁切固定 1000 宽正文。
        var scroller = (ScrollViewer(
            Border(pageBody)
                .Padding(8, 16, 24, 16)
                .HAlign(needsHorizontalScroll ? HorizontalAlignment.Left : HorizontalAlignment.Stretch)) with
        {
            HorizontalScrollMode = needsHorizontalScroll ? ScrollMode.Auto : ScrollMode.Disabled,
            HorizontalScrollBarVisibility = needsHorizontalScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalScrollMode = showMatches ? ScrollMode.Disabled : ScrollMode.Auto,
            VerticalScrollBarVisibility = showMatches ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
        }).IsTabStop(true);

        if (setViewportWidth is not null)
        {
            scroller = scroller.OnSizeChanged((_, e) =>
            {
                double w = Math.Floor(e.NewSize.Width);
                if (w > 0 && Math.Abs(w - viewportWidth) >= 1)
                {
                    setViewportWidth(w);
                }
            });
        }

        return scroller;
    }
}
