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
    /// <summary>英雄头像边长（Riot 原生方 tile，4px 控件圆角）。</summary>
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

    /// <summary>卡片之间的纵向间隙（对齐 4px 网格）。</summary>
    public const double CardGap = 4;

    public static Element Render(
        MatchSummary summary,
        YuumiPalette palette,
        bool isSelected = false,
        Action? onSelect = null)
    {
        // 英雄图标按 Riot 原生方 tile 呈现：圆形裁剪会切进图标自带的暗角晕影，
        // 露出一圈脏边（常用英雄条已有同样问题，见 RecentSummaryView.ChampionCell）。
        Element champion = SearchVisuals.Icon(
            SearchVisuals.ChampionIcon(summary.ChampionId), ChampionSize, YuumiTheme.ControlRadius);

        var isEn = Composition.AppHost.Services?.I18n.CurrentLanguage == YuumiX.Core.Localization.SupportedLanguages.EnUs;
        var queueName = SearchFilters.TranslateQueueLabel(summary.QueueName, isEn);

        string stripColor = SearchVisuals.ResultAccent(palette, summary);
        string resultText = SearchVisuals.ResultText(summary);

        Element middle = VStack(2,
            HStack(6,
                Body(queueName).Foreground(Theme.PrimaryText),
                Body(resultText).Foreground(stripColor))
                .VAlign(VerticalAlignment.Center),
            HStack(6,
                Caption(summary.ShortDateText).Foreground(Theme.SecondaryText),
                SearchVisuals.Kda(summary.Kills, summary.Deaths, summary.Assists, palette, strong: true)));

        Element indicatorStrip = Border(Empty())
            .Width(4)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Background(stripColor)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Stretch)
            .Margin(0, 8, 0, 8);

        Element content = HStack(8, champion, middle)
            .VAlign(VerticalAlignment.Center)
            .Margin(12, 8, 8, 8);

        Element cardBody = Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                indicatorStrip,
                content,
            ]);

        var card = Border(cardBody)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch)
            .Margin(0, 0, 0, CardGap)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(isSelected ? Theme.SubtleFill : Theme.CardBackground);

        card = isSelected
            ? card.WithBorder(Theme.Accent, 2)
            : card.WithBorder(Theme.CardStroke, 1);

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

        return card;
    }

    /// <summary>
    /// 列表加载中或空状态的骨架卡：与真实卡片同结构，只画一个浅色圆角底块。
    /// </summary>
    public static Element Skeleton(YuumiPalette? palette = null)
    {
        var border = Border(Empty())
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch)
            .Margin(0, 0, 0, CardGap)
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

        return border;
    }
}
