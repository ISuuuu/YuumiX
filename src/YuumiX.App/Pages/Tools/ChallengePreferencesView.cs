using YuumiX.App.Controls;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Tools.Models;

namespace YuumiX.App.Pages.Tools;

/// <summary>
/// 挑战勋章与称号装配视图（对应原 BadgesPreferencesCard.vue）。
/// </summary>
internal static class ChallengePreferencesView
{
    private static readonly string[] TierFilters =
    [
        "全部段位",
        "王者 / 宗师",
        "大师 / 钻石",
        "铂金 / 翡翠 / 黄金",
        "白银 / 黄铜 / 黑铁",
    ];

    public static Element Render(
        IReadOnlyList<ChallengeItem> allChallenges,
        bool isLoading,
        string searchQuery,
        Action<string> onSearchQueryChanged,
        int tierFilterIndex,
        Action<int> onTierFilterIndexChanged,
        string titleInput,
        Action<string> onTitleInputChanged,
        List<long> selectedChallengeIds,
        Action<long> onToggleBadge,
        Action onSavePreferences,
        Action onReloadChallenges,
        YuumiPalette palette)
    {
        var filtered = allChallenges.Where(c =>
        {
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                string q = searchQuery.Trim().ToLowerInvariant();
                bool matches = c.Name.ToLowerInvariant().Contains(q) || c.Description.ToLowerInvariant().Contains(q);
                if (!matches) return false;
            }

            string lvl = (c.CurrentLevel ?? "").ToUpperInvariant();
            if (lvl is "NONE" or "") return false;

            return tierFilterIndex switch
            {
                1 => lvl is "CHALLENGER" or "GRANDMASTER",
                2 => lvl is "MASTER" or "DIAMOND",
                3 => lvl is "PLATINUM" or "EMERALD" or "GOLD",
                4 => lvl is "SILVER" or "BRONZE" or "IRON",
                _ => true,
            };
        }).ToList();

        Element controlBar = Grid(
            [GridSize.Star(), GridSize.Auto, GridSize.Auto],
            [GridSize.Auto],
            [
                TextBox(searchQuery, onSearchQueryChanged, "搜索挑战勋章名称或说明...")
                    .Margin(new Thickness(0, 0, 8, 0))
                    .AutomationName("勋章搜索框")
                    .Grid(row: 0, column: 0),
                PixelComboBox.Render(TierFilters, tierFilterIndex, onTierFilterIndexChanged, automationName: "段位筛选下拉", width: 160).Margin(new Thickness(0, 0, 8, 0))
                    .Grid(row: 0, column: 1),
                Button(isLoading ? "加载中..." : "重新加载", onReloadChallenges)
                    .IsEnabled(!isLoading)
                    .AutomationName("刷新勋章列表")
                    .Grid(row: 0, column: 2),
            ]
        ).Margin(new Thickness(0, 0, 0, 8));

        Element titleRow = HStack(8,
            Caption("称号 ID:")
                .Foreground(Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center),
            TextBox(titleInput, onTitleInputChanged, "输入称号 ID（如留空则卸下称号）")
                .Width(220)
                .AutomationName("称号ID输入框"),
            Button("保存装配", onSavePreferences)
                .Background(Theme.Accent)
                .Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush"))
                .AutomationName("保存装配按钮"),
            Caption($"已选勋章: {selectedChallengeIds.Count}/3")
                .Foreground(Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center)
                .Margin(new Thickness(12, 0, 0, 0))
        ).Margin(new Thickness(0, 0, 0, 8));

        Element badgeList;
        if (filtered.Count == 0)
        {
            badgeList = Border(
                Caption(allChallenges.Count == 0 ? "尚未获取勋章列表，请点击上方「重新加载」" : "没有符合条件的挑战勋章")
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center)
                    .Margin(new Thickness(0, 20, 0, 20))
            );
        }
        else
        {
            var badgeItems = filtered.Select(b =>
            {
                bool isSelected = selectedChallengeIds.Contains(b.Id);
                var checkText = Caption(isSelected ? "☑" : "☐")
                    .Margin(new Thickness(0, 0, 8, 0))
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 0);

                if (isSelected)
                {
                    checkText = checkText.Foreground(Theme.Accent);
                }
                else
                {
                    checkText = checkText.Foreground(Theme.SecondaryText);
                }

                return Button(
                    Grid(
                        [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                        [GridSize.Auto],
                        [
                            checkText,
                            VStack(2,
                                Caption(b.Name),
                                Caption(b.Description)
                                    .Foreground(Theme.SecondaryText)
                            ).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 1),
                            Border(
                                Caption(b.CurrentLevel)
                                    .Foreground(Theme.Accent)
                            )
                            .Padding(6, 2)
                            .CornerRadius(YuumiTheme.ControlRadius)
                            .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
                            .VAlign(VerticalAlignment.Center)
                            .Grid(row: 0, column: 2),
                        ]
                    ),
                    () => onToggleBadge(b.Id)
                )
                .Background(isSelected ? Theme.Ref("LayerFillColorDefaultBrush") : Theme.Ref("SubtleFillColorTransparentBrush"))
                .BorderThickness(isSelected ? 1 : 0)
                .BorderBrush(Theme.Accent)
                .CornerRadius(YuumiTheme.ControlRadius)
                .Padding(8, 4)
                .HAlign(HorizontalAlignment.Stretch)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .AutomationName($"选择勋章_{b.Name}");
            }).ToArray();

            badgeList = Border(
                ScrollViewer(
                    VStack(4, badgeItems)
                ).Height(240)
            )
            .Padding(4)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.Ref("LayerFillColorDefaultBrush"))
            .WithBorder(Theme.CardStroke, 1);
        }

        return VStack(0,
            controlBar,
            titleRow,
            badgeList);
    }
}
