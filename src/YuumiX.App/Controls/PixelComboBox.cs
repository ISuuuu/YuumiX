using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;

namespace YuumiX.App.Controls;

/// <summary>
/// 下拉浮层的打开态快照。由 <see cref="PixelComboBox"/> 在锚点被点击时计算几何并发布，
/// <see cref="DropDownLayer"/> 订阅后渲染。几何全部是**宿主坐标系内的 DIP 整数**：
/// 宿主在主视觉树内（Viewbox 的缩放内容根），主树布局强制取整，
/// 因此浮层与页面同渲染路径——任意「界面缩放」档位下文字都锐利。
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
    IReadOnlyList<string> Labels,
    int SelectedIndex,
    Action<int> OnSelect,
    double Left,
    double Top,
    double Width,
    double MaxHeight);

/// <summary>
/// 下拉浮层的全局协调点：一层注册宿主、按钮发布打开态、浮层订阅渲染。
/// 主窗口（AppShell）与 OP.GG 独立窗口各挂一个 <see cref="DropDownLayer"/>；
/// 以 <see cref="XamlRoot"/> 为路由键，跨窗口互不串扰。
/// 注册与开关都只发生在 UI 线程（挂载回调与点击回调），无需加锁。
/// </summary>
public static class DropDownServiceHost
{
    private static readonly Dictionary<XamlRoot, FrameworkElement> Hosts = [];

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
    public static void Open(DropDownState state) => Changed?.Invoke(state);

    /// <summary>收起浮层。</summary>
    public static void Close() => Changed?.Invoke(null);
}

/// <summary>
/// 自绘下拉选择框：**主树内浮层**方案，替代原生 <c>ComboBox</c>。
///
/// 背景缺陷（2026-10-29 定性）：设置页「界面缩放 125%」走 Viewbox 渲染变换，
/// 原生 ComboBox 下拉宿主在独立 PopupRoot，表面按 1.0 栅格化后被整层 1.25 位图拉伸，
/// 文字重采样发虚（同帧 core/ink 41% vs 页面 78%），且 ComboBox 打开时会改写
/// <c>Popup.Horizontal/VerticalOffset</c>，任何外部吸附修正都会被冲掉或无效。
///
/// 修法：按钮 + 主树内浮层。浮层挂在窗口缩放内容根（AppShell contentContainer）的覆盖 Grid 里，
/// 与页面同渲染路径，缩放倍率下文字依然按矢量栅格化，锐利度与页面正文一致。
///
/// 取舍（相对原生 ComboBox）：
///   - 无键盘上下键/首字母跳转导航，不支持编辑态；点击浮层外任意处收起（含导航栏）。
///   - 选项文案只支持纯文本（与 <c>SettingsCardKit.Select</c> 的 string[] 约定一致）。
/// </summary>
public static class PixelComboBox
{
    /// <summary>单行选项高度（整数 DIP，对齐原生 ComboBoxItem 视觉密度）。</summary>
    internal const double RowHeight = 36;

    /// <summary>浮层内边距。</summary>
    internal const int ListPadding = 4;

    /// <summary>浮层与锚点的间距。</summary>
    private const int AnchorGap = 4;

    /// <summary>浮层最大高度：约 9 行可见，其余滚动。</summary>
    private const double MaxListHeight = RowHeight * 9 + ListPadding * 2;

    /// <summary>浮层最小宽度：选项过短时保持可点面积。</summary>
    private const int MinListWidth = 120;

    /// <summary>
    /// 渲染一个下拉选择框。返回根为 <see cref="Button"/>（Control），
    /// 调用方的 <c>.Width(...)</c> / <c>.IsEnabled(...)</c> / <c>.AutomationName(...)</c> 修饰符照常生效。
    /// </summary>
    /// <param name="labels">选项文案（只支持纯文本）。</param>
    /// <param name="selectedIndex">当前选中项，越界按无选中处理。</param>
    /// <param name="onSelectedIndexChanged">选中回调。</param>
    /// <param name="automationName">无障碍名（必填语义由调用方保证）。</param>
    public static Element Render(
        IReadOnlyList<string> labels,
        int selectedIndex,
        Action<int> onSelectedIndexChanged,
        string automationName = "")
    {
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
                    .Foreground(Theme.SecondaryText)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 1),
            ]);

        // 点击时才测量锚点几何：ActualWidth/TransformToVisual 在这里都是布局后的真实值。
        // 走 OnTapped 拿 sender（Button 工厂的 onClick 回调拿不到元素引用）；键盘激活（Enter）
        // 不展开是已知取舍，见类型注释。
        // 无障碍名缺省回退到选中值文案（分析器把 Grid 承载的文本按钮判为 icon-only，且空名等于未命名）。
        return Button(content, () => { })
            .OnTapped((sender, args) =>
            {
                args.Handled = true;
                if (sender is FrameworkElement anchor)
                {
                    OpenDropDown(anchor, labels, selectedIndex, onSelectedIndexChanged);
                }
            })
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .Padding(12, 0)
            .Height(32)
            .CornerRadius(YuumiTheme.ControlRadius)
            // 收起态不加背景填充（用户 2026-10-29 反馈）：只留描边，与原生收起态观感对齐。
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
            .WithBorder(Theme.ControlStroke, 1)
            .AutomationName(string.IsNullOrEmpty(automationName) ? current : automationName);
    }

    private static void OpenDropDown(
        FrameworkElement anchor,
        IReadOnlyList<string> labels,
        int selectedIndex,
        Action<int> onSelectedIndexChanged)
    {
        var host = DropDownServiceHost.For(anchor.XamlRoot);
        if (host is null)
        {
            return;
        }

        var origin = anchor.TransformToVisual(host).TransformPoint(new Point(0, 0));

        // 主树内浮层坐标全部取整：宿主坐标系里的半像素会沿布局链放大成整层重采样（见工作区记忆）。
        var left = (int)Math.Floor(origin.X);
        var top = (int)Math.Floor(origin.Y);
        var anchorHeight = (int)Math.Ceiling(anchor.ActualHeight);
        var hostHeight = (int)Math.Ceiling(host.ActualHeight);

        var listHeight = (int)Math.Min(labels.Count * RowHeight + ListPadding * 2, MaxListHeight);
        var listTop = top + anchorHeight + AnchorGap;
        if (listTop + listHeight > hostHeight - AnchorGap && top - listHeight - AnchorGap >= 0)
        {
            // 下方放不下且上方有空间：上翻（与原生 ComboBox 同策略）。
            listTop = top - listHeight - AnchorGap;
        }

        var width = Math.Max((int)Math.Ceiling(anchor.ActualWidth), MinListWidth);

        DropDownServiceHost.Open(new DropDownState(
            anchor.XamlRoot,
            labels,
            selectedIndex,
            onSelectedIndexChanged,
            left,
            listTop,
            width,
            listHeight));
    }
}

/// <summary>
/// 全局下拉浮层宿主：挂在窗口缩放内容根（AppShell contentContainer / OpggWindowHost 内容区），
/// 订阅 <see cref="DropDownServiceHost.Changed"/> 渲染主树内浮层。
/// 层根永久挂载（空态下无背景、不参与命中测试、不影响布局）。
/// </summary>
public sealed class DropDownLayer : Component
{
    public override Element Render()
    {
        var (state, setState) = UseState<DropDownState?>(null, threadSafe: true);

        UseEffect(
            () =>
            {
                void OnChanged(DropDownState? snapshot) => setState(snapshot);
                DropDownServiceHost.Changed += OnChanged;
                return () => DropDownServiceHost.Changed -= OnChanged;
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
                        }),
                state is null ? Empty() : RenderList(state).WithKey("dropdown_list"),
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
                fe.Loaded -= loadedRef.Current!;
                fe.Unloaded -= unloadedRef.Current!;
            });
    }

    private static Element RenderList(DropDownState state)
    {
        var rows = new Element[state.Labels.Count];
        for (var i = 0; i < state.Labels.Count; i++)
        {
            var index = i;
            var isSelected = i == state.SelectedIndex;
            var label = state.Labels[i];

            // 选中态样式对齐原生 ComboBoxItem：左侧强调色竖条（3px 圆角短条），
            // 文字保持常规前景色。首列固定 9px 缩进（条宽 3 + 余量），选中与否文字起点一致。
            Element content = Grid(
                    [GridSize.Px(9), GridSize.Star()],
                    [GridSize.Star()],
                    [
                        (isSelected
                            ? Border(Empty())
                                .Width(3)
                                .Height(18)
                                .CornerRadius(2)
                                .Background(Theme.Accent)
                            : Empty())
                            .HAlign(HorizontalAlignment.Left)
                            .VAlign(VerticalAlignment.Center)
                            .Grid(row: 0, column: 0),
                        Body(label)
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                            .VAlign(VerticalAlignment.Center)
                            .HAlign(HorizontalAlignment.Stretch)
                            .Grid(row: 0, column: 1),
                    ])
                .VAlign(VerticalAlignment.Center);

            rows[i] = Button(content, () =>
                {
                    // 先收起再回调：页面状态更新（可能整页重渲染）时浮层已不在树上。
                    DropDownServiceHost.Close();
                    state.OnSelect(index);
                })
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .BorderThickness(new Thickness(0))
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .Padding(10, 0)
                .Height(PixelComboBox.RowHeight)
                .HAlign(HorizontalAlignment.Stretch)
                .CornerRadius(YuumiTheme.ControlRadius)
                .AutomationName(label)
                .WithKey($"dropdown_item_{index}");
        }

        Element list = Border(
                Border(
                    // 滚动条全部隐藏（用户 2026-10-29 反馈）：横向直接禁用，
                    // 纵向保留滚轮/触控滚动但不出滚动条（对齐原生 ComboBox 下拉）。
                    ScrollViewer(
                        VStack(0, rows).HAlign(HorizontalAlignment.Stretch)) with
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        HorizontalScrollMode = ScrollMode.Disabled,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    })
                    .Padding(PixelComboBox.ListPadding))
            // 不透明实色底（用户 2026-10-29 反馈）：半透明卡片底会透出页面内容，
            // 列表文字对比度不够；SolidBackgroundFillColorBase 是原生弹层同款实色底。
            .Background(Theme.Ref("SolidBackgroundFillColorBaseBrush"))
            .WithBorder(Theme.CardStroke, 1)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Width(state.Width)
            .MaxHeight(state.MaxHeight)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Top)
            .Margin(new Thickness(state.Left, state.Top, 0, 0));

        return list;
    }
}
