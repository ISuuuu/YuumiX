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
    Action? OnSelect = null,
    string? Language = null);

/// <summary>
/// 生涯页的全宽单条对局卡片，对应原 <c>MatchCard.vue</c>：
/// [头像+等级角标] | [召唤师技能+符文] | [胜利/TFT名次/模式/时长] | [KDA+阈值配色比率] | [补刀+分均刀] | [海克斯强化+7格装备] | 右侧：[金币+地图+时间+回放]
/// （金币挂在右侧组、紧贴时间列右靠，装备数量差异不影响其列位）
///
/// 相比旧版的差异保留项：分均补刀（新版新增）、位置用文本（国服客户端同样不渲染位置图标）。
///
/// 本组件是 <see cref="Component"/> 而非静态工厂，原因有二（均为裁决项）：
/// 1. D6——回放按钮默认透明、整行悬停（或键盘 focus 落入）时浮现，悬停状态需要 <c>UseState</c>；
/// 2. 整行点击跳战绩详情（§4），回放按钮区域必须拦截点击，以事件接力的方式由组件内部处理。
/// </summary>
public sealed class CareerMatchRow : Component<CareerMatchRowProps>
{
    /// <summary>英雄头像：旧版 `.champ-avatar` 52×52（卡内最高元素，决定行高）。</summary>
    private const double ChampionSize = 52;

    /// <summary>装备格：旧版 `.item-slot` / `.ward-slot` 28×28。</summary>
    private const double ItemSize = 28;

    /// <summary>海克斯强化格：旧版 `.augment-slot` 22×22（与装备格尺寸不同，不能共用 <see cref="ItemSize"/>）。</summary>
    private const double AugmentSize = 22;

    /// <summary>召唤师技能与基石符文：旧版 `.spell-slot` / `.rune-slot` 22×22。</summary>
    private const double SpellSize = 22;

    /// <summary>
    /// 行高：旧版 `.match-card` 固定 82 = 52（头像）+ 14×2（padding）+ 1×2（border）。
    /// 本实现 padding 取 12（14 不在 4px 网格上），卡片高度仍是 82，差额落在上下居中留白里。
    ///
    /// 可见性放宽到 internal：加载态的骨架卡必须与真实卡片同高，
    /// 高度值不能各写一份（两处一旦漂移，数据到达时列表就会跳）。
    /// </summary>
    internal const double RowHeight = 82;

    /// <summary>
    /// 结果列：旧版 `.result-panel { min-width: 90px }`。取 100 让「海克斯大乱斗」等最长模式名
    /// 在 14px 下留足呼吸感，与旧版占比一致。
    /// </summary>
    private const double ResultWidth = 100;

    /// <summary>KDA 列：旧版 `.kda-panel { min-width: 105px }`。</summary>
    private const double KdaWidth = 105;

    /// <summary>补刀列：旧版 `.cs-panel` 44 + `margin-right: 6`，合计视觉宽 50，取 56 承载 14px 补刀数值。</summary>
    private const double CsWidth = 56;

    /// <summary>金币列：旧版 `.gold-panel { min-width: 62px }`，取 64。</summary>
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

        var i18n = Composition.AppHost.Services?.I18n;
        var isEn = i18n?.CurrentLanguage == YuumiX.Core.Localization.SupportedLanguages.EnUs;

        // 结果列：TFT 名次（Placement 非空显示「第 N 名」，第 1 金色、≤4 胜利色、>4 失败色）优先于胜负。
        // 重开局沿用次要文字色（ThemeRef），其余走具体色值——两种形态分别调用 Foreground。
        string resultText;
        if (summary.Placement is { } placement)
        {
            var template = i18n?.T("career.placement", "第 {placement} 名") ?? "第 {placement} 名";
            resultText = template.Replace("{placement}", placement.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            resultText = Search.SearchVisuals.ResultText(summary);
        }

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
                    .Padding(4, 1)
                    .CornerRadius(YuumiTheme.ControlRadius)
                    .Background(Theme.CardBackground)
                    .WithBorder(Theme.CardStroke, 1)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Bottom),
            ])
            .Width(ChampionSize)
            .Height(ChampionSize);

        // 召唤师技能（上下两个小图标）+ 基石符文
        // 旧版 `.spells-runes` 是 flex row（gap 3）：左列 `.spells-col` 竖排两个技能，
        // 符文 `.rune-slot` 在**右侧**独立一格。此前把符文接在技能下方（VStack 第三行），
        // 整块会高出一格、与旧版形状不符。
        Element spellsCol = VStack(2,
            Search.SearchVisuals.SpellIcon(catalog, summary.Spell1Id, SpellSize),
            Search.SearchVisuals.SpellIcon(catalog, summary.Spell2Id, SpellSize));

        // 符文：仅非强化队列显示；RuneId=0 不渲染（SGP 大乱斗恒 0，与无符文等价）。
        // 无符文的行（大乱斗等模式）保留等宽空占位，保证不同模式混排时后续列（结果/KDA…）横向对齐。
        string? runePath = null;
        if (!IsAugmentQueue(summary.QueueId) && summary.RuneId > 0
            && catalog.Runes.TryGetValue(summary.RuneId, out string? runeIcon))
        {
            runePath = runeIcon;
        }

        // 空占位必须由 Border 承载（裸 Empty 不参与布局测量、量出 0 宽，占不住位）
        Element runeSlot = runePath is null
            ? Border(Empty()).Width(SpellSize).Height(SpellSize).AccessibilityHidden()
            : Search.SearchVisuals.Icon(runePath, SpellSize, 10);
        Element spells = HStack(3, spellsCol, runeSlot)
            .VAlign(VerticalAlignment.Center);

        // 结果与模式列
        var queueName = Search.SearchFilters.TranslateQueueLabel(summary.QueueName, isEn);
        var posIcon = CareerVisuals.PositionIcon(summary.Position, 14);
        var posText = Search.SearchVisuals.PositionText(summary.Position);

        var resultHeaderItems = new List<Element> { resultTextBlock };
        if (summary.Position.HasValue)
        {
            resultHeaderItems.Add(posIcon.Margin(2, 0, 0, 0));
        }
        if (!string.IsNullOrEmpty(posText))
        {
            resultHeaderItems.Add(
                Body(posText)
                    .Foreground(Theme.SecondaryText)
                    .VAlign(VerticalAlignment.Center));
        }

        Element result = VStack(2,
                HStack(4, resultHeaderItems.ToArray()),
                Body(queueName).Foreground(Theme.PrimaryText),
                Caption(summary.DurationText).Foreground(Theme.SecondaryText))
            .Width(ResultWidth)
            .VAlign(VerticalAlignment.Center);

        // KDA 列（D4）：比率按旧版阈值配色——Perfect 橙 / ≥5 玫红 / ≥3 蓝 / 其余次要灰；死亡数走 palette.DeathText
        string kdaRatioText = summary.Deaths == 0 ? "Perfect KDA" : $"{summary.KdaText} KDA";
        string? kdaRatioColor = KdaRatioColor(summary);
        Element kdaTextRow = HStack(2,
            BodyStrong(summary.Kills.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Body(" / ").Foreground(Theme.SecondaryText),
            BodyStrong(summary.Deaths.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Foreground(palette.DeathText),
            Body(" / ").Foreground(Theme.SecondaryText),
            BodyStrong(summary.Assists.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        Element kdaRatioBlock = Caption(kdaRatioText).SemiBold();
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
        var csUnit = i18n?.T("career.creepScoreUnit", " 刀") ?? " 刀";
        var csPerMinFormat = i18n?.T("career.creepScorePerMin", "({cs}/分)") ?? "({cs}/分)";
        var csPerMinText = csPerMinFormat.Replace("{cs}", csPerMin.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        Element cs = VStack(2,
                HStack(2,
                    BodyStrong(summary.CreepScore.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    Caption(csUnit).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Bottom)),
                Caption(csPerMinText).Foreground(Theme.SecondaryText))
            .Width(CsWidth)
            .VAlign(VerticalAlignment.Center);

        // 装备列：海克斯强化（仅 2400/2450，去重保序最多 5 个，悬停显名称）+ 装备（未出装格子不显示）
        var itemChildren = new List<Element>();
        if (IsAugmentQueue(summary.QueueId) && summary.AugmentIds.Count > 0)
        {
            itemChildren.Add(HStack(2, summary.AugmentIds
                .Select(augmentId => AugmentIcon(catalog, augmentId)
                    .WithKey(augmentId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray()));
        }

        itemChildren.Add(Search.SearchVisuals.ItemRow(catalog, summary.ItemIds, ItemSize));

        Element itemSlots = HStack(4, itemChildren.ToArray())
            .VAlign(VerticalAlignment.Center);

        // 金币列：挂在右侧组、紧贴时间列右靠，位置恒定（不随装备数量左右漂移）
        var goldText = i18n?.T("search.gold", "金币") ?? "金币";
        Element gold = VStack(2,
                BodyStrong($"{summary.Gold:N0}"),
                Caption(goldText).Foreground(Theme.SecondaryText))
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

        Element rightGroup = HStack(8, gold, info, replayBtn)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center);

        // 左侧主体组合
        Element leftGroup = HStack(8,
            avatar,
            spells,
            result,
            kda,
            cs,
            itemSlots)
            .VAlign(VerticalAlignment.Center);

        // 自适应 Grid 两列布局：左侧拉伸，右侧自动靠右
        Element cardContent = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                leftGroup.Grid(row: 0, column: 0),
                rightGroup.Grid(row: 0, column: 1),
            ]);

        // 方案 A：卡片底走中性 CardBackground（悬停走 SubtleFill），左侧保留 4px 胜负色条
        string stripColor = summary.Remake
            ? palette.RemakeText
            : summary.Placement is { } tftRank
                ? tftRank == 1 ? AssetColors.Amber : tftRank <= 4 ? palette.WinText : palette.LoseText
                : summary.Win ? palette.WinText : palette.LoseText;

        Element indicatorStrip = Border(Empty())
            .Width(4)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Background(stripColor)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Stretch)
            .Margin(0, 8, 0, 8);

        Element rowContent = Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                indicatorStrip,
                cardContent.Margin(20, 12, 20, 12),
            ]);

        var row = Border(rowContent)
            .Height(RowHeight)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(isHovered ? Theme.SubtleFill : Theme.CardBackground)
            .AutomationName($"查看对局 {summary.GameId}");
        row = isHovered
            ? row.WithBorder(Theme.ControlStroke, 1)
            : row.WithBorder(Theme.CardStroke, 1);
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
        Search.SearchVisuals.AugmentIcon(catalog, augmentId, AugmentSize);
}
