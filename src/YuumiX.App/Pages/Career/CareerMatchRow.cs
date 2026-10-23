using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Career;

/// <summary>
/// 生涯页的全宽单条对局卡片，严格对齐 docs/pic/生涯.png 视觉：
/// [头像+等级角标] | [召唤师技能] | [胜利/模式/时长] | [KDA+高亮比率] | [补刀+分均刀] | [7格装备] | [金币] | 右侧：[地图+时间]
/// </summary>
internal static class CareerMatchRow
{
    private const double ChampionSize = 46;
    private const double ItemSize = 22;
    private const double RowHeight = 80;

    private const double ResultWidth = 115;
    private const double KdaWidth = 105;
    private const double CsWidth = 70;
    private const double ItemsWidth = 175;
    private const double GoldWidth = 75;

    public static Element Render(
        MatchSummary summary,
        GameDataCatalog catalog,
        YuumiPalette palette,
        Action? onSelect = null)
    {
        // 7 格装备（6 核心装备 + 1 饰品守卫）
        var items = new Element[Search.SearchVisuals.ItemSlotCount];
        for (var slot = 0; slot < items.Length; slot++)
        {
            int itemId = slot < summary.ItemIds.Count ? summary.ItemIds[slot] : 0;
            items[slot] = Search.SearchVisuals.ItemIcon(catalog, itemId, ItemSize);
        }

        Element resultText = TextBlock(Search.SearchVisuals.ResultText(summary))
            .FontSize(14)
            .FontWeight(Microsoft.UI.Text.FontWeights.Bold);

        resultText = summary.Remake
            ? resultText.Foreground(Theme.SecondaryText)
            : resultText.Foreground(summary.Win ? CareerVisuals.WinText : CareerVisuals.LoseText);

        // 头像 + 等级角标
        Element avatar = Grid(
            [GridSize.Auto],
            [GridSize.Auto],
            [
                Search.SearchVisuals.Icon(
                    Search.SearchVisuals.ChampionIcon(summary.ChampionId),
                    ChampionSize,
                    8),
                Border(TextBlock(summary.ChampionLevel.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .FontSize(9)
                        .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                        .Foreground(Theme.PrimaryText)
                        .HAlign(HorizontalAlignment.Center))
                    .Padding(3, 0)
                    .CornerRadius(3)
                    .Background(Theme.CardBackground)
                    .WithBorder(Theme.CardStroke, 1)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Bottom),
            ])
            .Width(ChampionSize)
            .Height(ChampionSize);

        // 召唤师技能（上下两个小图标）
        Element spells = VStack(2,
            Search.SearchVisuals.SpellIcon(catalog, summary.Spell1Id, 21),
            Search.SearchVisuals.SpellIcon(catalog, summary.Spell2Id, 21))
            .VAlign(VerticalAlignment.Center);

        // 结果与模式列
        Element result = VStack(2,
                HStack(4,
                    resultText,
                    TextBlock(Search.SearchVisuals.PositionText(summary.Position) ?? string.Empty)
                        .FontSize(11)
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)),
                TextBlock(summary.QueueName).FontSize(11).Foreground(Theme.SecondaryText),
                TextBlock(summary.DurationText).FontSize(11).Foreground(Theme.SecondaryText))
            .Width(ResultWidth)
            .VAlign(VerticalAlignment.Center);

        // KDA 列（带高亮橙色 KDA 比率，死亡数标红）
        string kdaRatioText = summary.Deaths == 0 ? "Perfect KDA" : $"{summary.KdaText} KDA";
        Element kdaTextRow = HStack(2,
            TextBlock($"{summary.Kills} / ").FontSize(13).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
            TextBlock(summary.Deaths.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .FontSize(13)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .Foreground(CareerVisuals.LoseText),
            TextBlock($" / {summary.Assists}").FontSize(13).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold));

        Element kda = VStack(2,
                kdaTextRow,
                TextBlock(kdaRatioText)
                    .FontSize(11)
                    .Foreground(palette.Accent)
                    .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold))
            .Width(KdaWidth)
            .VAlign(VerticalAlignment.Center);

        // 补刀列
        double minutes = Math.Max(summary.DurationSeconds / 60.0, 1.0);
        double csPerMin = summary.CreepScore / minutes;
        Element cs = VStack(2,
                TextBlock($"{summary.CreepScore} 刀")
                    .FontSize(12)
                    .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
                TextBlock($"({csPerMin:F1}/分)").FontSize(10).Foreground(Theme.SecondaryText))
            .Width(CsWidth)
            .VAlign(VerticalAlignment.Center);

        // 装备列（固定 7 格宽度）
        Element itemSlots = HStack(3, items)
            .Width(ItemsWidth)
            .VAlign(VerticalAlignment.Center);

        // 金币列
        Element gold = VStack(2,
                TextBlock($"{summary.Gold:N0}")
                    .FontSize(12)
                    .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
                TextBlock("金币").FontSize(10).Foreground(Theme.SecondaryText))
            .Width(GoldWidth)
            .VAlign(VerticalAlignment.Center);

        // 地图与时间列（自适应靠右）
        Element info = VStack(3,
                TextBlock(summary.MapName)
                    .FontSize(11)
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Right),
                TextBlock(summary.TimeText)
                    .FontSize(11)
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Right))
            .Width(130)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center);

        Element replayBtn = Component<ReplayButton, ReplayButtonProps>(
            new ReplayButtonProps(summary.GameId, Compact: true))
            .VAlign(VerticalAlignment.Center);

        Element rightGroup = HStack(8, info, replayBtn)
            .Width(170)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center);

        // 左侧主体组合
        Element leftGroup = HStack(12,
            avatar,
            spells,
            result,
            kda,
            cs,
            itemSlots,
            gold)
            .VAlign(VerticalAlignment.Center);

        const double CardContentWidth = 920;
        double leftGroupWidth = CardContentWidth - 170;

        Element cardContent = HStack(0,
            leftGroup.Width(leftGroupWidth),
            rightGroup);

        return Border(cardContent)
            .Height(RowHeight)
            .Padding(14, 8)
            .CornerRadius(8)
            .Background(Search.SearchVisuals.CardBackground(palette, summary))
            .WithBorder(CareerVisuals.WithAlpha(summary.Win ? CareerVisuals.WinText : (summary.Remake ? palette.RemakeCard : CareerVisuals.LoseText), 0x33), 1);
    }
}
