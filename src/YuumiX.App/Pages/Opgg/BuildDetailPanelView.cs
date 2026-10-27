using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Opgg.Models;

namespace YuumiX.App.Pages.Opgg;

public sealed class BuildDetailPanelProps
{
    public required OpggBuildData Build { get; init; }
    public required int ChampionId { get; init; }
    public string? Position { get; init; }
    public required IReadOnlyDictionary<int, string> ChampionsMap { get; init; }
    public required IReadOnlyDictionary<int, string> ItemsMap { get; init; }
    public required IReadOnlyDictionary<int, string> SpellsMap { get; init; }
    public required IReadOnlyDictionary<int, string> RunesMap { get; init; }
    public required IReadOnlyList<PerkStyle> PerkStyles { get; init; }
    public required IReadOnlyDictionary<int, PerkDetail> PerkDetails { get; init; }
    public required bool IsDark { get; init; }
    public required Action<OpggRunePreset> OnApplyRune { get; init; }
    public required Action OnApplyItemSet { get; init; }
    public bool IsApplyingItemSet { get; init; }
}

/// <summary>
/// 英雄出装详情主视图（英雄头栏、召唤师技能、符文树、技能加点、装备推荐、克制/被克制双列榜）。
/// </summary>
public sealed class BuildDetailPanelView : Component<BuildDetailPanelProps>
{
    public override Element Render()
    {
        var build = Props.Build;

        return ScrollViewer(
            VStack(8,
                // 模块 1：英雄概览标题栏
                RenderTitleBar(build),

                // 模块 2：召唤师技能推荐卡片
                RenderSpellCard(build),

                // 模块 3：符文推荐卡片
                Component<RunesSectionView, RunesSectionProps>(new RunesSectionProps
                {
                    Runes = build.Runes,
                    PerkStyles = Props.PerkStyles,
                    PerkDetails = Props.PerkDetails,
                    RunesMap = Props.RunesMap,
                    OnApplyRune = Props.OnApplyRune
                }),

                // 模块 4：技能加点推荐卡片
                Component<SkillOrderSectionView, SkillOrderSectionProps>(new SkillOrderSectionProps
                {
                    Skills = build.Skills,
                    SkillMasteries = build.SkillMasteries
                }),

                // 模块 5：装备推荐与一键导入
                Component<ItemsSectionView, ItemsSectionProps>(new ItemsSectionProps
                {
                    StarterItems = build.StarterItems,
                    CoreItems = build.CoreItems,
                    Boots = build.Boots,
                    LastItems = build.LastItems,
                    ItemsMap = Props.ItemsMap,
                    OnApplyItemSet = Props.OnApplyItemSet,
                    IsApplying = Props.IsApplyingItemSet
                }),

                // 模块 6：克制与被克制双列卡片
                RenderCounterCard(build)
            )
            .Padding(new Thickness(0, 6, 0, 16))
        );
    }

    private Element RenderTitleBar(OpggBuildData build)
    {
        var champId = build.Summary?.Id ?? Props.ChampionId;
        var champName = Props.ChampionsMap.TryGetValue(champId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : (build.Summary?.Name ?? $"Champion {champId}");

        // 提取统计与 Tier
        OpggStats? stats = null;
        if (!string.IsNullOrWhiteSpace(Props.Position) && build.Summary?.Positions is not null)
        {
            stats = build.Summary.Positions.FirstOrDefault(p =>
                string.Equals(p.Name, Props.Position, StringComparison.OrdinalIgnoreCase))?.Stats;
        }
        stats ??= build.Summary?.AverageStats;

        var selectedTier = stats?.TierData?.Tier ?? stats?.Tier;
        var bg = OpggVisuals.GetTierCardBackground(selectedTier, Props.IsDark);
        var border = OpggVisuals.GetTierCardBorder(selectedTier, Props.IsDark);

        return Border(
            Grid(
                [GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto],
                [GridSize.Auto],
                [
                    // 头像
                    Component<LcuImage, LcuImageProps>(
                        new LcuImageProps($"/lol-game-data/assets/v1/champion-icons/{champId}.png", 46, CornerRadius: 23))
                        .VAlign(VerticalAlignment.Center)
                        .Grid(0, 0),

                    // 名称与分路
                    VStack(2,
                        BodyLarge(champName)
                            .SemiBold(),
                        string.IsNullOrWhiteSpace(Props.Position)
                            ? Empty()
                            : Caption(Props.Position.ToUpperInvariant())
                                .Foreground(Theme.SecondaryText)
                    )
                    .Margin(new Thickness(12, 0, 0, 0))
                    .VAlign(VerticalAlignment.Center)
                    .Grid(0, 1),

                    // 胜率 / 登场率 / 禁用率 三列
                    HStack(12,
                        StatCol("胜率", OpggVisuals.FormatPercent(stats?.WinRate)),
                        Divider(),
                        StatCol("登场率", OpggVisuals.FormatPercent(stats?.PickRate)),
                        Divider(),
                        StatCol("禁用率", OpggVisuals.FormatPercent(stats?.BanRate))
                    )
                    .VAlign(VerticalAlignment.Center)
                    .Margin(new Thickness(0, 0, 16, 0))
                    .Grid(0, 2),

                    // Tier 徽章
                    RenderTierBadge(selectedTier).Grid(0, 3)
                ]
            )
        )
        .Margin(new Thickness(12, 6, 12, 6))
        .Padding(new Thickness(16, 12, 16, 12))
        .CornerRadius(YuumiTheme.OverlayRadius)
        .Background(bg)
        .WithBorder(border, 1);
    }

    private static Element StatCol(string label, string val) =>
        VStack(2,
            Caption(label)
                .Foreground(Theme.SecondaryText)
                .HAlign(HorizontalAlignment.Center),
            BodyStrong(val)
                .HAlign(HorizontalAlignment.Center)
        );

    private static Element Divider() =>
        Border(Empty())
            .Width(1)
            .Height(22)
            .Background(Theme.CardStroke)
            .VAlign(VerticalAlignment.Center);

    private static Element RenderTierBadge(string? tier)
    {
        var t = (tier ?? "").Trim();
        var badgeColor = OpggVisuals.GetTierBadgeColor(t);
        var label = string.IsNullOrWhiteSpace(t) ? "-" : (t == "0" ? "T1" : $"T{t}");

        return Border(
            Caption(label)
                .Foreground("#FFFFFF")
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
        )
        .Padding(new Thickness(8, 4, 8, 4))
        .CornerRadius(YuumiTheme.ControlRadius)
        .Background(badgeColor)
        .VAlign(VerticalAlignment.Center);
    }

    private Element RenderSpellCard(OpggBuildData build)
    {
        if (build.SummonerSpells.Count == 0) return Empty();

        return Border(
            Grid(
                [GridSize.Star(), GridSize.Px(1), GridSize.Star()],
                [GridSize.Auto],
                [
                    RenderSpellPair(build.SummonerSpells[0]).Grid(0, 0),

                    Border(Empty())
                        .Width(1)
                        .Background(Theme.CardStroke)
                        .Margin(new Thickness(12, 0, 12, 0))
                        .Grid(0, 1),

                    build.SummonerSpells.Count > 1
                        ? RenderSpellPair(build.SummonerSpells[1]).Grid(0, 2)
                        : Empty().Grid(0, 2)
                ]
            )
        )
        .Margin(new Thickness(12, 6, 12, 6))
        .Padding(new Thickness(16, 8, 16, 8))
        .CornerRadius(YuumiTheme.OverlayRadius)
        .Background(Theme.CardBackground)
        .WithBorder(Theme.CardStroke, 1);
    }

    private Element RenderSpellPair(OpggIdWinRate s)
    {
        return Grid(
            [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                // 双技能图标
                HStack(4,
                    s.Ids.Take(2).Select(id =>
                    {
                        var iconPath = Props.SpellsMap.TryGetValue(id, out var path) && !string.IsNullOrWhiteSpace(path)
                            ? path
                            : $"/lol-game-data/assets/v1/summoner-spells/{id}.png";

                        return Component<LcuImage, LcuImageProps>(
                            new LcuImageProps(iconPath, 28, CornerRadius: 5));
                    }).ToArray()
                )
                .VAlign(VerticalAlignment.Center)
                .Grid(0, 0),

                // 中间胜率与场次
                VStack(1,
                    Caption(OpggVisuals.FormatRatio(s.Win, s.Play))
                        .SemiBold(),
                    Caption($"{s.Play} 场")
                        .Foreground(Theme.SecondaryText)
                )
                .Margin(new Thickness(8, 0, 0, 0))
                .VAlign(VerticalAlignment.Center)
                .Grid(0, 1),

                // 右侧登场率
                Caption(OpggVisuals.FormatPercent(s.PickRate))
                    .Foreground(Theme.SecondaryText)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(0, 2)
            ]
        );
    }

    private Element RenderCounterCard(OpggBuildData build)
    {
        if (build.Counters is null || build.Counters.Count == 0) return Empty();

        var strongCounters = build.Counters
            .Where(c => c.Rate >= 0.5)
            .OrderByDescending(c => c.Rate)
            .Take(5)
            .ToList();

        var weakCounters = build.Counters
            .Where(c => c.Rate < 0.5)
            .OrderBy(c => c.Rate)
            .Take(5)
            .ToList();

        return Border(
            Grid(
                [GridSize.Star(), GridSize.Px(1), GridSize.Star()],
                [GridSize.Auto],
                [
                    // 左列：克制
                    VStack(6,
                        Caption("对抗优势 (克制)")
                            .Foreground(OpggVisuals.WinGreen),

                        VStack(4,
                            strongCounters.Select(c => RenderCounterRow(c, isAdvantage: true).WithKey($"strong-{c.ChampionId}")).ToArray()
                        )
                    )
                    .Grid(0, 0),

                    // 中间分割线
                    Border(Empty())
                        .Width(1)
                        .Background(Theme.CardStroke)
                        .Margin(new Thickness(12, 0, 12, 0))
                        .Grid(0, 1),

                    // 右列：被克制
                    VStack(6,
                        Caption("对抗劣势 (被克制)")
                            .Foreground(OpggVisuals.LossRed),

                        VStack(4,
                            weakCounters.Select(c => RenderCounterRow(c, isAdvantage: false).WithKey($"weak-{c.ChampionId}")).ToArray()
                        )
                    )
                    .Grid(0, 2)
                ]
            )
        )
        .Margin(new Thickness(12, 6, 12, 6))
        .Padding(new Thickness(16, 12, 16, 12))
        .CornerRadius(YuumiTheme.OverlayRadius)
        .Background(Theme.CardBackground)
        .WithBorder(Theme.CardStroke, 1);
    }

    private Element RenderCounterRow(OpggCounter c, bool isAdvantage)
    {
        var name = Props.ChampionsMap.TryGetValue(c.ChampionId, out var champName) && !string.IsNullOrWhiteSpace(champName)
            ? champName
            : (c.Name ?? $"Hero {c.ChampionId}");

        var rateColor = isAdvantage ? OpggVisuals.WinGreen : OpggVisuals.LossRed;

        return Grid(
            [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                Component<LcuImage, LcuImageProps>(
                    new LcuImageProps($"/lol-game-data/assets/v1/champion-icons/{c.ChampionId}.png", 22, CornerRadius: 11))
                    .VAlign(VerticalAlignment.Center)
                    .Grid(0, 0),

                Caption(name)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .Margin(new Thickness(6, 0, 0, 0))
                    .VAlign(VerticalAlignment.Center)
                    .Grid(0, 1),

                HStack(8,
                    Caption($"{c.Play} 场")
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center),
                    Caption(OpggVisuals.FormatRatio(c.Win, c.Play))
                        .SemiBold()
                        .Foreground(rateColor)
                        .VAlign(VerticalAlignment.Center)
                )
                .Grid(0, 2)
            ]
        )
        .Padding(new Thickness(0, 2, 0, 2));
    }
}
