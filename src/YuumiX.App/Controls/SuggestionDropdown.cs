using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;
using XamlScrollViewer = Microsoft.UI.Xaml.Controls.ScrollViewer;
using YuumiX.App.Theming;

namespace YuumiX.App.Controls;

/// <summary>
/// 下拉面板分节配置模型（预留后续扩展插槽：如常用关注、当前对局队友等）。
/// </summary>
internal sealed record SuggestionSection(
    string Title,
    Element Content,
    Element? HeaderAction = null);

/// <summary>
/// 通用自定义下拉建议组件：
/// 1. 多列网格排列（一行两到三项，Chip 样式）；
/// 2. 支持单项删除（×）与全部清空；
/// 3. 分节架构（Section），支持后续接入任意自定义扩展内容；
/// 4. 遵守原生 Fluent 规范（Theme 语义令牌、ControlRadius、高度自适应无多余滚动条）。
/// </summary>
internal static class SuggestionDropdown
{
    private const int DefaultColumns = 2;

    private static readonly RoutedEventHandler DropdownLoadedHandler = (sender, _) =>
    {
        if (sender is not FrameworkElement fe)
        {
            return;
        }

        DependencyObject? current = fe;
        while (current is not null)
        {
            if (current is XamlScrollViewer sv)
            {
                sv.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                sv.HorizontalScrollMode = ScrollMode.Disabled;
                sv.VerticalScrollMode = ScrollMode.Enabled;
            }
            else if (current is FlyoutPresenter fp)
            {
                fp.Padding = new Thickness(0);
                XamlScrollViewer.SetVerticalScrollBarVisibility(fp, ScrollBarVisibility.Hidden);
                XamlScrollViewer.SetHorizontalScrollBarVisibility(fp, ScrollBarVisibility.Disabled);
                XamlScrollViewer.SetHorizontalScrollMode(fp, ScrollMode.Disabled);
                XamlScrollViewer.SetVerticalScrollMode(fp, ScrollMode.Enabled);
            }

            current = VisualTreeHelper.GetParent(current) ?? (current as FrameworkElement)?.Parent;
        }
    };

    public static Element Render(
        IReadOnlyList<string> historyList,
        Action<string> onSelectHistory,
        Action<string> onRemoveHistory,
        Action onClearHistory,
        IReadOnlyList<SuggestionSection>? extraSections = null,
        int columns = DefaultColumns)
    {
        // 1. 搜索历史分节
        Element historySection = RenderHistorySection(
            historyList,
            onSelectHistory,
            onRemoveHistory,
            onClearHistory,
            columns);

        // 2. 组装所有分节（支持后续插槽扩展）
        List<Element> sections = [historySection];
        if (extraSections is not null)
        {
            foreach (var extra in extraSections)
            {
                sections.Add(RenderCustomSection(extra));
            }
        }

        Element content = VStack(12, sections.ToArray())
            .Padding(12);

        return Border(content)
            .Width(460)
            .OnMountAdd(element =>
            {
                if (element is FrameworkElement fe)
                {
                    fe.Loaded += DropdownLoadedHandler;
                }
            })
            .OnUnmountAdd(element =>
            {
                if (element is FrameworkElement fe)
                {
                    fe.Loaded -= DropdownLoadedHandler;
                }
            });
    }

    private static Element RenderHistorySection(
        IReadOnlyList<string> historyList,
        Action<string> onSelectHistory,
        Action<string> onRemoveHistory,
        Action onClearHistory,
        int columns)
    {
        Element headerLeft = Caption("搜索历史")
            .Foreground(Theme.SecondaryText)
            .VAlign(VerticalAlignment.Center);

        Element? headerAction = historyList.Count > 0
            ? Button(
                Caption("清空全部").Foreground(Theme.SecondaryText),
                onClearHistory)
                .ApplyStyle("SubtleButtonStyle")
                .Padding(6, 2)
                .AutomationName("清空全部搜索历史")
                .ToolTip("清空全部搜索历史")
            : null;

        Element header = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                headerLeft.Grid(row: 0, column: 0),
                (headerAction ?? Empty()).Grid(row: 0, column: 1),
            ]);

        Element content = historyList.Count == 0
            ? Caption("暂无搜索历史")
                .Foreground(Theme.SecondaryText)
                .HAlign(HorizontalAlignment.Center)
                .Padding(0, 16)
            : RenderChipGrid(historyList, onSelectHistory, onRemoveHistory, columns);

        return VStack(8, header, content);
    }

    private static Element RenderChipGrid(
        IReadOnlyList<string> historyList,
        Action<string> onSelectHistory,
        Action<string> onRemoveHistory,
        int columns)
    {
        int effectiveCols = Math.Max(1, columns);
        int rows = (historyList.Count + effectiveCols - 1) / effectiveCols;

        var colSizes = new GridSize[effectiveCols];
        for (int i = 0; i < effectiveCols; i++)
        {
            colSizes[i] = GridSize.Star();
        }

        var rowSizes = new GridSize[rows];
        for (int i = 0; i < rows; i++)
        {
            rowSizes[i] = GridSize.Auto;
        }

        var chipElements = new Element[historyList.Count];
        for (int i = 0; i < historyList.Count; i++)
        {
            int r = i / effectiveCols;
            int c = i % effectiveCols;
            string item = historyList[i];
            chipElements[i] = HistoryChip(item, onSelectHistory, onRemoveHistory)
                .WithKey(item)
                .Grid(row: r, column: c);
        }

        return (Grid(colSizes, rowSizes, chipElements) with
        {
            RowSpacing = 8,
            ColumnSpacing = 8,
        });
    }

    private static Element HistoryChip(
        string item,
        Action<string> onSelect,
        Action<string> onRemove)
    {
        int hashIdx = item.IndexOf('#');
        string namePart = hashIdx >= 0 ? item[..hashIdx] : item;
        string tagPart = hashIdx >= 0 ? item[hashIdx..] : string.Empty;

        Element textContent = HStack(4,
            Caption(namePart)
                .Foreground(Theme.PrimaryText)
                .TextTrimming(TextTrimming.CharacterEllipsis),
            !string.IsNullOrEmpty(tagPart)
                ? Caption(tagPart)
                    .Foreground(Theme.SecondaryText)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                : Empty())
        .VAlign(VerticalAlignment.Center);

        Element selectButton = Button(textContent, () => onSelect(item))
            .ApplyStyle("SubtleButtonStyle")
            .HorizontalContentAlignment(HorizontalAlignment.Left)
            .Padding(8, 0)
            .Height(32)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationName($"搜索 {item}")
            .ToolTip(item);

        Element deleteButton = Button(
            Caption(Icons.Cancel)
                .FontFamily(Icons.FontFamily)
                .Foreground(Theme.SecondaryText),
            () => onRemove(item))
            .ApplyStyle("SubtleButtonStyle")
            .Width(28)
            .Height(32)
            .Padding(0)
            .AutomationName($"删除历史记录 {item}")
            .ToolTip("删除此条记录");

        return Border(
            Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [
                    selectButton.Grid(row: 0, column: 0),
                    deleteButton.Grid(row: 0, column: 1),
                ]))
            .Background(Theme.SubtleFill)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Height(32);
    }

    private static Element RenderCustomSection(SuggestionSection section)
    {
        Element headerLeft = Caption(section.Title)
            .Foreground(Theme.SecondaryText)
            .VAlign(VerticalAlignment.Center);

        Element header = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                headerLeft.Grid(row: 0, column: 0),
                (section.HeaderAction ?? Empty()).Grid(row: 0, column: 1),
            ]);

        return VStack(8, header, section.Content);
    }
}
