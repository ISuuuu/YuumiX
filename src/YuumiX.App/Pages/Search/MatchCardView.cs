using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Match.Models;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 战绩列表的单条卡片：一屏能看到十几局，替代改造前那种「大卡片 + 多行信息」的布局。
///
/// 对应原 <c>Search.vue</c> 的左侧战绩列表：英雄头像 + 「队列名 / 日期 + KDA」两行，
/// 底色与描边按胜/负/重开取色（配置里那三套颜色）。
/// </summary>
internal static class MatchCardView
{
    /// <summary>英雄头像边长（圆形，半径取一半）。</summary>
    private const double ChampionSize = 36;

    /// <summary>
    /// 卡片宽度。
    /// 172 = 列表列 190 减去内边距与滚动条的余量。
    /// </summary>
    public const double CardWidth = 172;

    /// <summary>
    /// 卡片高度。
    /// 56 = 8 内边距 ×2 + 36 头像 + 边框。
    /// </summary>
    public const double CardHeight = 56;

    /// <summary>卡片之间的纵向间隙。</summary>
    private const double CardGap = 6;

    public static Element Render(
        MatchSummary summary,
        YuumiPalette palette,
        bool isSelected = false,
        Action? onSelect = null)
    {
        Element champion = SearchVisuals.Icon(
            SearchVisuals.ChampionIcon(summary.ChampionId), ChampionSize, ChampionSize / 2);

        Element middle = VStack(2,
            Caption(summary.QueueName)
                .Foreground(Theme.PrimaryText),
            HStack(6,
                Caption(summary.ShortDateText).Foreground(Theme.SecondaryText),
                SearchVisuals.Kda(summary.Kills, summary.Deaths, summary.Assists, palette)));

        string borderBrush;
        string bg;
        double borderThickness;

        if (isSelected)
        {
            borderThickness = 2;
            if (summary.Remake)
            {
                borderBrush = palette.RemakeText;
                bg = palette.RemakeBgHover;
            }
            else
            {
                borderBrush = summary.Win ? palette.WinText : palette.LoseText;
                bg = summary.Win ? palette.WinBgHover : palette.LoseBgHover;
            }
        }
        else
        {
            borderThickness = 1;
            if (summary.Remake)
            {
                borderBrush = palette.RemakeBorder;
                bg = palette.RemakeBg;
            }
            else
            {
                borderBrush = summary.Win ? palette.WinBorder : palette.LoseBorder;
                bg = summary.Win ? palette.WinBg : palette.LoseBg;
            }
        }

        Element card = Border(HStack(8, champion, middle).VAlign(VerticalAlignment.Center))
            .Width(CardWidth)
            .Height(CardHeight)
            .Padding(8)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(bg)
            .WithBorder(borderBrush, borderThickness);

        if (onSelect is not null)
        {
            card = card
                .IsTabStop(true)
                .OnKeyDown((_, args) =>
                {
                    if (args.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
                    {
                        onSelect();
                        args.Handled = true;
                    }
                })
                .OnTapped((_, _) => onSelect());
        }

        return VStack(0, card, SearchVisuals.VerticalGap(CardGap)).Width(CardWidth);
    }

    /// <summary>
    /// 列表加载中或空状态的骨架卡：与真实卡片同尺寸，只画一个浅色圆角底块。
    /// </summary>
    public static Element Skeleton(YuumiPalette? palette = null)
    {
        var border = Border(Empty())
            .Width(CardWidth)
            .Height(CardHeight)
            .CornerRadius(YuumiTheme.OverlayRadius);

        if (palette is not null)
        {
            border = border
                .Background(Theme.SubtleFill)
                .WithBorder(Theme.CardStroke, 1);
        }
        else
        {
            border = border
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke, 1);
        }

        return VStack(0, border, SearchVisuals.VerticalGap(CardGap)).Width(CardWidth);
    }
}
