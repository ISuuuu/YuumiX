using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Opgg.Models;

namespace YuumiX.App.Pages.Opgg;

public sealed class TierListPanelProps
{
    public required IReadOnlyList<TierListItem> Items { get; init; }
    public required IReadOnlyDictionary<int, string> ChampionsMap { get; init; }
    public required bool IsDark { get; init; }
    public required Action<int> OnSelectChampion { get; init; }
}

/// <summary>
/// 梯队排行榜视图（表头、Tier 染色卡片行、T1~T4 徽标、Counter 头像组）。
/// </summary>
public sealed class TierListPanelView : Component<TierListPanelProps>
{
    public override Element Render()
    {
        return VStack(0,
            RenderHeader(),
            ScrollView(
                VStack(3,
                    Props.Items.Select((item, index) => RenderRow(item, index).WithKey($"tier-row-{item.Id}")).ToArray()
                )
                .Padding(new Thickness(12, 6, 12, 16))
            )
        );
    }

    private static Element RenderHeader() =>
        Border(
            Grid(
                [
                    GridSize.Px(34),   // #
                    GridSize.Star(),   // 英雄
                    GridSize.Px(54),   // Tier
                    GridSize.Px(68),   // 胜率
                    GridSize.Px(68),   // 登场率
                    GridSize.Px(68),   // 禁用率
                    GridSize.Px(64),   // KDA
                    GridSize.Px(96)    // 劣势对抗
                ],
                [GridSize.Auto],
                [
                    TextBlock("#")
                        .FontSize(11)
                        .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .Grid(0, 0),

                    TextBlock("英雄")
                        .FontSize(11)
                        .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                        .Foreground(Theme.SecondaryText)
                        .Margin(new Thickness(8, 0, 0, 0))
                        .Grid(0, 1),

                    TextBlock("Tier")
                        .FontSize(11)
                        .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .Grid(0, 2),

                    TextBlock("胜率")
                        .FontSize(11)
                        .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .Grid(0, 3),

                    TextBlock("登场率")
                        .FontSize(11)
                        .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .Grid(0, 4),

                    TextBlock("禁用率")
                        .FontSize(11)
                        .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .Grid(0, 5),

                    TextBlock("KDA")
                        .FontSize(11)
                        .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .Grid(0, 6),

                    TextBlock("劣势对抗")
                        .FontSize(11)
                        .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .Grid(0, 7)
                ]
            )
        )
        .Padding(new Thickness(16, 8, 16, 8))
        .BorderThickness(new Thickness(0, 0, 0, 1))
        .WithBorder(Theme.CardStroke, 1);

    private Element RenderRow(TierListItem item, int index)
    {
        var champName = Props.ChampionsMap.TryGetValue(item.Id, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : item.Name;

        var bg = OpggVisuals.GetTierCardBackground(item.Tier, Props.IsDark);
        var border = OpggVisuals.GetTierCardBorder(item.Tier, Props.IsDark);

        return Border(
            Grid(
                [
                    GridSize.Px(34),   // #
                    GridSize.Star(),   // 英雄
                    GridSize.Px(54),   // Tier
                    GridSize.Px(68),   // 胜率
                    GridSize.Px(68),   // 登场率
                    GridSize.Px(68),   // 禁用率
                    GridSize.Px(64),   // KDA
                    GridSize.Px(96)    // 劣势对抗
                ],
                [GridSize.Auto],
                [
                    // 1. 排名序号
                    TextBlock((index + 1).ToString())
                        .FontSize(12)
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(0, 0),

                    // 2. 英雄头像与名称
                    HStack(8,
                        Component<LcuImage, LcuImageProps>(
                            new LcuImageProps($"/lol-game-data/assets/v1/champion-icons/{item.Id}.png", 34, CornerRadius: 17)),
                        TextBlock(champName)
                            .FontSize(13)
                            .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                            .VAlign(VerticalAlignment.Center)
                    )
                    .VAlign(VerticalAlignment.Center)
                    .Grid(0, 1),

                    // 3. Tier 盾牌徽章
                    RenderTierBadge(item.Tier).Grid(0, 2),

                    // 4. 胜率
                    TextBlock(OpggVisuals.FormatPercent(item.WinRate))
                        .FontSize(12.5)
                        .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(0, 3),

                    // 5. 登场率
                    TextBlock(OpggVisuals.FormatPercent(item.PickRate))
                        .FontSize(12)
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(0, 4),

                    // 6. 禁用率
                    TextBlock(OpggVisuals.FormatPercent(item.BanRate))
                        .FontSize(12)
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(0, 5),

                    // 7. KDA
                    TextBlock(OpggVisuals.FormatKda(item.Kda))
                        .FontSize(12)
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(0, 6),

                    // 8. 劣势对抗 (Counters)
                    HStack(3,
                        item.Counters.Take(3).Select(cid =>
                            Component<LcuImage, LcuImageProps>(
                                new LcuImageProps($"/lol-game-data/assets/v1/champion-icons/{cid}.png", 22, CornerRadius: 11))
                                .WithKey($"counter-{cid}")
                        ).ToArray()
                    )
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(0, 7)
                ]
            )
        )
        .Padding(new Thickness(12, 6, 12, 6))
        .CornerRadius(6)
        .Background(bg)
        .WithBorder(border, 1)
        .OnPointerPressed((_, _) => Props.OnSelectChampion(item.Id))
        .WithKey($"tier-row-{item.Id}");
    }

    private static Element RenderTierBadge(string? tier)
    {
        var t = (tier ?? "").Trim();
        if (AppAssets.TierIconUris.TryGetValue(t, out var uri) && !string.IsNullOrWhiteSpace(uri))
        {
            return Image(uri)
                .AutomationName("Tier徽标")
                .Width(22)
                .Height(22)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center);
        }

        var badgeColor = OpggVisuals.GetTierBadgeColor(tier);
        var label = string.IsNullOrWhiteSpace(t) ? "-" : (t == "0" ? "1" : t);

        return Border(
            TextBlock(label)
                .FontSize(11)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .Foreground("#FFFFFF")
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
        )
        .Width(24)
        .Height(20)
        .CornerRadius(4)
        .Background(badgeColor)
        .HAlign(HorizontalAlignment.Center)
        .VAlign(VerticalAlignment.Center);
    }
}
