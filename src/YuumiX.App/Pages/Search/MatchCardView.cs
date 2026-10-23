using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 战绩列表的单条卡片。
///
/// 对应原 <c>Search.vue</c> 的战绩卡片行（胜/负/重开三色底 + 英雄头像 + KDA + 装备条）。
/// 做成静态渲染方法而不是 <see cref="Component"/>：卡片本身没有独立状态，
/// 选中态由外部的 <c>ListView</c> 决定，再包一层组件只会多一次上下文查找。
/// </summary>
internal static class MatchCardView
{
    /// <summary>英雄头像边长。</summary>
    private const double ChampionSize = 44;

    /// <summary>装备图标边长。</summary>
    private const double ItemSize = 20;

    /// <summary>
    /// 卡片宽度。ListView 的项容器不会把子元素横向拉伸，不显式给宽度会塌成一条竖线
    /// （实测：不设宽度时卡片宽度约 2 px，整列看似空白）。
    /// </summary>
    public const double CardWidth = 470;

    /// <summary>
    /// 卡片高度。必须显式给：<c>ListView</c> 的项容器按"固定尺寸 + 拉伸"布局，
    /// 高度留空时内容被压成 0 高，表现为一整列空白方块（实测）。
    /// 76 = 10 内边距 ×2 + 44 英雄头像 + 边框，容纳三行文字。
    /// </summary>
    private const double CardHeight = 76;

    public static Element Render(
        MatchSummary summary,
        GameDataCatalog catalog,
        YuumiPalette palette)
    {
        var items = new Element[SearchVisuals.ItemSlotCount];
        for (var slot = 0; slot < items.Length; slot++)
        {
            int itemId = slot < summary.ItemIds.Count ? summary.ItemIds[slot] : 0;
            items[slot] = SearchVisuals.ItemIcon(catalog, itemId, ItemSize);
        }

        Element champion = SearchVisuals.Icon(SearchVisuals.ChampionIcon(summary.ChampionId), ChampionSize, 6);

        Element middle = VStack(3,
            HStack(8,
                TextBlock(summary.QueueName).FontSize(13).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
                TextBlock(summary.ShortTimeText).FontSize(11).Foreground(Theme.SecondaryText),
                TextBlock(SearchVisuals.PositionText(summary.Position) ?? "无分路").FontSize(11)
                    .Foreground(Theme.SecondaryText)),
            HStack(10,
                TextBlock($"{summary.Kills} / {summary.Deaths} / {summary.Assists}").FontSize(13),
                TextBlock($"KDA {summary.KdaText}").FontSize(11).Foreground(Theme.SecondaryText),
                TextBlock($"补刀 {summary.CreepScore}").FontSize(11).Foreground(Theme.SecondaryText)),
            HStack(2, items));

        Element right = VStack(3,
            TextBlock(SearchVisuals.ResultText(summary)).FontSize(13).Foreground(palette.Accent),
            TextBlock(summary.DurationText).FontSize(11).Foreground(Theme.SecondaryText));

        // 刻意不接收选中态：卡片随选中重建会让 ListView 反复抛 SelectionChanged，
        // 形成渲染自激（整片闪烁）。选中视觉由 ListView 自身高亮承担。
        return Border(HStack(12, champion, middle, right))
            .Width(CardWidth)
            .Height(CardHeight)
            .Padding(10)
            .CornerRadius(6)
            .Background(SearchVisuals.CardBackground(palette, summary))
            .WithBorder(Theme.CardStroke, 1);
    }
}
