using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;

namespace YuumiX.App.Pages.GameInfo;

/// <summary>
/// 对局信息页的加载态空壳：形状与真实视图一致（工具栏 44 + 面板 + 等宽列），
/// 数据到达时整页从「骨架」变成「内容」而不是「居中两行字」变成「另一套布局」。
///
/// <para><b>为什么对局信息页的骨架最费工</b></para>
/// 这一页有两套完全不同的布局：五列（左 250 队伍面板 + 右 5 列战绩）与
/// 十列（无左面板、10 列战绩），窄窗时还会上下堆叠。骨架必须跟着当前布局走，
/// 否则骨架本身就会造成一次跳变，等于用跳变换跳变。
///
/// <para><b>为什么只画形状不画内容</b></para>
/// 玩家战绩列里的头像、装备、KDA 都有各自的排版规则，仿造它们等于复制一份
/// 会被立刻丢弃的界面。列用整块灰底占位即可——用户关心的是「这块地方马上会有东西」，
/// 而不是「这块地方具体有什么」。
/// </summary>
internal static class GameInfoSkeleton
{
    /// <summary>宽屏固定 250（旧版 `.game-layout { grid-template-columns: 250px 1fr }`）。</summary>
    private const double LeftPanelWidth = 250;

    /// <summary>窄窗堆叠断点，与 <c>GameInfoPage</c> 的五列视图一致。</summary>
    private const int StackBreakpoint = 1100;

    /// <summary>
    /// 加载态空壳。<paramref name="tenColumns"/> 为 true 时铺十列视图，
    /// 否则铺五列视图；<paramref name="windowWidth"/> 决定宽屏并列还是窄窗堆叠。
    /// </summary>
    internal static Element Render(bool tenColumns, double windowWidth)
    {
        bool stacked = windowWidth > 0 && windowWidth < StackBreakpoint;

        Element rightPanel = Panel(
            Grid(
                [GridSize.Star()],
                [GridSize.Auto, GridSize.Star()],
                [
                    ToolbarSkeleton().Grid(row: 0),
                    ColumnSkeleton(tenColumns ? 10 : 5).Grid(row: 1),
                ]));

        if (tenColumns)
        {
            // 十列视图没有左侧队伍面板，只有工具栏 + 10 列。
            return rightPanel;
        }

        Element leftPanel = Panel(
            Grid(
                [GridSize.Star()],
                [GridSize.Auto, GridSize.Star()],
                [
                    TeamTabSkeleton().Grid(row: 0),
                    PlayerCardSkeleton(5).Grid(row: 1),
                ]));

        Element layout = stacked
            ? Grid(
                [GridSize.Star()],
                [GridSize.Star(34), GridSize.Star(66)],
                [
                    leftPanel.Grid(row: 0),
                    rightPanel.Grid(row: 1).Margin(0, 12, 0, 0),
                ])
            : Grid(
                [GridSize.Auto, GridSize.Star()],
                [GridSize.Star()],
                [
                    leftPanel.Width(LeftPanelWidth).Grid(row: 0, column: 0).Margin(0, 0, 16, 0),
                    rightPanel.Grid(row: 0, column: 1),
                ]);

        return layout;
    }

    /// <summary>面板外框：与真实面板同底色、同描边、同圆角。</summary>
    private static Element Panel(Element content) =>
        Border(content)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .CornerRadius(YuumiTheme.OverlayRadius);

    /// <summary>工具栏骨架：44 高 + 底部分隔线，与真实工具栏同高。</summary>
    private static Element ToolbarSkeleton() =>
        Border(HStack(8, Block(120, 16), Block(56, 16), Block(56, 16)))
            .Height(GameInfoPage.TeamTabHeight)
            .Padding(12, 6, 12, 6)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .BorderBrush(Theme.CardStroke);

    /// <summary>左侧队伍页签骨架：两个 44 高的页签位 + 底部分隔线。</summary>
    private static Element TeamTabSkeleton() =>
        Border(HStack(8, Block(90, 16), Block(90, 16)))
            .Height(GameInfoPage.TeamTabHeight)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .BorderBrush(Theme.CardStroke);

    /// <summary>
    /// 左侧玩家卡骨架：5 张等分铺满（与真实列表的 Star 行等分一致），
    /// 卡片自带 8 的下间距形成缝隙。
    /// </summary>
    private static Element PlayerCardSkeleton(int count)
    {
        var rows = Enumerable.Range(0, count).Select(_ => GridSize.Star()).ToArray();
        var cards = Enumerable.Range(0, count)
            .Select(index => Border(Empty())
                .Background(Theme.SubtleFill)
                .CornerRadius(YuumiTheme.OverlayRadius)
                .Margin(0, 0, 0, GameInfoPage.CardGap)
                .Grid(row: index, column: 0)
                .WithKey($"gameinfo-skeleton-card-{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
            .ToArray();

        return Grid([GridSize.Star()], rows, cards)
            .Padding(GameInfoPage.CardGap, GameInfoPage.CardGap, GameInfoPage.CardGap, 0);
    }

    /// <summary>右侧战绩列骨架：等宽列，每列一块灰底。</summary>
    private static Element ColumnSkeleton(int count)
    {
        var columns = Enumerable.Range(0, count).Select(_ => GridSize.Star()).ToArray();
        var cells = Enumerable.Range(0, count)
            .Select(index => Border(Empty())
                .Background(Theme.SubtleFill)
                .CornerRadius(YuumiTheme.OverlayRadius)
                .Margin(0, 0, GameInfoPage.CardGap, 0)
                .Grid(row: 0, column: index)
                .WithKey($"gameinfo-skeleton-col-{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
            .ToArray();

        return Grid(columns, [GridSize.Star()], cells);
    }

    /// <summary>骨架里的灰块：统一底色与圆角，尺寸由调用方给。</summary>
    private static Element Block(double width, double height, double radius = 4) =>
        Border(Empty())
            .Width(width)
            .Height(height)
            .CornerRadius(radius)
            .Background(Theme.SubtleFill);
}
