using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.Core.Features.Opgg.Models;

namespace YuumiX.App.Pages.Opgg;

public sealed class OpggFilterBarProps
{
    public required string Region { get; init; }
    public required string Mode { get; init; }
    public required string Tier { get; init; }
    public required string Position { get; init; }

    public required Action<string> OnRegionChanged { get; init; }
    public required Action<string> OnModeChanged { get; init; }
    public required Action<string> OnTierChanged { get; init; }
    public required Action<string> OnPositionChanged { get; init; }
}

/// <summary>
/// 四维筛选栏组件（大区 / 模式 / 段位 / 分路[仅排位显示]）。
/// </summary>
public sealed class OpggFilterBarView : Component<OpggFilterBarProps>
{
    public override Element Render()
    {
        var i18n = Composition.AppHost.CurrentI18n;
        var regions = OpggRegion.All;
        var modes = OpggMode.All;
        var tiers = OpggTier.All;
        var positions = OpggPosition.All;

        var regionLabels = regions.Select(r => i18n?.T($"opgg.regions.{r.Value}", r.Label) ?? r.Label).ToArray();
        var modeLabels = modes.Select(m => i18n?.T($"opgg.modes.{m.Value}", m.Label) ?? m.Label).ToArray();
        var tierLabels = tiers.Select(t => i18n?.T($"opgg.tiers.{t.Value}", t.Label) ?? t.Label).ToArray();
        var positionLabels = positions.Select(p => i18n?.T($"opgg.positions.{p.Value}", p.Label) ?? p.Label).ToArray();

        int regionIndex = FindOptionIndex(regions, Props.Region);
        int modeIndex = FindOptionIndex(modes, Props.Mode);
        int tierIndex = FindOptionIndex(tiers, Props.Tier);
        int positionIndex = FindOptionIndex(positions, Props.Position);

        var items = new List<Element>
        {
            // 1. 大区
            ComboBox(regionLabels, regionIndex, idx =>
            {
                if (idx >= 0 && idx < regions.Count && regions[idx].Value != Props.Region)
                {
                    Props.OnRegionChanged(regions[idx].Value);
                }
            })
            .Width(100)
            .AutomationName("OPGG大区选择"),

            // 2. 模式
            ComboBox(modeLabels, modeIndex, idx =>
            {
                if (idx >= 0 && idx < modes.Count && modes[idx].Value != Props.Mode)
                {
                    Props.OnModeChanged(modes[idx].Value);
                }
            })
            .Width(126)
            .AutomationName("OPGG模式选择"),

            // 3. 段位
            ComboBox(tierLabels, tierIndex, idx =>
            {
                if (idx >= 0 && idx < tiers.Count && tiers[idx].Value != Props.Tier)
                {
                    Props.OnTierChanged(tiers[idx].Value);
                }
            })
            .Width(116)
            .AutomationName("OPGG段位选择")
        };

        // 4. 分路（仅排位模式显示）
        if (string.Equals(Props.Mode, OpggMode.Ranked, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(
                ComboBox(positionLabels, positionIndex, idx =>
                {
                    if (idx >= 0 && idx < positions.Count && positions[idx].Value != Props.Position)
                    {
                        Props.OnPositionChanged(positions[idx].Value);
                    }
                })
                .Width(116)
                .AutomationName("OPGG分路选择"));
        }

        return Border(
            HStack(6, [.. items])
                .VAlign(VerticalAlignment.Center)
        )
        .Padding(new Thickness(16, 6, 16, 6))
        .BorderThickness(new Thickness(0, 0, 0, 1))
        .WithBorder(Theme.CardStroke, 1);
    }

    private static int FindOptionIndex(IReadOnlyList<FilterOption> list, string val)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Value, val, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return 0;
    }
}
