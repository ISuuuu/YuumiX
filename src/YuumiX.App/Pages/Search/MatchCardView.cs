using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Match.Models;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 战绩列表的单条卡片：一屏能看到十几局，替代改造前那种「大卡片 + 多行信息」的布局。
///
/// 对应原 <c>Search.vue</c> 的左侧战绩列表：英雄头像 + 「队列名 / 日期 + KDA」两行，
/// 底色与描边按胜/负/重开取色（配置里那三套颜色）。
///
/// 做成静态渲染方法而不是 <see cref="Component"/>：卡片本身没有独立状态，
/// 选中态由外部的 <c>ListView</c> 决定，再包一层组件只会多一次上下文查找。
/// </summary>
internal static class MatchCardView
{
    /// <summary>英雄头像边长（圆形，半径取一半）。</summary>
    private const double ChampionSize = 36;

    /// <summary>
    /// 卡片宽度。必须显式给：<c>ListView</c> 的项容器不会把子元素横向拉伸，
    /// 不给宽度会塌成一条竖线（实测：不设宽度时卡片宽度约 2 px，整列看似空白）。
    /// 172 = 列表列 190 减去项容器内边距与滚动条的余量。
    /// </summary>
    public const double CardWidth = 172;

    /// <summary>
    /// 卡片高度。必须显式给：<c>ListView</c> 的项容器按"固定尺寸 + 拉伸"布局，
    /// 高度留空时内容被压成 0 高，表现为一整列空白方块（实测）。
    /// 56 = 8 内边距 ×2 + 36 头像 + 边框。
    /// </summary>
    public const double CardHeight = 56;

    /// <summary>卡片之间的纵向间隙。ListView 的项容器自身上下没有外边距，不加就会连成一片。</summary>
    private const double CardGap = 6;

    public static Element Render(
        MatchSummary summary,
        YuumiPalette palette)
    {
        Element champion = SearchVisuals.Icon(
            SearchVisuals.ChampionIcon(summary.ChampionId), ChampionSize, ChampionSize / 2);

        Element middle = VStack(2,
            TextBlock(summary.QueueName)
                .FontSize(12)
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
            HStack(6,
                TextBlock(summary.ShortDateText).FontSize(11).Foreground(Theme.SecondaryText),
                SearchVisuals.Kda(summary.Kills, summary.Deaths, summary.Assists, 11, palette)));

        // 刻意不接收选中态：卡片随选中重建会让 ListView 反复抛 SelectionChanged，
        // 形成渲染自激（整片闪烁）。选中视觉由 ListView 自身高亮承担。
        Element card = Border(HStack(8, champion, middle))
            .Width(CardWidth)
            .Height(CardHeight)
            .Padding(8)
            .CornerRadius(8)
            .Background(SearchVisuals.CardBackground(palette, summary))
            .WithBorder(SearchVisuals.ResultBorder(palette, summary), 1);

        return VStack(0, card, SearchVisuals.VerticalGap(CardGap)).Width(CardWidth);
    }

    /// <summary>
    /// 列表加载中的骨架卡：与真实卡片同尺寸，只画一个浅色底块。
    ///
    /// 不用转圈图标代替整片列表，是因为列表区域高度固定（≈9 张卡），
    /// 骨架能把面板撑住，图片与文字到达时不会有整页跳动。
    /// </summary>
    public static Element Skeleton() =>
        VStack(0,
            Border(Empty())
                .Width(CardWidth)
                .Height(CardHeight)
                .CornerRadius(8)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke, 1),
            SearchVisuals.VerticalGap(CardGap)).Width(CardWidth);
}
