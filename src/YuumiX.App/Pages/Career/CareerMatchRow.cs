using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Career;

/// <summary>生涯对局卡片入参。<see cref="OnSelect"/> 为整行点击（跳战绩详情）。</summary>
public sealed record CareerMatchRowProps(
    MatchSummary Summary,
    GameDataCatalog Catalog,
    YuumiPalette Palette,
    Action? OnSelect = null);

/// <summary>
/// 生涯页的全宽单条对局卡片，对应原 <c>MatchCard.vue</c>：
/// [头像+等级角标] | [召唤师技能+符文] | [胜利/TFT名次/模式/时长] | [KDA+阈值配色比率] | [补刀+分均刀] | [海克斯强化+7格装备] | [金币] | 右侧：[地图+时间+回放]
///
/// 相比旧版的差异保留项：分均补刀（新版新增）、位置用文本（国服客户端同样不渲染位置图标）。
///
/// 本组件是 <see cref="Component"/> 而非静态工厂，原因有二（均为裁决项）：
/// 1. D6——回放按钮默认透明、整行悬停（或键盘 focus 落入）时浮现，悬停状态需要 <c>UseState</c>；
/// 2. 整行点击跳战绩详情（§4），回放按钮区域必须拦截点击，以事件接力的方式由组件内部处理。
/// </summary>
public sealed class CareerMatchRow : Component<CareerMatchRowProps>
{
    private const double ChampionSize = 46;
    private const double ItemSize = 22;
    private const double RowHeight = 80;

    private const double ResultWidth = 115;
    private const double KdaWidth = 105;
    private const double CsWidth = 70;
    private const double ItemsWidth = 175;
    private const double GoldWidth = 75;

    /// <summary>海克斯大乱斗两个队列：强化列只在这里显示、基石符文在这里隐藏（旧版 MatchCard.vue 同规则）。</summary>
    private static bool IsAugmentQueue(int queueId) => queueId is 2400 or 2450;

    public override Element Render()
    {
        MatchSummary summary = Props.Summary;
        GameDataCatalog catalog = Props.Catalog;
        YuumiPalette palette = Props.Palette;

        var (replayRevealed, setReplayRevealed) = UseState(false, threadSafe: true);

        // 7 格装备（6 核心装备 + 1 饰品守卫）
        var items = new Element[Search.SearchVisuals.ItemSlotCount];
        for (var slot = 0; slot < items.Length; slot++)
        {
            int itemId = slot < summary.ItemIds.Count ? summary.ItemIds[slot] : 0;
            items[slot] = Search.SearchVisuals.ItemIcon(catalog, itemId, ItemSize);
        }

        // 结果列：TFT 名次（Placement 非空显示「第 N 名」，第 1 金色、≤4 胜利色、>4 失败色）优先于胜负。
        // 重开局沿用次要文字色（ThemeRef），其余走具体色值——两种形态分别调用 Foreground。
        string resultText = summary.Placement is { } placement
            ? $"第 {placement} 名"
            : Search.SearchVisuals.ResultText(summary);
        string? resultColor = summary.Remake
            ? null
            : summary.Placement is { } rank
                ? rank == 1 ? "#FFF59E0B" : rank <= 4 ? CareerVisuals.WinText : CareerVisuals.LoseText
                : summary.Win ? CareerVisuals.WinText : CareerVisuals.LoseText;
        Element resultTextBlock = TextBlock(resultText)
            .FontSize(14)
            .FontWeight(Microsoft.UI.Text.FontWeights.Bold);
        resultTextBlock = resultColor is null
            ? resultTextBlock.Foreground(Theme.SecondaryText)
            : resultTextBlock.Foreground(resultColor);

        // 头像 + 等级角标 + 召唤师技能 + 基石符文
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

        // 召唤师技能（上下两个小图标）；2400/2450 队列下方放基石符文，其余模式隐藏
        var spellsChildren = new List<Element>
        {
            Search.SearchVisuals.SpellIcon(catalog, summary.Spell1Id, 21),
            Search.SearchVisuals.SpellIcon(catalog, summary.Spell2Id, 21),
        };

        // 符文：仅非强化队列显示；RuneId=0 不渲染（SGP 大乱斗恒 0，与无符文等价）
        if (!IsAugmentQueue(summary.QueueId) && summary.RuneId > 0)
        {
            string? runePath = catalog.Runes.TryGetValue(summary.RuneId, out string? icon) ? icon : null;
            spellsChildren.Add(Search.SearchVisuals.Icon(runePath, 21, 10));
        }

        Element spells = VStack(2, spellsChildren.ToArray())
            .VAlign(VerticalAlignment.Center);

        // 结果与模式列
        Element result = VStack(2,
                HStack(4,
                    resultTextBlock,
                    TextBlock(Search.SearchVisuals.PositionText(summary.Position) ?? string.Empty)
                        .FontSize(11)
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)),
                TextBlock(summary.QueueName).FontSize(11).Foreground(Theme.SecondaryText),
                TextBlock(summary.DurationText).FontSize(11).Foreground(Theme.SecondaryText))
            .Width(ResultWidth)
            .VAlign(VerticalAlignment.Center);

        // KDA 列（D4）：比率按旧版阈值配色——Perfect 橙 / ≥5 玫红 / ≥3 蓝 / 其余次要灰；死亡数标红
        string kdaRatioText = summary.Deaths == 0 ? "Perfect KDA" : $"{summary.KdaText} KDA";
        string? kdaRatioColor = KdaRatioColor(summary);
        Element kdaTextRow = HStack(2,
            TextBlock($"{summary.Kills} / ").FontSize(13).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
            TextBlock(summary.Deaths.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .FontSize(13)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .Foreground(CareerVisuals.LoseText),
            TextBlock($" / {summary.Assists}").FontSize(13).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold));

        Element kdaRatioBlock = TextBlock(kdaRatioText)
            .FontSize(11)
            .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold);
        kdaRatioBlock = kdaRatioColor is null
            ? kdaRatioBlock.Foreground(Theme.SecondaryText)
            : kdaRatioBlock.Foreground(kdaRatioColor);

        Element kda = VStack(2,
                kdaTextRow,
                kdaRatioBlock)
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

        // 装备列：海克斯强化（仅 2400/2450，去重保序最多 5 个，悬停显名称）+ 固定 7 格
        var itemChildren = new List<Element>();
        if (IsAugmentQueue(summary.QueueId) && summary.AugmentIds.Count > 0)
        {
            itemChildren.Add(HStack(2, summary.AugmentIds
                .Select(augmentId => AugmentIcon(catalog, augmentId)
                    .WithKey(augmentId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray()));
        }

        itemChildren.Add(HStack(3, items));

        Element itemSlots = HStack(4, itemChildren.ToArray())
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

        // D6：回放按钮默认透明且不参与命中，整行悬停或键盘 focus 落入时浮现；
        // 悬停期间按钮自身拦截点击，不会透传给整行的 OnTapped。
        Element replayBtn = Component<ReplayButton, ReplayButtonProps>(
                new ReplayButtonProps(summary.GameId, Compact: true))
            .Opacity(replayRevealed ? 1 : 0)
            .IsHitTestVisible(replayRevealed)
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
            .WithBorder(CareerVisuals.WithAlpha(summary.Win ? CareerVisuals.WinText : (summary.Remake ? palette.RemakeCard : CareerVisuals.LoseText), 0x33), 1)
            .AutomationName($"查看对局 {summary.GameId}")
            // D6：悬停（含 focus 落入行内按钮）浮现回放；整行点击跳战绩详情（§4）
            .OnPointerEntered((_, _) => setReplayRevealed(true))
            .OnPointerExited((_, _) => setReplayRevealed(false))
            .OnGotFocus((_, _) => setReplayRevealed(true))
            .OnLostFocus((_, _) => setReplayRevealed(false))
            // 键盘可达（REACTOR_A11Y_004）：Tab 聚焦后 Enter/Space 等效点击
            .IsTabStop(true)
            .OnKeyDown((_, args) =>
            {
                if (args.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
                {
                    Props.OnSelect?.Invoke();
                    args.Handled = true;
                }
            })
            .OnTapped((_, _) => Props.OnSelect?.Invoke());
    }

    /// <summary>
    /// KDA 比率阈值色（D4，对齐旧版 MatchCard.vue 的 kda-perfect/great/good/normal）。
    /// 次要灰没有具体色值（主题令牌是 ThemeRef），返回 <c>null</c> 由调用方走 <c>Theme.SecondaryText</c>。
    /// </summary>
    private static string? KdaRatioColor(MatchSummary summary)
    {
        if (summary.Deaths == 0)
        {
            return "#FFD97706"; // Perfect → 琥珀
        }

        double ratio = (summary.Kills + summary.Assists) / (double)summary.Deaths;
        return ratio switch
        {
            >= 5 => "#FFDB2777", // 玫红
            >= 3 => "#FF2563EB", // 蓝
            _ => null,           // 次要灰（主题令牌）
        };
    }

    /// <summary>海克斯强化图标：紫色边框小格，悬停 tooltip 显示名称（旧版 .augment-slot 样式）。</summary>
    private static Element AugmentIcon(GameDataCatalog catalog, int augmentId)
    {
        string name = catalog.Augments.TryGetValue(augmentId, out CherryAugmentDetail? detail) ? detail.Name : "海克斯强化";
        string? iconPath = detail?.IconPath;

        return Search.SearchVisuals.Icon(iconPath, ItemSize, 4)
            .WithToolTip(TextBlock(name).FontSize(12))
            .AutomationName($"海克斯强化：{name}");
    }
}
