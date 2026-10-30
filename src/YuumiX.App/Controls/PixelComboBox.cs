using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;

namespace YuumiX.App.Controls;

/// <summary>
/// 下拉浮层的打开态快照。由 <see cref="PixelComboBox"/> 在锚点被点击时计算几何并发布，
/// <see cref="DropDownLayer"/> 订阅后渲染。几何全部是**宿主坐标系内的 DIP 整数**：
/// 宿主在主视觉树内（Viewbox 的缩放内容根），与页面使用相同的渲染路径。
/// </summary>
/// <param name="Scope">发起下拉的 <see cref="XamlRoot"/>，用于把状态路由到正确的窗口层。</param>
/// <param name="Labels">选项文案。</param>
/// <param name="SelectedIndex">当前选中项（-1 表示无选中）。</param>
/// <param name="OnSelect">选中回调（先关浮层再回调，保证页面状态更新时浮层已收起）。</param>
/// <param name="Left">浮层左边距（宿主坐标，整数 DIP）。</param>
/// <param name="Top">浮层顶边距（宿主坐标，整数 DIP；空间不足时自动上翻）。</param>
/// <param name="Width">浮层宽度（对齐锚点宽度，整数 DIP）。</param>
/// <param name="MaxHeight">浮层最大高度（超出走内部滚动）。</param>
public sealed record DropDownState(
    XamlRoot Scope,
    FrameworkElement Anchor,
    IReadOnlyList<string> Labels,
    int SelectedIndex,
    Action<int> OnSelect,
    double Left,
    double Top,
    double Width,
    double MaxHeight);

/// <summary>
/// 下拉浮层的全局协调点：一层注册宿主、按钮发布打开态、浮层订阅渲染。
/// 主窗口在缩放内容根挂载 <see cref="DropDownLayer"/>。
/// 注册与开关都只发生在 UI 线程（挂载回调与点击回调），无需加锁。
/// </summary>
public static class DropDownServiceHost
{
    private static readonly Dictionary<XamlRoot, FrameworkElement> Hosts = [];
    private static DropDownState? _current;
    private static PixelComboTriggerControl? _pending;

    /// <summary>打开态变更通知。参数为 <c>null</c> 表示请求收起。</summary>
    public static event Action<DropDownState?>? Changed;

    /// <summary>浮层挂载时注册自己的容器（几何与命中测试都以它为基准）。scope 为空时忽略（尚未进视觉树）。</summary>
    public static void Register(XamlRoot scope, FrameworkElement host)
    {
        if (scope is not null)
        {
            Hosts[scope] = host;
        }
    }

    /// <summary>取某窗口当前注册的宿主容器；尚未挂载层的窗口返回 <c>null</c>（按钮点击则退化为无操作）。</summary>
    public static FrameworkElement? For(XamlRoot scope) =>
        scope is not null && Hosts.TryGetValue(scope, out var host) ? host : null;

    /// <summary>
    /// 按实例反注册：卸载回调里 <see cref="XamlRoot"/> 可能已不可靠，
    /// 遍历找出所有映射到该容器的表项移除（同容器只挂一棵树，命中 0~1 条）。
    /// </summary>
    public static void Detach(FrameworkElement host)
    {
        var scopes = Hosts.Where(kv => ReferenceEquals(kv.Value, host)).Select(kv => kv.Key).ToList();
        if (scopes.Count > 0)
        {
            Close();
        }

        foreach (var scope in scopes)
        {
            Hosts.Remove(scope);
        }
    }

    /// <summary>发布打开态（若已有浮层打开会被新状态直接覆盖）。</summary>
    public static void Open(DropDownState state)
    {
        if (_current is not null) Close();
        _pending = null;
        _current = state;
        if (state.Anchor is PixelComboTriggerControl trigger) trigger.SetExpanded(true);
        Changed?.Invoke(state);
    }

    /// <summary>收起浮层。</summary>
    public static void Close()
    {
        _pending?.SetExpanded(false);
        _pending = null;
        if (_current?.Anchor is PixelComboTriggerControl trigger) trigger.SetExpanded(false);
        _current = null;
        Changed?.Invoke(null);
    }

    public static void Prepare(PixelComboTriggerControl trigger)
    {
        Close();
        _pending = trigger;
        trigger.SetExpanded(true);
    }

    public static void CloseFor(FrameworkElement anchor)
    {
        if (ReferenceEquals(_pending, anchor) || ReferenceEquals(_current?.Anchor, anchor))
        {
            Close();
        }
    }
}

/// <summary>
/// 自绘下拉选择框：**主树内浮层**方案，替代原生 <c>ComboBox</c>。
///
/// 浮层挂在主视觉树中的缩放内容根，选项使用原生 ListView。
/// </summary>
public static class PixelComboBox
{
    /// <summary>单行选项高度（整数 DIP）。</summary>
    internal const double RowHeight = 40;

    /// <summary>浮层内边距。</summary>
    internal const int ListPadding = 4;

    /// <summary>浮层与锚点的间距。</summary>
    private const int AnchorGap = 4;

    /// <summary>浮层最大高度：约 9 行可见，其余滚动。</summary>
    private const double MaxListHeight = RowHeight * 9 + ListPadding * 2;

    /// <summary>浮层最小宽度：选项过短时保持可点面积。</summary>
    private const int MinListWidth = 120;

    /// <summary>
    /// 渲染下拉选择框，宽度、启用状态和无障碍名称通过参数设置。
    /// </summary>
    /// <param name="labels">选项文案（只支持纯文本）。</param>
    /// <param name="selectedIndex">当前选中项，越界按无选中处理。</param>
    /// <param name="onSelectedIndexChanged">选中回调。</param>
    /// <param name="automationName">无障碍名（必填语义由调用方保证）。</param>
    public static Element Render(
        IReadOnlyList<string> labels,
        int selectedIndex,
        Action<int> onSelectedIndexChanged,
        string automationName = "", double width = 160, bool isEnabled = true) =>
        Border(Component<PixelComboTrigger, PixelComboTriggerProps>(new(labels, selectedIndex, onSelectedIndexChanged, automationName, width, isEnabled)));

    internal static void OpenDropDown(
        FrameworkElement anchor,
        IReadOnlyList<string> labels,
        int selectedIndex,
        Action<int> onSelectedIndexChanged)
    {
        var host = DropDownServiceHost.For(anchor.XamlRoot);
        if (host is null)
        {
            DropDownServiceHost.CloseFor(anchor);
            return;
        }

        var origin = anchor.TransformToVisual(host).TransformPoint(new Point(0, 0));
        var placement = CalculatePlacement(origin.X, origin.Y, anchor.ActualWidth, anchor.ActualHeight, host.ActualWidth, host.ActualHeight, labels.Count);
        if (placement is null)
        {
            DropDownServiceHost.CloseFor(anchor);
            return;
        }

        var (left, listTop, width, listHeight) = placement.Value;

        DropDownServiceHost.Open(new DropDownState(
            anchor.XamlRoot,
            anchor,
            labels,
            selectedIndex,
            onSelectedIndexChanged,
            left,
            listTop,
            width,
            listHeight));
    }
    internal static (int Left, int Top, int Width, int Height)? CalculatePlacement(
        double x, double y, double anchorWidth, double anchorHeight, double hostWidth, double hostHeight, int count)
    {
        if (count <= 0 || hostWidth < 1 || hostHeight < 1)
        {
            return null;
        }

        var left = (int)Math.Floor(x);
        var top = (int)Math.Floor(y);
        var availableWidth = (int)Math.Floor(hostWidth);
        var availableHeight = (int)Math.Floor(hostHeight);
        var height = Math.Min((int)Math.Min(count * RowHeight + ListPadding * 2, MaxListHeight), availableHeight);
        var listTop = top + (int)Math.Ceiling(anchorHeight) + AnchorGap;
        if (listTop + height > availableHeight - AnchorGap && top - height - AnchorGap >= 0)
        {
            listTop = top - height - AnchorGap;
        }

        var width = Math.Min(Math.Max((int)Math.Ceiling(anchorWidth), MinListWidth), availableWidth);
        return (Math.Max(0, Math.Min(left, availableWidth - width)),
            Math.Max(0, Math.Min(listTop, availableHeight - height)), width, height);
    }

    internal static bool ShouldCommit(int index, int selectedIndex, int count) =>
        index >= 0 && index < count && index != selectedIndex;

}

public sealed record PixelComboTriggerProps(IReadOnlyList<string> Labels, int SelectedIndex, Action<int> OnChanged, string AutomationName, double Width, bool IsEnabled);

public sealed class PixelComboTrigger : Component<PixelComboTriggerProps>
{
    public override Element Render()
    {
        var labels = Props.Labels;
        var selectedIndex = Props.SelectedIndex;
        var onSelectedIndexChanged = Props.OnChanged;
        var automationName = Props.AutomationName;
        var current = selectedIndex >= 0 && selectedIndex < labels.Count ? labels[selectedIndex] : string.Empty;

        Element content = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                Body(current)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 0),
                Caption(Icons.ChevronDown)
                    .FontFamily(Icons.FontFamily)
                    .Foreground(Props.IsEnabled ? Theme.SecondaryText : Theme.DisabledText)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 1),
            ]);

        var anchorRef = UseRef<FrameworkElement?>(null);
        var openRef = UseRef<Action?>(null);
        openRef.Current = () =>
            {
                if (labels.Count > 0 && anchorRef.Current is { XamlRoot: not null } anchor && DropDownServiceHost.For(anchor.XamlRoot) is not null)
                {
                    var trigger = (PixelComboTriggerControl)anchor;
                    DropDownServiceHost.Prepare(trigger);
                    var generation = trigger.Generation;
                    anchor.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                    if (!anchor.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (trigger.Generation != generation) return;
                        if (ReferenceEquals(anchorRef.Current, anchor) && anchor.IsLoaded && anchor.XamlRoot is not null)
                        {
                            PixelComboBox.OpenDropDown(anchor, labels, selectedIndex, onSelectedIndexChanged);
                        }
                        else
                        {
                            DropDownServiceHost.CloseFor(trigger);
                        }
                    }))
                    {
                        DropDownServiceHost.CloseFor(trigger);
                    }
                }
            };
        return PixelComboTriggerControlElement.PixelComboTriggerControl(content: content, onClick: () => openRef.Current?.Invoke())
            .Width(Props.Width)
            .IsEnabled(Props.IsEnabled)
            .OnMountAdd(element =>
            {
                anchorRef.Current = element;
                var trigger = (PixelComboTriggerControl)element;
                trigger.ExpandRequested = () => openRef.Current?.Invoke();
                trigger.CollapseRequested = () =>
                {
                    trigger.SetExpanded(false);
                    DropDownServiceHost.CloseFor(trigger);
                };
            })
            .OnUnmountAdd(element =>
            {
                DropDownServiceHost.CloseFor(element);
                var trigger = (PixelComboTriggerControl)element;
                trigger.SetExpanded(false);
                trigger.ExpandRequested = null;
                trigger.CollapseRequested = null;
                anchorRef.Current = null;
            })
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Padding(12, 0)
            .Height(32)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Background(Theme.Ref("ComboBoxBackground"))
            .WithBorder(Theme.Ref("ComboBoxBorderBrush"), 1)
            .AutomationName(string.IsNullOrEmpty(automationName) ? current : $"{automationName}，{current}");
    }
}

/// <summary>
/// 全局下拉浮层宿主：挂在窗口缩放内容根（AppShell contentContainer），
/// 订阅 <see cref="DropDownServiceHost.Changed"/> 渲染主树内浮层。
/// 层根永久挂载（空态下无背景、不参与命中测试、不影响布局）。
/// </summary>
public sealed class DropDownLayer : Component
{
    public override Element Render()
    {
        var (state, setState) = UseState<DropDownState?>(null, threadSafe: true);
        var selectedRef = UseRef(-1);
        var navigatingRef = UseRef(false);
        var committedRef = UseRef(false);

        UseEffect(
            () =>
            {
                void OnChanged(DropDownState? snapshot)
                {
                    if (snapshot is null || DropDownServiceHost.For(snapshot.Scope) is not null)
                    {
                        selectedRef.Current = snapshot?.SelectedIndex ?? -1;
                        if (snapshot is not null) committedRef.Current = false;
                        setState(snapshot);
                    }
                }
                DropDownServiceHost.Changed += OnChanged;
                return () =>
                {
                    DropDownServiceHost.Changed -= OnChanged;
                    DropDownServiceHost.Close();
                };
            },
            0);

        // 层根**永久挂载**：注册宿主容器发生在挂载回调里，若空态返回 Empty() 会导致
        // 挂载回调永不执行，按钮点击时查不到宿主、下拉永远打不开（实测踩坑 2026-10-29）。
        // 空态下根 Grid 无背景 → 不参与命中测试，视觉与布局零影响。
        //
        // 注册时序：挂载回调时 XamlRoot 尚为 null（元素还没进视觉树，直接当字典 key 会抛
        // ArgumentNullException，实测踩坑 2026-10-29），因此优先用挂载回调里已就绪的 XamlRoot，
        // 否则等 Loaded 事件再注册；Unloaded 反注册。Loaded/Unloaded 委托用 UseRef 缓存
        // 保证跨渲染同实例（AGENTS.md §5.7），挂载/卸载配对挂卸。
        var loadedRef = UseRef<RoutedEventHandler?>(null);
        var unloadedRef = UseRef<RoutedEventHandler?>(null);
        loadedRef.Current ??= (sender, _) =>
        {
            if (sender is FrameworkElement fe)
            {
                DropDownServiceHost.Register(fe.XamlRoot, fe);
            }
        };
        unloadedRef.Current ??= (sender, _) =>
        {
            if (sender is FrameworkElement fe)
            {
                DropDownServiceHost.Detach(fe);
            }
        };

        return Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                state is null
                    ? Empty()
                    : Border(Empty())
                        // 纯命中测试层：必须全透明，不给页面叠任何可见色调（用户反馈点开后发灰看不清）。
                        .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                        .OnPointerPressed((_, args) =>
                        {
                            args.Handled = true;
                            DropDownServiceHost.Close();
                            state.Anchor.Focus(FocusState.Programmatic);
                        }),
                state is null ? Empty() : RenderList(state, selectedRef, navigatingRef, committedRef).WithKey("dropdown_list"),
            ])
            .OnMountAdd(element =>
            {
                var fe = (FrameworkElement)element;
                if (fe.XamlRoot is not null)
                {
                    // 复用池取出的已挂树元素：XamlRoot 已就绪，直接注册。
                    DropDownServiceHost.Register(fe.XamlRoot, fe);
                }
                else
                {
                    fe.Loaded += loadedRef.Current!;
                }

                fe.Unloaded += unloadedRef.Current!;
            })
            .OnUnmountAdd(element =>
            {
                var fe = (FrameworkElement)element;
                DropDownServiceHost.Detach(fe);
                fe.Loaded -= loadedRef.Current!;
                fe.Unloaded -= unloadedRef.Current!;
            });
    }

    private static Element RenderList(DropDownState state, Ref<int> selectedRef, Ref<bool> navigatingRef, Ref<bool> committedRef)
    {
        void Commit()
        {
            if (committedRef.Current)
            {
                return;
            }

            committedRef.Current = true;
            var index = selectedRef.Current;
            var focusState = FocusManager.GetFocusedElement(state.Scope) is Control { FocusState: FocusState.Keyboard }
                ? FocusState.Keyboard
                : FocusState.Pointer;
            DropDownServiceHost.Close();
            if (PixelComboBox.ShouldCommit(index, state.SelectedIndex, state.Labels.Count))
            {
                state.OnSelect(index);
            }
            state.Anchor.Focus(focusState);
        }

        var options = ListView((int?)state.SelectedIndex, index =>
            {
                selectedRef.Current = index;
                if (!navigatingRef.Current && index != state.SelectedIndex)
                {
                    Commit();
                }
            }, state.Labels.Select(label => (Element)Body(label)).ToArray())
            .Set(list =>
            {
                Microsoft.UI.Xaml.Controls.ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
                Microsoft.UI.Xaml.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
            })
            .AutomationName("选项")
            .OnTapped((_, _) => Commit())
            .OnPreviewKeyDown((sender, args) =>
            {
                if (args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown)
                {
                    navigatingRef.Current = true;
                    if (sender is FrameworkElement element)
                    {
                        element.DispatcherQueue.TryEnqueue(() => navigatingRef.Current = false);
                    }
                }
                else if (args.Key == VirtualKey.Escape)
                {
                    args.Handled = true;
                    DropDownServiceHost.Close();
                    state.Anchor.Focus(FocusState.Keyboard);
                }
                else if (args.Key is VirtualKey.Enter or VirtualKey.Space)
                {
                    args.Handled = true;
                    Commit();
                }
            })
            .OnMountAdd(element => element.DispatcherQueue.TryEnqueue(() =>
            {
                element.UpdateLayout();
                if (element is ListView listBox && listBox.ContainerFromIndex(Math.Max(0, state.SelectedIndex)) is Control item)
                {
                    item.Focus(state.Anchor.FocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Pointer);
                }
            }));

        Element list = Border(options.HAlign(HorizontalAlignment.Stretch))
            .Padding(PixelComboBox.ListPadding)
            .Background(Theme.Ref("SolidBackgroundFillColorBaseBrush"))
            .WithBorder(Theme.CardStroke, 1)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Width(state.Width)
            .Height(state.MaxHeight)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Top)
            .Margin(new Thickness(state.Left, state.Top, 0, 0));

        return list;
    }
}
