using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Tools.Models;

namespace YuumiX.App.Pages.Tools;

/// <summary>
/// 英雄选择器视图（用于自动选用与自动禁用英雄配置，对应原 ChampionPicker.vue）。
/// </summary>
internal static class ChampionPickerView
{
    public static Element Render(
        IReadOnlyList<ChampionSummaryItem> allChampions,
        IReadOnlyList<int> selectedIds,
        Action<int> onToggleChampion,
        Action onClearSelection,
        string searchQuery,
        Action<string> onSearchQueryChanged,
        bool isLoading,
        YuumiPalette palette,
        int maxCount = 5)
    {
        var championMap = allChampions.ToDictionary(c => c.Id);

        // 已选英雄预览行
        Element selectedSection;
        if (selectedIds.Count == 0)
        {
            selectedSection = Border(
                TextBlock("未选择任何英雄（按下方列表点击添加；将按添加顺序优先选用）")
                    .FontSize(12)
                    .Foreground(Theme.SecondaryText)
            ).Margin(new Thickness(0, 4, 0, 8));
        }
        else
        {
            var selectedChips = new List<Element>();
            for (int i = 0; i < selectedIds.Count; i++)
            {
                int id = selectedIds[i];
                int order = i + 1;
                string name = championMap.TryGetValue(id, out var champ) ? champ.Name : $"ID:{id}";
                string iconPath = championMap.TryGetValue(id, out var c2) ? c2.IconPath : $"/lol-game-data/assets/v1/champion-icons/{id}.png";

                selectedChips.Add(
                    Border(
                        HStack(6,
                            Caption($"#{order}").Foreground(palette.Accent).VAlign(VerticalAlignment.Center),
                            Component<LcuImage, LcuImageProps>(new LcuImageProps(iconPath, Size: 24, CornerRadius: 4)).VAlign(VerticalAlignment.Center),
                            Caption(name).VAlign(VerticalAlignment.Center),
                            Button(Caption("\uE711"), () => onToggleChampion(id))
                                .Padding(4, 2)
                                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                                .Foreground(Theme.SecondaryText)
                                .AutomationName($"移除_{name}")
                        )
                    )
                    .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
                    .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
                    .BorderThickness(1)
                    .CornerRadius(6)
                    .Padding(8, 4)
                );
            }

            selectedSection = VStack(4,
                HStack(8,
                    TextBlock($"已选英雄 ({selectedIds.Count}/{maxCount}):")
                        .FontSize(12)
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center),
                    Button(Caption("清空全部"), onClearSelection)
                        .Padding(6, 2)
                        .Foreground(Theme.Ref("SystemFillColorCriticalBrush"))
                        .AutomationName("清空已选英雄")
                ),
                HStack(6, selectedChips.ToArray())
            ).Margin(new Thickness(0, 4, 0, 8));
        }

        // 搜索栏
        Element searchBar = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                TextBox(searchQuery, onSearchQueryChanged, "搜索英雄名称、别名或称号...")
                    .AutomationName("英雄库搜索")
                    .Grid(row: 0, column: 0),
                TextBlock(isLoading ? "正在加载英雄库..." : $"共 {allChampions.Count} 位英雄")
                    .FontSize(12)
                    .Foreground(Theme.SecondaryText)
                    .VAlign(VerticalAlignment.Center)
                    .Margin(new Thickness(12, 0, 0, 0))
                    .Grid(row: 0, column: 1),
            ]
        ).Margin(new Thickness(0, 0, 0, 8));

        if (isLoading && allChampions.Count == 0)
        {
            return VStack(0,
                selectedSection,
                searchBar,
                TextBlock("正在从客户端拉取英雄数据，请稍候...").FontSize(12).Foreground(Theme.SecondaryText).Margin(new Thickness(0, 16, 0, 16))
            );
        }

        // 过滤
        var filtered = allChampions.Where(c =>
        {
            if (string.IsNullOrWhiteSpace(searchQuery)) return true;
            string q = searchQuery.Trim().ToLowerInvariant();
            return c.Name.ToLowerInvariant().Contains(q)
                || c.Alias.ToLowerInvariant().Contains(q)
                || c.Title.ToLowerInvariant().Contains(q)
                || c.Id.ToString() == q;
        }).ToList();

        // 英雄候选网格（每行展示 5 个小项）
        var selectedSet = new HashSet<int>(selectedIds);
        var rows = new List<Element>();
        var currentRow = new List<Element>();

        foreach (var champ in filtered)
        {
            bool isSelected = selectedSet.Contains(champ.Id);
            var item = Button(
                HStack(6,
                    Component<LcuImage, LcuImageProps>(new LcuImageProps(champ.IconPath, Size: 28, CornerRadius: 4)).VAlign(VerticalAlignment.Center),
                    VStack(2,
                        TextBlock(champ.Name).FontSize(12).FontWeight(isSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal),
                        Caption(champ.Title).Foreground(Theme.SecondaryText)
                    ).VAlign(VerticalAlignment.Center)
                ),
                () => onToggleChampion(champ.Id)
            )
            .Width(130)
            .Padding(6, 4)
            .Background(isSelected ? Theme.Ref("LayerFillColorDefaultBrush") : Theme.Ref("SubtleFillColorTransparentBrush"))
            .BorderBrush(isSelected ? palette.Accent : "#20808080")
            .BorderThickness(isSelected ? 1.5 : 1)
            .CornerRadius(6)
            .Margin(new Thickness(0, 0, 6, 6))
            .AutomationName($"选择英雄_{champ.Name}");

            currentRow.Add(item);
            if (currentRow.Count >= 5)
            {
                rows.Add(HStack(0, currentRow.ToArray()));
                currentRow = new List<Element>();
            }
        }

        if (currentRow.Count > 0)
        {
            rows.Add(HStack(0, currentRow.ToArray()));
        }

        var candidateView = ScrollViewer(
            VStack(0, rows.ToArray())
        ).Height(180);

        return VStack(0,
            selectedSection,
            searchBar,
            candidateView
        );
    }
}
