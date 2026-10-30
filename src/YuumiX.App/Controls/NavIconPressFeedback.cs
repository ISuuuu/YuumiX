using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace YuumiX.App.Controls;

/// <summary>
/// 侧边栏菜单项的视觉辅助：定位图标宿主、向上回溯所属菜单项。
///
/// 这些能力都作用在 <see cref="NavigationView"/> 的**模板内部元素**上，
/// 只能用命令式视觉树遍历完成——Reactor 的虚拟树里没有「模板内图标宿主」这一层节点。
/// </summary>
internal static class NavItemVisuals
{
    /// <summary>
    /// 找菜单项里的图标本体（或其 <c>ContentPresenter</c> 宿主）。
    ///
    /// 三条识别路径按精确度排序：<c>IconElement</c> 本体 → 其 <c>ContentPresenter</c> 宿主
    /// （模板里叫 <c>IconPresenter</c>，但名字并非公开契约，因此再兜一层名字匹配）。
    ///
    /// 之所以不按内边距估算位置：图标在收起态（栏内居中）与展开态（16px 内边距）的位置完全不同，
    /// 任何写死的偏移都只会在其中一种状态下正确——实测展开态会偏到图标上方。
    /// </summary>
    internal static FrameworkElement? FindIconHost(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is IconElement icon)
            {
                return icon;
            }

            if (child is ContentPresenter { Content: IconElement } host)
            {
                return host;
            }

            if (child is FrameworkElement { Name: "IconPresenter" } named)
            {
                return named;
            }

            if (FindIconHost(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// 从指针事件的 <c>OriginalSource</c> 向上回溯所属的 <see cref="NavigationViewItem"/>。
    ///
    /// 命中点常常是项内部的 TextBlock 或图标本体，必须先回溯到项本身才能判断「按的是哪一项」；
    /// 回溯不到项时返回 <c>null</c>（例如按在导航栏空白处或折叠开关上）。
    /// </summary>
    internal static NavigationViewItem? FindOwningItem(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is NavigationViewItem item)
            {
                return item;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}

/// <summary>
/// 侧边栏图标的按下回弹反馈（Composition 弹簧缩放）。
///
/// 为什么需要它：WinUI 的 <see cref="NavigationViewItem"/> 模板在
/// <c>Normal / PointerOver / Pressed</c> 三态里只改图标前景色，并把
/// <c>AnimatedIcon.State</c> 附加属性同步给图标宿主——该属性只对
/// <see cref="AnimatedIcon"/> 生效，<c>FontIcon</c> / <c>ImageIcon</c> 一律无动于衷。
/// 因此无论图标是原生 Segoe 字形还是品牌位图（OP.GG），点按都没有任何反馈，
/// 只有背景色的瞬时切换。
///
/// 这里走的是与图标来源完全无关的一条路：对图标宿主元素的 Composition Visual 做
/// 弹簧缩放，按下收缩、松开回弹。原生字形与位图一视同仁，也不需要任何 Lottie 素材。
///
/// 三个刻意的取舍：
/// 1. **只动图标，不动整项**：整项缩放会让文字跟着抖，图标单独收缩才是 Fluent 的按压语感；
///    图标显示面只有 16 DIP，0.86 的收缩约 2px，足够被感知又不会「跳」。
/// 2. **用 spring 而不是关键帧**：弹簧从当前值起算，连续快速点击可被新动画自然接管，
///    不会像关键帧那样从头重放造成顿挫。
/// 3. **尊重系统「减少动画」**：关闭时直接不挂动画，图标保持原样——
///    缩放类动效对前庭敏感用户不友好，而点击本身已有背景色反馈，不依赖缩放。
/// </summary>
internal static class NavIconPressFeedback
{
    /// <summary>按下时的目标缩放。图标显示面 16 DIP，0.86 约收缩 2px。</summary>
    private const float PressedScale = 0.86f;

    /// <summary>按下收缩的弹簧周期（毫秒）。偏短，跟随手指/鼠标的按下瞬间。</summary>
    private const double PressPeriodMs = 26;

    /// <summary>松开回弹的弹簧周期（毫秒）。略长于收缩，让回弹的余韵可见。</summary>
    private const double ReleasePeriodMs = 34;

    /// <summary>
    /// 按下时的阻尼比。0.62 接近临界阻尼，几乎不过冲——按压要「跟手」，不该先弹一下。
    /// </summary>
    private const float PressDamping = 0.62f;

    /// <summary>
    /// 松开时的阻尼比。0.42 明显欠阻尼，会轻微过冲到 1 以上再落回，这正是「回弹」的来源。
    /// </summary>
    private const float ReleaseDamping = 0.42f;

    /// <summary>
    /// 已挂接的会话表。用弱引用键避免导航栏被整体重建时（语言切换会重建菜单与外壳）
    /// 把旧实例挂在静态表里。
    /// </summary>
    private static readonly ConditionalWeakTable<NavigationView, Session> Sessions = new();

    /// <summary>
    /// 给导航栏挂上按下回弹。重复调用无副作用（同一实例只挂一次）。
    ///
    /// 挂接失败只降级、不上抛：本方法在外壳挂载期执行，
    /// 让它抛异常等于「一个装饰性动效把整个侧边栏渲染打断」，代价远大于收益。
    /// </summary>
    internal static void Attach(NavigationView view, ILogger logger)
    {
        if (Sessions.TryGetValue(view, out _))
        {
            return;
        }

        var session = new Session(view, logger);
        try
        {
            session.Subscribe();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "侧边栏图标按压反馈挂接失败，本次运行不提供该反馈");
            session.Unsubscribe();
            return;
        }

        Sessions.Add(view, session);
    }

    /// <summary>摘除事件钩子。卸载时调用，避免旧导航栏继续持有委托。</summary>
    internal static void Detach(NavigationView view)
    {
        if (!Sessions.TryGetValue(view, out var session))
        {
            return;
        }

        Sessions.Remove(view);
        session.Unsubscribe();
    }

    /// <summary>系统是否允许播放动画（设置 → 辅助功能 → 视觉效果 → 动画效果）。</summary>
    private static bool AnimationsEnabled
    {
        get
        {
            try
            {
                return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
            }
            catch
            {
                // 极少数环境下无障碍查询不可用：按「不允许动画」处理，最保守。
                return false;
            }
        }
    }

    /// <summary>
    /// 一次挂接的生命周期。事件委托必须是跨调用稳定的同一实例（否则无法摘除），
    /// 因此全部在构造时创建一次。
    /// </summary>
    private sealed class Session
    {
        private readonly NavigationView _view;
        private readonly ILogger _logger;
        private readonly PointerEventHandler _onPressed;
        private readonly PointerEventHandler _onReleased;

        /// <summary>
        /// 当前处于按下态的图标宿主。指针在项之间快速移动、或按下后拖出再松开时，
        /// 靠它保证「上一个被缩小的图标」一定被复位，不会卡在 0.86。
        /// </summary>
        private FrameworkElement? _activeIcon;

        /// <summary>
        /// 图标宿主 → Composition 视觉的缓存。见 <see cref="GetVisual"/>：同一元素不允许
        /// 第二次 <c>GetElementVisual</c>，必须复用首次取得的实例。
        /// </summary>
        private readonly ConditionalWeakTable<FrameworkElement, Visual> _visuals = new();

        internal Session(NavigationView view, ILogger logger)
        {
            _view = view;
            _logger = logger;
            _onPressed = OnPressed;
            _onReleased = OnReleased;
        }

        internal void Subscribe()
        {
            // handledEventsToo: true —— NavigationViewItem 自己会处理并标记 PointerPressed/Released，
            // 用普通事件订阅收不到；这里只需要观察，不参与命中处理。
            _view.AddHandler(UIElement.PointerPressedEvent, _onPressed, handledEventsToo: true);
            _view.AddHandler(UIElement.PointerReleasedEvent, _onReleased, handledEventsToo: true);
            _view.AddHandler(UIElement.PointerCaptureLostEvent, _onReleased, handledEventsToo: true);
            _view.AddHandler(UIElement.PointerCanceledEvent, _onReleased, handledEventsToo: true);
        }

        internal void Unsubscribe()
        {
            _view.RemoveHandler(UIElement.PointerPressedEvent, _onPressed);
            _view.RemoveHandler(UIElement.PointerReleasedEvent, _onReleased);
            _view.RemoveHandler(UIElement.PointerCaptureLostEvent, _onReleased);
            _view.RemoveHandler(UIElement.PointerCanceledEvent, _onReleased);
            _activeIcon = null;
        }

        private void OnPressed(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (!AnimationsEnabled)
                {
                    return;
                }

                if (NavItemVisuals.FindOwningItem(e.OriginalSource as DependencyObject) is not { } item)
                {
                    return;
                }

                if (NavItemVisuals.FindIconHost(item) is not { ActualWidth: > 0, ActualHeight: > 0 } icon)
                {
                    return;
                }

                // 上一次按下若没收到松开（例如指针被别处捕获），先把它复位再缩新图标。
                if (_activeIcon is { } previous && !ReferenceEquals(previous, icon))
                {
                    StartScale(previous, 1f, ReleaseDamping, ReleasePeriodMs);
                }

                _activeIcon = icon;
                StartScale(icon, PressedScale, PressDamping, PressPeriodMs);
            }
            catch (Exception ex)
            {
                // 动画只是锦上添花：合成器在极端环境下可能不可用，
                // 此时放弃这一次反馈即可，不能因为「图标不弹」打断导航。
                _logger.LogDebug(ex, "侧边栏图标按下动画启动失败，本次点击不做缩放反馈");
            }
        }

        private void OnReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_activeIcon is not { } icon)
            {
                return;
            }

            _activeIcon = null;

            try
            {
                if (!AnimationsEnabled)
                {
                    // 按下时已禁用动画就不会有残留缩放，这里无需恢复。
                    return;
                }

                StartScale(icon, 1f, ReleaseDamping, ReleasePeriodMs);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "侧边栏图标回弹动画启动失败，图标保持当前缩放");
            }
        }

        /// <summary>
        /// 启动一次弹簧缩放。
        ///
        /// 三个必须同时成立的细节（每一条都是实测踩出来的，改动前务必读完）：
        /// 1. **中心点只能设在 Composition 视觉上，绝不能用 <see cref="UIElement.CenterPoint"/>**：
        ///    一旦设置该 XAML 属性，框架即「收回」该元素的视觉访问权，
        ///    后续 <c>ElementCompositionPreview.GetElementVisual</c> 会抛
        ///    「Access denied … this object currently has the CenterPoint property in use」，
        ///    而该异常被本类的 catch 降级吞掉 → 表现为「点了完全没反应」。
        /// 2. **Visual 按元素缓存**：不缓存的话每次按下都要重新 <c>GetElementVisual</c>，
        ///    在上一条的语境下（元素已设过 CenterPoint）第二次就会失败。
        /// 3. **中心点每次重算**：图标宿主尺寸随 DPI 变化，沿用旧值会让图标偏出 1px。
        ///
        /// 弹簧动画从「当前值」起算，因此不需要写 <c>InitialValue</c>。
        /// </summary>
        private void StartScale(FrameworkElement host, float target, float damping, double periodMs)
        {
            var visual = GetVisual(host);

            float width = (float)host.ActualWidth;
            float height = (float)host.ActualHeight;
            visual.CenterPoint = new Vector3(width / 2f, height / 2f, 0f);

            var spring = visual.Compositor.CreateSpringVector3Animation();
            spring.Target = "Scale";
            spring.FinalValue = new Vector3(target, target, 1f);
            spring.DampingRatio = damping;
            spring.Period = TimeSpan.FromMilliseconds(periodMs);

            visual.StartAnimation("Scale", spring);
        }

        /// <summary>
        /// 取（并缓存）元素的 Composition 视觉。缓存键用弱引用：菜单项在语言切换、
        /// 可见性配置变化时会被整批重建，旧图标不该被静态表留住。
        /// </summary>
        private Visual GetVisual(FrameworkElement host)
        {
            if (_visuals.TryGetValue(host, out var cached))
            {
                return cached;
            }

            var visual = ElementCompositionPreview.GetElementVisual(host);
            _visuals.Add(host, visual);
            return visual;
        }
    }
}
