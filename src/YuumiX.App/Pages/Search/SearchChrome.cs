using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.App.Pages.Search;

/// <summary>顶部工具栏 props（自 <c>SearchPage.Toolbar</c> 拆出，R14）。</summary>
internal sealed record SearchToolbarProps(
    string Query,
    Action<string> OnQueryChanged,
    IReadOnlyList<string> HistoryList,
    Action<string> OnSelectHistory,
    Action<string> OnRemoveHistory,
    Action OnClearHistory,
    Action OnSearch,
    Action OnCareer,
    bool HasSummoner,
    string[] FilterLabels,
    int FilterIndex,
    Action<int> OnFilterChanged,
    string? Notice,
    bool Searching,
    SummonerProfile? CurrentSummoner,
    bool UploadConfigured,
    bool UploadEnabled,
    Action<bool> OnUploadToggle,
    IReadOnlyList<SuggestionSection>? ExtraSections = null);

/// <summary>
/// 顶部工具栏：搜索框带自定义建议下拉面板吃满剩余宽度，右侧为定宽功能按钮与筛选下拉。
/// 独立成组件是为了用 <c>UseRef</c> 缓存建议弹出的三个事件委托（R9，照 AppShell 做法）。
/// </summary>
internal sealed class SearchToolbar : Component<SearchToolbarProps>
{
    private static Style? _dropdownFlyoutPresenterStyle;

    private static Style GetOrCreateDropdownFlyoutPresenterStyle()
    {
        if (_dropdownFlyoutPresenterStyle is not null)
        {
            return _dropdownFlyoutPresenterStyle;
        }

        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Microsoft.UI.Xaml.Controls.ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        style.Setters.Add(new Setter(Microsoft.UI.Xaml.Controls.ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden));
        style.Setters.Add(new Setter(Microsoft.UI.Xaml.Controls.ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled));
        style.Setters.Add(new Setter(Microsoft.UI.Xaml.Controls.ScrollViewer.VerticalScrollModeProperty, ScrollMode.Enabled));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        _dropdownFlyoutPresenterStyle = style;
        return style;
    }

    public override Element Render()
    {
        var p = Props;

        var (isOpen, setIsOpen) = UseState(false);
        var flyoutRef = UseRef<Flyout?>(null);

        // 如果输入框内容为空、或与当前已展示的召唤师姓名一致（代表未输入新内容，仅仅是点击聚焦），则展示全部历史记录（对齐旧版 Search.vue）
        bool isDefaultQuery = string.IsNullOrWhiteSpace(p.Query)
            || (p.CurrentSummoner is not null && (
                string.Equals(p.Query.Trim(), p.CurrentSummoner.RiotId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Query.Trim(), p.CurrentSummoner.GameName, StringComparison.OrdinalIgnoreCase)));

        string[] filteredHistory = isDefaultQuery
            ? [.. p.HistoryList]
            : [.. p.HistoryList.Where(item => item.Contains(p.Query.Trim(), StringComparison.OrdinalIgnoreCase))];

        // 建议面板弹出行为：聚焦 / 键入 / 按下均展开（列表或扩展分节非空才弹）。
        // 委托必须跨渲染同一实例（AGENTS.md §5.7）：UseRef 缓存 + OnMountAdd/OnUnmountAdd 配对挂卸。
        // 状态读取放 action ref（AGENTS.md §5.18），避免捕获旧渲染周期的闭包。
        var openDropdownRef = UseRef<Action?>(null);
        openDropdownRef.Current = () =>
        {
            bool hasContent = filteredHistory.Length > 0 || (p.ExtraSections is not null && p.ExtraSections.Count > 0);
            if (hasContent && !isOpen)
            {
                setIsOpen(true);
            }
        };

        var openOnFocus = UseRef<RoutedEventHandler?>(null);
        openOnFocus.Current ??= (_, _) => openDropdownRef.Current?.Invoke();

        var openOnTyping = UseRef<TypedEventHandler<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs>?>(null);
        openOnTyping.Current ??= (_, e) =>
        {
            if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                openDropdownRef.Current?.Invoke();
            }
        };

        var openOnPointerPressed = UseRef<PointerEventHandler?>(null);
        openOnPointerPressed.Current ??= (_, _) => openDropdownRef.Current?.Invoke();

        var i18n = Composition.AppHost.Services?.I18n;
        var searchPlaceholder = i18n?.T("search.searchPlaceholder", "输入召唤师名称进行搜索…") ?? "输入召唤师名称进行搜索…";
        var careerBtnText = i18n?.T("nav.career", "生涯") ?? "生涯";

        void HandleSelectHistory(string item)
        {
            flyoutRef.Current?.Hide();
            setIsOpen(false);
            p.OnSelectHistory(item);
        }

        void HandleSearch()
        {
            flyoutRef.Current?.Hide();
            setIsOpen(false);
            p.OnSearch();
        }

        Element searchBox = AutoSuggestBox(
                p.Query,
                p.OnQueryChanged,
                _ => HandleSearch())
            .QueryIcon(FontIcon(Icons.Search, Icons.FontFamily))
            .PlaceholderText(searchPlaceholder)
            .AutomationName("召唤师名称搜索")
            .OnMountAdd(element =>
            {
                var box = (AutoSuggestBox)element;
                box.GotFocus += openOnFocus.Current!;
                box.TextChanged += openOnTyping.Current!;
                box.AddHandler(UIElement.PointerPressedEvent, openOnPointerPressed.Current!, handledEventsToo: true);
            })
            .OnUnmountAdd(element =>
            {
                var box = (AutoSuggestBox)element;
                box.GotFocus -= openOnFocus.Current!;
                box.TextChanged -= openOnTyping.Current!;
                box.RemoveHandler(UIElement.PointerPressedEvent, openOnPointerPressed.Current!);
            });

        Element dropdownContent = SuggestionDropdown.Render(
            filteredHistory,
            HandleSelectHistory,
            p.OnRemoveHistory,
            p.OnClearHistory,
            p.ExtraSections);

        Element flyoutSearchBox = (Flyout(searchBox, dropdownContent)
            .Set(f =>
            {
                flyoutRef.Current = f;
                f.FlyoutPresenterStyle ??= GetOrCreateDropdownFlyoutPresenterStyle();
            }) with
            {
                IsOpen = isOpen,
                ShowMode = FlyoutShowMode.Transient,
                Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
                OnClosed = () =>
                {
                    if (isOpen)
                    {
                        setIsOpen(false);
                    }
                },
            })
            .Grid(row: 0, column: 0);

        Element bar = (Grid(
            [GridSize.Star(), GridSize.Auto, GridSize.Auto, GridSize.Auto],
            [GridSize.Auto],
            [
                flyoutSearchBox,
                Button(careerBtnText, p.OnCareer).IsEnabled(p.HasSummoner).AutomationName("跳转生涯").Grid(row: 0, column: 1),
                ComboBox(p.FilterLabels, p.FilterIndex, p.OnFilterChanged)
                    .AutomationName("队列筛选")
                    .Width(130)
                    .Grid(row: 0, column: 2),
                // 上传开关只在配好服务器地址时出现（旧 Search.vue 的 v-if="General.UploadApiUrl"）
                p.UploadConfigured
                    ? CheckBox(p.UploadEnabled, p.OnUploadToggle, i18n?.T("settings.autoUploadTitle", "上传对局") ?? "上传对局")
                        .AutomationName("上传对局开关")
                        .Margin(8, 0, 0, 0)
                        .Grid(row: 0, column: 3)
                    : Empty().Grid(row: 0, column: 3),
            ]) with { ColumnSpacing = 8 });

        // 提示行放在搜索框下方而不是列表上方：出错的是"搜索"，不是"列表"
        Element body = p.Notice is null
            ? bar
            : VStack(6, bar, Caption(p.Notice).Foreground(Theme.SecondaryText));

        return Border(body)
            .Padding(8)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(HorizontalAlignment.Stretch);
    }
}

/// <summary>列表区与分页的视觉构件（自 <c>SearchPage</c> 拆出，R14）。</summary>
internal static class SearchChrome
{
    /// <summary>加载中骨架卡数量：每页 10 张，避免数据到达时整列跳动。</summary>
    private const int SkeletonCount = 10;

    /// <summary>底部翻页：只做前后各一页，页码即当前页序号（从 1 开始）。</summary>
    internal static Element Pager(int pageIndex, bool hasMore, Action<int> setPageIndex)
    {
        if (pageIndex == 0 && !hasMore)
        {
            // 只有一页且无更多数据：不占用行高，也不显示一个点不动的翻页器
            return Empty();
        }

        Element previous = Button("‹", () => setPageIndex(pageIndex - 1)).Width(32)
            .AutomationName("上一页")
            .IsEnabled(pageIndex > 0);

        Element next = Button("›", () => setPageIndex(pageIndex + 1)).Width(32)
            .AutomationName("下一页")
            .IsEnabled(hasMore);

        Element content = HStack(8,
                previous,
                Caption((pageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .VAlign(VerticalAlignment.Center),
                next)
            .HAlign(HorizontalAlignment.Center);

        return Border(content)
            .Padding(4)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(HorizontalAlignment.Stretch);
    }

    internal static Element RetryHint(string message, string retry, Action onRetry) =>
        VStack(8, Hint(message), Button(retry, onRetry).AutomationName(retry));

    internal static Element ResourceState(AsyncValue<SummonerProfile?> summoner, AsyncValue<SearchHistoryResult> history,
        bool hasCurrentPage, bool hasSummoner, bool hasMatches, YuumiPalette palette, Action retry, I18nService i18n)
    {
        if (!hasSummoner && summoner is AsyncValue<SummonerProfile?>.Loading or AsyncValue<SummonerProfile?>.Reloading) return BuildSkeletons(palette);
        if (!hasSummoner && summoner is AsyncValue<SummonerProfile?>.Error)
            return RetryHint(i18n.T("search.resolveFailed", "读取召唤师失败"), i18n.T("search.retry", "重试"), retry);
        if (!hasSummoner) return Hint(i18n.T("search.noSummoner", "未取得召唤师，请检查客户端连接"));
        if (history is AsyncValue<SearchHistoryResult>.Error)
            return RetryHint(i18n.T("search.historyFailed", "读取战绩失败"), i18n.T("search.retry", "重试"), retry);
        if (!hasCurrentPage) return BuildSkeletons(palette);
        return Hint(hasMatches ? i18n.T("search.filteredEmpty", "没有符合筛选条件的对局") : i18n.T("search.historyEmpty", "暂无战绩"));
    }

    /// <summary>列表区的说明块：与卡片同宽，避免右侧面板被拉宽。</summary>
    internal static Element Hint(string text) =>
        Border(Caption(text).Foreground(Theme.SecondaryText).TextWrapping())
            .Padding(12)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(HorizontalAlignment.Stretch);

    private static Element BuildSkeletons(YuumiPalette palette)
    {
        var skeletons = new Element[SkeletonCount];
        for (var i = 0; i < skeletons.Length; i++)
        {
            skeletons[i] = MatchCardView.Skeleton(palette)
                .WithKey($"skeleton_{i}")
                .Grid(row: i, column: 0);
        }

        var rows = new GridSize[SkeletonCount];
        Array.Fill(rows, GridSize.Star());

        return Grid([GridSize.Star()], rows, skeletons)
            .VAlign(VerticalAlignment.Stretch)
            .HAlign(HorizontalAlignment.Stretch);
    }
}
