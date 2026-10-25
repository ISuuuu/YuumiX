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
        // 根布局必须是 Grid 而非 VStack：StackPanel 会以无限高度测量子级，
        // ScrollViewer 拿不到有界高度就永远不滚动，内容直接溢出窗口被裁掉。
        // 注意 Reactor 的 Grid 工厂第一参数是**列**、第二参数才是行。
        return Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Star()],
            [
                RenderHeader().Grid(0, 0),
                ScrollViewer(
                    VStack(3,
                        Props.Items.Select((item, index) => RenderRow(item, index).WithKey($"tier-row-{item.Id}")).ToArray()
                    )
                    .Padding(new Thickness(12, 6, 12, 16))
                )
                .Grid(1, 0)
            ]
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
                    Caption("#")
                        .SemiBold()
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .Grid(0, 0),

                    Caption("英雄")
                        .Foreground(Theme.SecondaryText)
                        .Margin(new Thickness(8, 0, 0, 0))
                        .Grid(0, 1),

                    Caption("Tier")
                        .SemiBold()
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .Grid(0, 2),

                    Caption("胜率")
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .Grid(0, 3),

                    Caption("登场率")
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .Grid(0, 4),

                    Caption("禁用率")
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .Grid(0, 5),

                    Caption("KDA")
                        .SemiBold()
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .Grid(0, 6),

                    Caption("劣势对抗")
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
                    Caption((index + 1).ToString())
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(0, 0),

                    // 2. 英雄头像与名称
                    HStack(8,
                        Component<LcuImage, LcuImageProps>(
                            new LcuImageProps($"/lol-game-data/assets/v1/champion-icons/{item.Id}.png", 34, CornerRadius: 17)),
                        BodyStrong(champName)
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                            .VAlign(VerticalAlignment.Center)
                    )
                    .VAlign(VerticalAlignment.Center)
                    .Grid(0, 1),

                    // 3. Tier 盾牌徽章
                    RenderTierBadge(item.Tier).Grid(0, 2),

                    // 4. 胜率
                    BodyStrong(OpggVisuals.FormatPercent(item.WinRate))
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(0, 3),

                    // 5. 登场率
                    Caption(OpggVisuals.FormatPercent(item.PickRate))
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(0, 4),

                    // 6. 禁用率
                    Caption(OpggVisuals.FormatPercent(item.BanRate))
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(0, 5),

                    // 7. KDA
                    Caption(OpggVisuals.FormatKda(item.Kda))
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
            Caption(label)
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
