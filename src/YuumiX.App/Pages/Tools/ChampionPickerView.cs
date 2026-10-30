using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Tools;
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
        var i18n = Composition.AppHost.Services?.I18n;

        // 已选英雄预览行
        Element selectedSection;
        if (selectedIds.Count == 0)
        {
            selectedSection = Border(
                Caption(i18n?.T("tools.noCandidates", "未选择任何英雄（按下方列表点击添加；将按添加顺序优先选用）") ?? "未选择任何英雄（按下方列表点击添加；将按添加顺序优先选用）")
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
                            Caption($"#{order}").Foreground(Theme.Accent).VAlign(VerticalAlignment.Center),
                            Component<LcuImage, LcuImageProps>(new LcuImageProps(iconPath, Size: 24, CornerRadius: 4)).VAlign(VerticalAlignment.Center),
                            Caption(name).VAlign(VerticalAlignment.Center),
                            Button(Caption(Icons.Cancel).FontFamily(Icons.FontFamily), () => onToggleChampion(id))
                                .Padding(4, 2)
                                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                                .Foreground(Theme.SecondaryText)
                                .AutomationName($"移除_{name}")
                        )
                    )
                    .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
                    .BorderBrush(Theme.Ref("CardStrokeColorDefaultBrush"))
                    .BorderThickness(1)
                    .CornerRadius(YuumiTheme.OverlayRadius)
                    .Padding(8, 4)
                );
            }

            var isEn = i18n?.CurrentLanguage == YuumiX.Core.Localization.SupportedLanguages.EnUs;
            var selectedPrefix = isEn ? "Selected" : "已选英雄";

            selectedSection = VStack(4,
                HStack(8,
                    Caption($"{selectedPrefix} ({selectedIds.Count}/{maxCount}):")
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center),
                    Button(Caption(i18n?.T("tools.clearBtn", "清空全部") ?? "清空全部"), onClearSelection)
                        .Padding(6, 2)
                        .Foreground(Theme.Ref("SystemFillColorCriticalBrush"))
                        .AutomationName("清空已选英雄")
                ),
                HStack(6, selectedChips.ToArray())
            ).Margin(new Thickness(0, 4, 0, 8));
        }

        var isEnPicker = i18n?.CurrentLanguage == YuumiX.Core.Localization.SupportedLanguages.EnUs;

        // 过滤：名称/称号/英文别名/id + 别名索引（中文别称、拼音全拼、拼音首字母），规则集中在 Core 便于单测
        var aliasProvider = Composition.AppHost.CurrentServices?.Services.GetService(typeof(IChampionAliasProvider)) as IChampionAliasProvider;
        var filtered = allChampions.Where(c => ChampionSearch.Matches(c, searchQuery, aliasProvider)).ToList();
        bool isSearching = !string.IsNullOrWhiteSpace(searchQuery);


        var countText = isSearching
            ? (isEnPicker ? $"{filtered.Count} / {allChampions.Count} matched" : $"匹配 {filtered.Count} / 共 {allChampions.Count} 位英雄")
            : (isEnPicker ? $"{allChampions.Count} champions" : $"共 {allChampions.Count} 位英雄");

        // 搜索栏
        Element searchBar = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                TextBox(searchQuery, onSearchQueryChanged, i18n?.T("tools.searchChampPlaceholder", "搜索英雄名称、称号、拼音或别称...") ?? "搜索英雄名称、称号、拼音或别称...")
                    .AutomationName("英雄库搜索")
                    .Grid(row: 0, column: 0),
                Caption(isLoading ? (i18n?.T("tools.loadingChamps", "正在加载英雄库...") ?? "正在加载英雄库...") : countText)
                    .Foreground(Theme.SecondaryText)
                    .VAlign(VerticalAlignment.Center)
                    .Margin(new Thickness(12, 0, 0, 0))
                    .Grid(row: 0, column: 1),
            ]
        ).Margin(new Thickness(0, 0, 0, 8));

        if (isLoading && allChampions.Count == 0)
        {
            var loadingWaitText = isEnPicker ? "Fetching champion data from client, please wait..." : "正在从客户端拉取英雄数据，请稍候...";
            return VStack(0,
                selectedSection,
                searchBar,
                Caption(loadingWaitText).Foreground(Theme.SecondaryText).Margin(new Thickness(0, 16, 0, 16))
            );
        }

        if (filtered.Count == 0)
        {
            // 两种情况要分开：搜索无结果（提示可试的检索维度）vs 英雄库本身为空（没拉到数据）
            string emptyText;
            if (!isSearching)
            {
                emptyText = isEnPicker ? "Champion library is empty." : "英雄库暂无可选英雄。";
            }
            else
            {
                emptyText = isEnPicker
                    ? $"No champion matches \"{searchQuery.Trim()}\". Try name, title, pinyin or a nickname."
                    : $"没有匹配「{searchQuery.Trim()}」的英雄，可试名称、称号、拼音或常见别称。";
            }

            return VStack(0,
                selectedSection,
                searchBar,
                Caption(emptyText).Foreground(Theme.SecondaryText).Margin(new Thickness(0, 16, 0, 16))
            );
        }

        // 英雄候选网格：ItemsRepeater + UniformGridLayout（原生虚拟化，只实现视口内的行、滚动时回收复用）。
        // 原先手工拼「每行 5 个 HStack」，一次性把全部格子挂进树里；视口只有 180px 高、看得见 4~5 行，
        // 其余 85% 的元素从创建到销毁都没进过视口 —— 展开那一下的卡顿正是它们。
        var selectedSet = new HashSet<int>(selectedIds);

        var tileLayout = new UniformGridLayout
        {
            Orientation = Orientation.Horizontal,
            // 横向排布下 MaximumRowsOrColumns 是「每行最多几项」，与原来每行 5 个一致。
            MaximumRowsOrColumns = 5,
            MinItemWidth = 130,
            MinItemHeight = 44,
            MinRowSpacing = 4,
            MinColumnSpacing = 4,
            ItemsStretch = UniformGridLayoutItemsStretch.None,
            ItemsJustification = UniformGridLayoutItemsJustification.Start,
        };

        var grid = ItemsRepeater(
            filtered,
            champ => champ.Id.ToString(),
            (champ, _) => ChampionTile(champ, selectedSet.Contains(champ.Id), onToggleChampion))
            with
            { Layout = tileLayout };

        var candidateView = ScrollViewer(grid).Height(180);

        return VStack(0,
            selectedSection,
            searchBar,
            candidateView
        );
    }

    /// <summary>
    /// 英雄格子。抽成独立方法是因为它现在由 <c>ItemsRepeater</c> 的 viewBuilder 逐项调用：
    /// 滚动复用时会用新英雄重新调一次，所以这里必须是纯函数（不能捕获循环变量、不能有外部可变状态）。
    /// 尺寸与间距交给 <see cref="UniformGridLayout"/>，格子本身不再写 Margin。
    /// </summary>
    private static Element ChampionTile(
        ChampionSummaryItem champ,
        bool isSelected,
        Action<int> onToggleChampion) =>
        Button(
            HStack(6,
                Component<LcuImage, LcuImageProps>(new LcuImageProps(champ.IconPath, Size: 28, CornerRadius: 4)).VAlign(VerticalAlignment.Center),
                VStack(2,
                    (isSelected ? BodyStrong(champ.Name) : Body(champ.Name)),
                    Caption(champ.Title).Foreground(Theme.SecondaryText)
                ).VAlign(VerticalAlignment.Center)
            ),
            () => onToggleChampion(champ.Id))
        .Width(130)
        .Padding(4, 4)
        .Background(isSelected ? Theme.Ref("LayerFillColorDefaultBrush") : Theme.Ref("SubtleFillColorTransparentBrush"))
        .BorderBrush(isSelected ? Theme.Accent : Theme.ControlStroke)
        .BorderThickness(isSelected ? 1.5 : 1)
        .CornerRadius(YuumiTheme.OverlayRadius)
        .AutomationName($"选择英雄_{champ.Name}");
}
