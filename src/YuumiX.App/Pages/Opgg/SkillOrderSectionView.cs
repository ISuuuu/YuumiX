using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Opgg.Models;

namespace YuumiX.App.Pages.Opgg;

public sealed class SkillOrderSectionProps
{
    public IReadOnlyList<OpggSkillOrder>? Skills { get; init; }
    public IReadOnlyList<OpggSkillMastery>? SkillMasteries { get; init; }
}

/// <summary>
/// 技能加点推荐卡片：主升技能方框 + 逐级彩色加点序列 + 胜率/场次/登场率。
/// </summary>
public sealed class SkillOrderSectionView : Component<SkillOrderSectionProps>
{
    public override Element Render()
    {
        var hasSkills = Props.Skills is { Count: > 0 };
        var hasMasteries = Props.SkillMasteries is { Count: > 0 };
        if (!hasSkills && !hasMasteries) return Empty();

        var skillStats = hasSkills ? Props.Skills![0] : null;
        var mastery = hasMasteries ? Props.SkillMasteries![0] : null;

        return Border(
            Grid(
                [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [
                    // 1. 主升技能顺序 (Q › W › E)
                    RenderMainMasteries(mastery).Grid(0, 0),

                    // 2. 逐级加点序列 (1~18级方框)
                    RenderLevelOrder(skillStats).Grid(0, 1),

                    // 3. 胜率与场次统计
                    RenderStats(skillStats).Grid(0, 2)
                ]
            )
        )
        .Margin(new Thickness(12, 6, 12, 6))
        .Padding(new Thickness(16, 8, 16, 8))
        .CornerRadius(YuumiTheme.OverlayRadius)
        .Background(Theme.CardBackground)
        .WithBorder(Theme.CardStroke, 1);
    }

    private static Element RenderMainMasteries(OpggSkillMastery? mastery)
    {
        if (mastery is null || mastery.Ids.Count == 0) return Empty();

        var items = new List<Element>();
        for (int i = 0; i < mastery.Ids.Count; i++)
        {
            var key = mastery.Ids[i];
            var (textCol, bgCol) = OpggVisuals.GetSkillKeyColor(key);

            items.Add(
                Border(
                    BodyStrong(key)
                        .Foreground(textCol)
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center)
                )
                .Width(26)
                .Height(26)
                .CornerRadius(YuumiTheme.ControlRadius)
                .Background(bgCol)
            );

            if (i < mastery.Ids.Count - 1)
            {
                items.Add(
                    Body("›")
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)
                );
            }
        }

        return HStack(4, [.. items])
            .VAlign(VerticalAlignment.Center)
            .Margin(new Thickness(0, 0, 12, 0));
    }

    private static Element RenderLevelOrder(OpggSkillOrder? skillStats)
    {
        if (skillStats is null || skillStats.Order.Count == 0) return Empty();

        var boxes = skillStats.Order.Select((key, idx) =>
        {
            var (textCol, bgCol) = OpggVisuals.GetSkillKeyColor(key);

            return Border(
                Caption(key)
                    .Foreground(textCol)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
            )
            .Width(18)
            .Height(18)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Background(bgCol)
            .WithToolTip(Caption($"Lv.{idx + 1}: {key}"));
        });

        return HStack(2, boxes.ToArray())
            .VAlign(VerticalAlignment.Center);
    }

    private static Element RenderStats(OpggSkillOrder? skillStats)
    {
        if (skillStats is null) return Empty();

        return HStack(12,
            VStack(1,
                Caption(OpggVisuals.FormatRatio(skillStats.Win, skillStats.Play))
                    .SemiBold()
                    .HAlign(HorizontalAlignment.Right),
                Caption($"{skillStats.Play} 场")
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Right)
            )
            .VAlign(VerticalAlignment.Center),

            Caption(OpggVisuals.FormatPercent(skillStats.PickRate))
                .Foreground(Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center)
        )
        .VAlign(VerticalAlignment.Center)
        .Margin(new Thickness(12, 0, 0, 0));
    }
}
