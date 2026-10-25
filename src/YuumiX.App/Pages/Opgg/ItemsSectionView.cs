using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Opgg.Models;

namespace YuumiX.App.Pages.Opgg;

public sealed class ItemsSectionProps
{
    public IReadOnlyList<OpggIdWinRate>? StarterItems { get; init; }
    public IReadOnlyList<OpggIdWinRate>? CoreItems { get; init; }
    public IReadOnlyList<OpggIdWinRate>? Boots { get; init; }
    public IReadOnlyList<OpggIdWinRate>? LastItems { get; init; }
    public required IReadOnlyDictionary<int, string> ItemsMap { get; init; }
    public required Action OnApplyItemSet { get; init; }
    public bool IsApplying { get; init; }
}

/// <summary>
/// 出装推荐卡片：一键应用操作栏 + 初始装备与鞋子 + 核心出装链 + 可选装备池。
/// </summary>
public sealed class ItemsSectionView : Component<ItemsSectionProps>
{
    public override Element Render()
    {
        var hasStarter = Props.StarterItems is { Count: > 0 };
        var hasCore = Props.CoreItems is { Count: > 0 };
        var hasBoots = Props.Boots is { Count: > 0 };
        var hasLast = Props.LastItems is { Count: > 0 };

        if (!hasStarter && !hasCore && !hasBoots && !hasLast) return Empty();

        return VStack(8,
            // 顶部全宽一键导入按钮
            RenderApplyBar(),

            // 主出装推荐卡片
            Border(
                VStack(10,
                    // 1. 初始装备与鞋子（并排）
                    RenderStarterAndBoots(hasStarter, hasBoots),

                    // 2. 核心装备组合链
                    hasCore ? RenderCoreItems() : Empty(),

                    // 3. 可选与推荐装备
                    hasLast ? RenderLastItems() : Empty()
                )
            )
            .Padding(new Thickness(16, 12, 16, 12))
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
        )
        .Margin(new Thickness(12, 6, 12, 6));
    }

    private Element RenderApplyBar()
    {
        // 与符文区的「设为当前符文页」按钮同款观感：实心绿底白字；
        // 原先的 12% 半透明绿底叠在卡片上会发灰，描边包裹层也显得多余。
        return Button(
            BodyStrong(Props.IsApplying ? "正在应用出装..." : "一键应用出装到游戏推荐页")
                .Foreground("#FFFFFF"),
            Props.OnApplyItemSet)
            .IsEnabled(!Props.IsApplying)
            .Background(OpggVisuals.ActiveTabGreen)
            .Height(36)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationName("一键应用出装按钮");
    }

    private Element RenderStarterAndBoots(bool hasStarter, bool hasBoots)
    {
        if (!hasStarter && !hasBoots) return Empty();

        return Grid(
            [GridSize.Star(), GridSize.Px(1), GridSize.Star()],
            [GridSize.Auto],
            [
                // 左侧初始装备
                VStack(6,
                    Caption("初始装备")
                        .Foreground(Theme.SecondaryText),
                    VStack(4,
                        (Props.StarterItems ?? []).Take(3).Select((item, idx) => RenderItemRow(item).WithKey($"starter-{idx}")).ToArray()
                    )
                )
                .Grid(0, 0),

                // 中间垂直分割线
                Border(Empty())
                    .Width(1)
                    .Background(Theme.CardStroke)
                    .Margin(new Thickness(12, 0, 12, 0))
                    .Grid(0, 1),

                // 右侧鞋子
                VStack(6,
                    Caption("鞋子选择")
                        .Foreground(Theme.SecondaryText),
                    VStack(4,
                        (Props.Boots ?? []).Take(3).Select((item, idx) => RenderItemRow(item).WithKey($"boots-{idx}")).ToArray()
                    )
                )
                .Grid(0, 2)
            ]
        );
    }

    private Element RenderCoreItems()
    {
        return VStack(8,
            Border(Empty()).Height(1).Background(Theme.CardStroke),

            Caption("核心装备组合")
                .Foreground(Theme.SecondaryText),

            VStack(6,
                (Props.CoreItems ?? []).Take(5).Select((item, idx) =>
                    Grid(
                        [GridSize.Star(), GridSize.Auto],
                        [GridSize.Auto],
                        [
                            // 装备链
                            HStack(4,
                                RenderChainIcons(item.Ids)
                            )
                            .VAlign(VerticalAlignment.Center)
                            .Grid(0, 0),

                            // 统计
                            HStack(12,
                                VStack(1,
                                    Caption(OpggVisuals.FormatRatio(item.Win, item.Play))
                                        .SemiBold()
                                        .HAlign(HorizontalAlignment.Right),
                                    Caption($"{item.Play} 场")
                                        .Foreground(Theme.SecondaryText)
                                        .HAlign(HorizontalAlignment.Right)
                                )
                                .VAlign(VerticalAlignment.Center),

                                Caption(OpggVisuals.FormatPercent(item.PickRate))
                                    .Foreground(Theme.SecondaryText)
                                    .VAlign(VerticalAlignment.Center)
                            )
                            .VAlign(VerticalAlignment.Center)
                            .Grid(0, 1)
                        ]
                    )
                    .WithKey($"core-chain-{idx}")
                ).ToArray()
            )
        );
    }

    private Element RenderLastItems()
    {
        return VStack(8,
            Border(Empty()).Height(1).Background(Theme.CardStroke),

            Caption("可选与推荐装备")
                .Foreground(Theme.SecondaryText),

            HStack(6,
                (Props.LastItems ?? []).Take(10).Select((item, idx) =>
                {
                    int id = item.Ids.FirstOrDefault();
                    return RenderItemIcon(id, 28).WithKey($"last-{idx}-{id}");
                }).ToArray()
            )
        );
    }

    private Element RenderItemRow(OpggIdWinRate item)
    {
        return Grid(
            [GridSize.Auto, GridSize.Star()],
            [GridSize.Auto],
            [
                HStack(3,
                    item.Ids.Select((id, idx) => RenderItemIcon(id, 24).WithKey($"subitem-{idx}-{id}")).ToArray()
                )
                .VAlign(VerticalAlignment.Center)
                .Grid(0, 0),

                HStack(8,
                    Caption(OpggVisuals.FormatRatio(item.Win, item.Play))
                        .SemiBold()
                        .VAlign(VerticalAlignment.Center),
                    Caption($"{item.Play} 场")
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)
                )
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Center)
                .Grid(0, 1)
            ]
        );
    }

    private Element[] RenderChainIcons(IReadOnlyList<int> ids)
    {
        var elements = new List<Element>();
        for (int i = 0; i < ids.Count; i++)
        {
            elements.Add(RenderItemIcon(ids[i], 28));
            if (i < ids.Count - 1)
            {
                elements.Add(
                    Body("›")
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)
                );
            }
        }
        return elements.ToArray();
    }

    private Element RenderItemIcon(int id, double size)
    {
        var path = Props.ItemsMap.TryGetValue(id, out var iconPath) && !string.IsNullOrWhiteSpace(iconPath)
            ? iconPath
            : $"/lol-game-data/assets/v1/items/{id}.png";

        return Component<LcuImage, LcuImageProps>(new LcuImageProps(path, size, CornerRadius: 4))
            .VAlign(VerticalAlignment.Center);
    }
}
