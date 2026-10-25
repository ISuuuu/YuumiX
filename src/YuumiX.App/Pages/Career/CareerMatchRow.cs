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
    private const double CsWidth = 56;
    private const double GoldWidth = 64;

    /// <summary>海克斯大乱斗两个队列：强化列只在这里显示、基石符文在这里隐藏（旧版 MatchCard.vue 同规则）。</summary>
    private static bool IsAugmentQueue(int queueId) => queueId is 2400 or 2450;

    public override Element Render()
    {
        MatchSummary summary = Props.Summary;
        GameDataCatalog catalog = Props.Catalog;
        YuumiPalette palette = Props.Palette;

        var (replayRevealed, setReplayRevealed) = UseState(false, threadSafe: true);
        var (isHovered, setIsHovered) = UseState(false, threadSafe: true);

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
                ? rank == 1 ? AssetColors.Amber : rank <= 4 ? CareerVisuals.WinText : CareerVisuals.LoseText
                : summary.Win ? CareerVisuals.WinText : CareerVisuals.LoseText;
        Element resultTextBlock = BodyStrong(resultText);
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
                Border(Caption(summary.ChampionLevel.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .SemiBold()
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
                    Caption(Search.SearchVisuals.PositionText(summary.Position) ?? string.Empty)
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)),
                Caption(summary.QueueName).Foreground(Theme.SecondaryText),
                Caption(summary.DurationText).Foreground(Theme.SecondaryText))
            .Width(ResultWidth)
            .VAlign(VerticalAlignment.Center);

        // KDA 列（D4）：比率按旧版阈值配色——Perfect 橙 / ≥5 玫红 / ≥3 蓝 / 其余次要灰；死亡数走 palette.DeathText
        string kdaRatioText = summary.Deaths == 0 ? "Perfect KDA" : $"{summary.KdaText} KDA";
        string? kdaRatioColor = KdaRatioColor(summary);
        Element kdaTextRow = HStack(2,
            BodyStrong($"{summary.Kills} / "),
            BodyStrong(summary.Deaths.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Foreground(palette.DeathText),
            BodyStrong($" / {summary.Assists}"));

        Element kdaRatioBlock = Caption(kdaRatioText);
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
                Caption($"{summary.CreepScore} 刀"),
                Caption($"({csPerMin:F1}/分)").Foreground(Theme.SecondaryText))
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
            .VAlign(VerticalAlignment.Center);

        // 金币列
        Element gold = VStack(2,
                Caption($"{summary.Gold:N0}")
                    .SemiBold(),
                Caption("金币").Foreground(Theme.SecondaryText))
            .Width(GoldWidth)
            .VAlign(VerticalAlignment.Center);

        // 地图与时间列（自适应靠右，日期格式对齐原系统 yyyy/MM/dd HH:mm）
        string formattedTime = summary.DetailDateText;
        Element info = VStack(3,
                Caption(summary.MapName)
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Right),
                Caption(formattedTime)
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Right))
            .Width(116)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center);

        // D6：回放按钮默认透明且不参与命中，整行悬停或键盘 focus 落入时浮现；
        // 包裹透明 Border 拦截点击，避免事件冒泡触发整行 OnSelect（防误跳战绩）。
        Element replayBtn = Border(Component<ReplayButton, ReplayButtonProps>(
                new ReplayButtonProps(summary.GameId, Compact: false)))
            .Opacity(replayRevealed ? 1 : 0)
            .IsHitTestVisible(replayRevealed)
            .Background("Transparent")
            .OnTapped((_, args) => args.Handled = true)
            .VAlign(VerticalAlignment.Center);

        Element rightGroup = HStack(8, info, replayBtn)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center);

        // 左侧主体组合
        Element leftGroup = HStack(8,
            avatar,
            spells,
            result,
            kda,
            cs,
            itemSlots,
            gold)
            .VAlign(VerticalAlignment.Center);

        // 自适应 Grid 两列布局：左侧拉伸，右侧自动靠右
        Element cardContent = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                leftGroup.Grid(row: 0, column: 0),
                rightGroup.Grid(row: 0, column: 1),
            ]);

        // 卡片底色、悬停底色与描边联动
        string normalBg = summary.Remake ? palette.RemakeBg : (summary.Win ? palette.WinBg : palette.LoseBg);
        string hoverBg = summary.Remake ? palette.RemakeBgHover : (summary.Win ? palette.WinBgHover : palette.LoseBgHover);
        string normalBorder = summary.Remake ? palette.RemakeBorder : (summary.Win ? palette.WinBorder : palette.LoseBorder);

        string currentBg = isHovered ? hoverBg : normalBg;

        // 业务描边是 string、悬停描边是 ThemeRef 令牌，两种形态无法共存于同一三元，因此在元素级分支。
        var row = Border(cardContent)
            .Height(RowHeight)
            .Padding(14, 8)
            .CornerRadius(8)
            .Background(currentBg)
            .AutomationName($"查看对局 {summary.GameId}");
        row = isHovered
            ? row.WithBorder(Theme.ControlStroke, 1)
            : row.WithBorder(normalBorder, 1);
        return row
            // D6：悬停（含 focus 落入行内按钮）浮现回放；整行点击跳战绩详情（§4）
            .OnPointerEntered((_, _) => { setReplayRevealed(true); setIsHovered(true); })
            .OnPointerExited((_, _) => { setReplayRevealed(false); setIsHovered(false); })
            .OnGotFocus((_, _) => { setReplayRevealed(true); setIsHovered(true); })
            .OnLostFocus((_, _) => { setReplayRevealed(false); setIsHovered(false); })
            // 键盘可达（REACTOR_A11Y_004）：Tab 聚焦后 Enter/Space 等效点击
            .IsTabStop(true)
            .OnKeyDown((sender, args) =>
            {
                if (args.Handled || (args.OriginalSource is not null && args.OriginalSource != sender))
                {
                    return;
                }

                if (args.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
                {
                    Props.OnSelect?.Invoke();
                    args.Handled = true;
                }
            })
            .OnTapped((_, args) =>
            {
                if (!args.Handled)
                {
                    Props.OnSelect?.Invoke();
                }
            });
    }

    /// <summary>
    /// KDA 比率阈值色（D4，对齐旧版 MatchCard.vue 的 kda-perfect/great/good/normal）。
    /// 次要灰没有具体色值（主题令牌是 ThemeRef），返回 <c>null</c> 由调用方走 <c>Theme.SecondaryText</c>。
    /// </summary>
    private static string? KdaRatioColor(MatchSummary summary)
    {
        if (summary.Deaths == 0)
        {
            return AssetColors.AmberDeep; // Perfect → 琥珀
        }

        double ratio = (summary.Kills + summary.Assists) / (double)summary.Deaths;
        return ratio switch
        {
            >= 5 => AssetColors.Rose, // 玫红
            >= 3 => AssetColors.Blue, // 蓝
            _ => null,           // 次要灰（主题令牌）
        };
    }

    /// <summary>海克斯强化图标：紫色边框小格，悬停 tooltip 显示名称（旧版 .augment-slot 样式）。</summary>
    private static Element AugmentIcon(GameDataCatalog catalog, int augmentId) =>
        Search.SearchVisuals.AugmentIcon(catalog, augmentId, ItemSize);
}
