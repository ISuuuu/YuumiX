using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Opgg.Models;

namespace YuumiX.App.Pages.Opgg;

public sealed class RunesSectionProps
{
    public required IReadOnlyList<OpggRunePreset> Runes { get; init; }
    public required IReadOnlyList<PerkStyle> PerkStyles { get; init; }
    public required IReadOnlyDictionary<int, PerkDetail> PerkDetails { get; init; }
    public required IReadOnlyDictionary<int, string> RunesMap { get; init; }
    public required Action<OpggRunePreset> OnApplyRune { get; init; }
}

/// <summary>
/// 符文推荐卡片：双符文树（主副系） + 属性碎片 + 3 套预设切换 + 一键应用按钮。
/// </summary>
public sealed class RunesSectionView : Component<RunesSectionProps>
{
    public override Element Render()
    {
        if (Props.Runes.Count == 0) return Empty();

        var (selectedIdx, setSelectedIdx) = UseState(0);
        if (selectedIdx >= Props.Runes.Count)
        {
            selectedIdx = 0;
        }

        var activeRune = Props.Runes[selectedIdx];
        var primaryStyle = Props.PerkStyles.FirstOrDefault(s => s.Id == activeRune.PrimaryPageId);
        var secondaryStyle = Props.PerkStyles.FirstOrDefault(s => s.Id == activeRune.SecondaryPageId);

        if (primaryStyle is null || secondaryStyle is null)
        {
            return Empty();
        }

        return Border(
            Grid(
                [GridSize.Star(), GridSize.Px(180)],
                [GridSize.Auto],
                [
                    // 左侧：三列符文树
                    RenderRuneTree(primaryStyle, secondaryStyle, activeRune).Grid(0, 0),

                    // 右侧：预设切换与一键应用
                    RenderPresetsPane(activeRune, selectedIdx, setSelectedIdx).Grid(0, 1)
                ]
            )
        )
        .Margin(new Thickness(12, 6, 12, 6))
        .Padding(new Thickness(16, 12, 16, 12))
        .CornerRadius(8)
        .Background(Theme.CardBackground)
        .WithBorder(Theme.CardStroke, 1);
    }

    private Element RenderRuneTree(PerkStyle primaryStyle, PerkStyle secondaryStyle, OpggRunePreset activeRune)
    {
        return Grid(
            [GridSize.Star(), GridSize.Star(), GridSize.Star()],
            [GridSize.Auto],
            [
                // 1. 主系列 (前4层插槽)
                VStack(8,
                    // 流派头
                    HStack(6,
                        Component<LcuImage, LcuImageProps>(new LcuImageProps(primaryStyle.IconPath, 24)),
                        Caption(primaryStyle.Name)
                            .VAlign(VerticalAlignment.Center)
                    ).HAlign(HorizontalAlignment.Center),

                    // 4 层插槽
                    VStack(6,
                        primaryStyle.Slots.Take(4).Select((slot, sIdx) =>
                            HStack(6,
                                slot.Perks.Select(perkId =>
                                    RenderPerkItem(perkId, activeRune.PrimaryRuneIds.Contains(perkId), isKeystone: sIdx == 0, isShard: false)
                                        .WithKey($"perk-{perkId}")
                                ).ToArray()
                            ).HAlign(HorizontalAlignment.Center).WithKey($"prim-slot-{sIdx}")
                        ).ToArray()
                    )
                )
                .HAlign(HorizontalAlignment.Center)
                .Grid(0, 0),

                // 2. 副系列 (跳过基石，取接下来 3 层插槽)
                VStack(8,
                    HStack(6,
                        Component<LcuImage, LcuImageProps>(new LcuImageProps(secondaryStyle.IconPath, 24)),
                        Caption(secondaryStyle.Name)
                            .VAlign(VerticalAlignment.Center)
                    ).HAlign(HorizontalAlignment.Center),

                    VStack(6,
                        secondaryStyle.Slots.Skip(1).Take(3).Select((slot, sIdx) =>
                            HStack(6,
                                slot.Perks.Select(perkId =>
                                    RenderPerkItem(perkId, activeRune.SecondaryRuneIds.Contains(perkId), isKeystone: false, isShard: false)
                                        .WithKey($"perk-{perkId}")
                                ).ToArray()
                            ).HAlign(HorizontalAlignment.Center).WithKey($"sec-slot-{sIdx}")
                        ).ToArray()
                    )
                )
                .HAlign(HorizontalAlignment.Center)
                .Grid(0, 1),

                // 3. 属性碎片列 (Slots 4~6)
                VStack(8,
                    // 顶部空白占位，使碎片行与副系底部对齐
                    Border(Empty()).Height(24),

                    VStack(8,
                        primaryStyle.Slots.Skip(4).Take(3).Select((slot, sIdx) =>
                            HStack(6,
                                slot.Perks.Select(perkId =>
                                    RenderPerkItem(perkId, activeRune.StatModIds.Contains(perkId), isKeystone: false, isShard: true)
                                        .WithKey($"perk-{perkId}")
                                ).ToArray()
                            ).HAlign(HorizontalAlignment.Center).WithKey($"shard-slot-{sIdx}")
                        ).ToArray()
                    )
                )
                .HAlign(HorizontalAlignment.Center)
                .Grid(0, 2)
            ]
        );
    }

    private Element RenderPerkItem(int perkId, bool isActive, bool isKeystone, bool isShard)
    {
        double size = isKeystone ? 38 : (isShard ? 20 : 28);
        double iconSize = isKeystone ? 34 : (isShard ? 18 : 24);
        double radius = size / 2;

        var iconPath = Props.RunesMap.TryGetValue(perkId, out var path) && !string.IsNullOrWhiteSpace(path)
            ? path
            : $"/lol-game-data/assets/v1/perks/{perkId}.png";

        Props.PerkDetails.TryGetValue(perkId, out var detail);
        var tooltipText = detail is not null
            ? (string.IsNullOrWhiteSpace(detail.CleanedDesc) ? detail.Name : $"{detail.Name}\n{detail.CleanedDesc}")
            : $"Perk {perkId}";

        var borderBrush = isActive
            ? (isShard ? OpggVisuals.GreenShardBorder : OpggVisuals.GoldRuneBorder)
            : "transparent";

        var bg = isActive
            ? (isShard ? "rgba(81, 207, 102, 0.15)" : "rgba(229, 169, 59, 0.15)")
            : "transparent";

        var item = Border(
            Component<LcuImage, LcuImageProps>(new LcuImageProps(iconPath, iconSize, CornerRadius: iconSize / 2))
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
        )
        .Width(size)
        .Height(size)
        .CornerRadius(radius)
        .Background(bg)
        .WithBorder(borderBrush, isActive ? (isKeystone ? 2 : 1.5) : 0)
        .Opacity(isActive ? 1.0 : (isShard ? 0.3 : 0.35))
        .WithToolTip(Caption(tooltipText).TextWrapping());

        return item;
    }

    private Element RenderPresetsPane(OpggRunePreset activeRune, int selectedIdx, Action<int> onSelect)
    {
        return Border(
            VStack(8,
                Caption("推荐符文配置")
                    .Foreground(Theme.SecondaryText)
                    .Margin(new Thickness(4, 0, 0, 4)),

                VStack(4,
                    Props.Runes.Take(3).Select((rune, idx) =>
                    {
                        bool isSelected = idx == selectedIdx;
                        var styleIconPath = Props.PerkStyles.FirstOrDefault(s => s.Id == rune.PrimaryPageId)?.IconPath
                            ?? $"/lol-game-data/assets/v1/perk-images/Styles/{rune.PrimaryPageId}.png";

                        // 选中行只做轻微提示：实心品牌蓝过于抢眼、白字对比也不足，
                        // 选中语义由主色描边承担，底色仅 10% 透明主色。
                        var ratioText = Caption(OpggVisuals.FormatRatio(rune.Win, rune.Play))
                            .SemiBold()
                            .Foreground(Theme.PrimaryText);

                        var playCountText = Caption($"{rune.Play} 场")
                            .Foreground(Theme.SecondaryText);

                        var pickRateText = Caption(OpggVisuals.FormatPercent(rune.PickRate))
                            .Foreground(Theme.SecondaryText)
                            .VAlign(VerticalAlignment.Center)
                            .Grid(0, 2);

                        var itemBorder = Border(
                            Grid(
                                [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                                [GridSize.Auto],
                                [
                                    Component<LcuImage, LcuImageProps>(new LcuImageProps(styleIconPath, 22))
                                        .VAlign(VerticalAlignment.Center)
                                        .Grid(0, 0),

                                    VStack(1, ratioText, playCountText)
                                        .Margin(new Thickness(6, 0, 0, 0))
                                        .VAlign(VerticalAlignment.Center)
                                        .Grid(0, 1),

                                    pickRateText
                                ]
                            )
                        )
                        .Padding(new Thickness(8, 6, 8, 6))
                        .CornerRadius(6)
                        .OnPointerPressed((_, _) => onSelect(idx))
                        .WithKey($"preset-{idx}");

                        return isSelected
                            ? itemBorder.Background("rgba(99, 111, 249, 0.10)").WithBorder(OpggVisuals.OpggBrandColor, 1)
                            : itemBorder.Background("transparent").WithBorder(Theme.CardStroke, 1);
                    }).ToArray()
                ),

                Border(Empty()).Height(6),

                Button(
                    Caption("设为当前符文页")
                        .Foreground("#FFFFFF")
                        ,
                    () => Props.OnApplyRune(activeRune))
                    .Background(OpggVisuals.ActiveTabGreen)
                    .Height(32)
                    .HAlign(HorizontalAlignment.Stretch)
                    .AutomationName("一键应用符文页按钮")
            )
        )
        .Padding(new Thickness(12, 0, 0, 0))
        .BorderThickness(new Thickness(1, 0, 0, 0))
        .WithBorder(Theme.CardStroke, 1);
    }
}
