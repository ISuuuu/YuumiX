using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Navigation;
using YuumiX.App.Theming;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Shell;

/// <summary>
/// 侧边栏「设置」项更新红点与更新通知提示：
/// 订阅更新状态、查找设置菜单项容器、自绘 4px 覆盖圆点定位、呼吸动画及无障碍提示。
/// </summary>
internal static class ShellUpdateBadge
{
    /// <summary>
    /// 更新提示红点的直径（DIP）。
    ///
    /// 压在图标右上角的角标，尺寸只在「看得见」与「不抢戏」之间取窄带：
    /// 4 对应 16px 图标的 1/4，比系统 InfoBadge 的 dot 更小、更收敛。
    /// 调这一个常量即可整体缩放。
    /// </summary>
    internal const double UpdateDotSize = 4;

    /// <summary>
    /// 订阅更新通知状态：处理上次更新失败的一次性 Toast 提示、同步红点可见性，
    /// 并在首次发现新版本时弹出 info Toast（同版本不重复打扰）。
    /// </summary>
    internal static Action AttachUpdateNotice(
        UpdateNoticeState updateNotice,
        Ref<string?> notifiedVersion,
        Action<bool> setUpdateAvailable,
        ToastQueue toastQueue,
        ILogger logger)
    {
        // 上次更新未完成的提示：只弹一次（状态对象读后即清），与「发现新版本」提示互不影响。
        // 这条提示是失败路径上唯一的反馈——脚本执行时主进程已经退出，用户只会看到
        // 「应用关了、版本没变」，没有这条 Toast 就只能自己猜。
        if (updateNotice.FailureCode is { } failureCode)
        {
            updateNotice.ClearFailure();
            logger.LogInformation("已提示上次更新未完成：{Code}", failureCode);
            toastQueue.Error($"上次更新未完成：{DescribeUpdateFailure(failureCode)}，可在「设置 → 软件更新」重试");
        }

        void Apply(UpdateNoticeState state)
        {
            setUpdateAvailable(state.HasUpdate);

            if (state.Version is not { } version || notifiedVersion.Current == version)
            {
                return;
            }

            notifiedVersion.Current = version;
            toastQueue.Info($"发现新版本 v{version}，可在「设置 → 通用设置 → 软件更新」中查看");
        }

        // 先按当前快照执行一次：启动检查通常早于本组件挂载，
        // 漏掉这一次的话下一次提示要等周期复查（最长 6 小时）。
        Apply(updateNotice);
        var subscription = updateNotice.Subscribe(Apply);
        return () => subscription.Dispose();
    }

    /// <summary>
    /// 构造覆盖在导航栏之上的红点 Canvas：元素常驻、仅切换可见性，
    /// 通过 LayoutUpdated 命令式定位以避免导航栏展开动画期间触发整个外壳重渲染。
    /// </summary>
    internal static Element BuildOverlay(
        bool updateAvailable,
        Ref<NavigationView?> navViewRef,
        Ref<FrameworkElement?> updateDotRef,
        Ref<Canvas?> updateOverlayRef,
        EventHandler<object> layoutHandler,
        ILogger logger)
    {
        var dot = Border(Empty())
            .Width(UpdateDotSize)
            .Height(UpdateDotSize)
            .CornerRadius(YuumiTheme.PillRadius)
            .Background(Theme.SystemCritical)
            .IsHitTestVisible(false)
            // 元素常驻、只切可见性：条件渲染（有更新 ? 红点 : Empty）会在两种元素类型之间切换，
            // 每次都让 Reactor 重建 Canvas 并重挂 LayoutUpdated；隐藏一个已有元素没有这个代价。
            // 呼吸动画在不可见期间继续跑，合成层开销可忽略，换来的是「显示时立刻处于当前相位」。
            .IsVisible(updateAvailable)
            .OnMountAdd(element =>
            {
                updateDotRef.Current = (FrameworkElement)element;
                StartBreathing((FrameworkElement)element, logger);
                PositionUpdateDot(navViewRef.Current, updateDotRef.Current, updateOverlayRef.Current, logger);
            })
            .OnUnmountAdd(_ => updateDotRef.Current = null);

        return Canvas([dot])
            .IsHitTestVisible(false)
            .OnMountAdd(element =>
            {
                updateOverlayRef.Current = (Canvas)element;
                // LayoutUpdated 覆盖所有会移动「设置」项的情况：导航栏展开/收起、窗口缩放、
                // 菜单项因可见性配置重建。回调内部先比较坐标，因此不会因自身的 SetLeft 反复触发。
                element.LayoutUpdated += layoutHandler;
                PositionUpdateDot(navViewRef.Current, updateDotRef.Current, updateOverlayRef.Current, logger);
            })
            .OnUnmountAdd(element =>
            {
                element.LayoutUpdated -= layoutHandler;
                updateOverlayRef.Current = null;
            });
    }

    /// <summary>
    /// 按「设置」菜单项图标的实测坐标定位覆盖圆点。
    /// 定位走命令式（Canvas.SetLeft/Top）而非 state，避免动画期高频重渲染。
    /// </summary>
    internal static void PositionUpdateDot(
        NavigationView? view,
        FrameworkElement? dot,
        Canvas? canvas,
        ILogger logger)
    {
        if (dot is null || canvas is null || view is null)
        {
            return;
        }

        var item = FindSettingsMenuItem(view);
        if (item is null)
        {
            return;
        }

        double left;
        double top;

        // 优先直接取图标本体的实测角点：
        // 收起/展开两种状态的内边距完全不同，用固定数字估会在某一种状态下偏到图标上方。
        if (NavItemVisuals.FindIconHost(item) is { ActualWidth: > 0 } icon)
        {
            Point corner;
            try
            {
                corner = icon.TransformToVisual(canvas).TransformPoint(new Point(icon.ActualWidth, 0));
            }
            catch (ArgumentException)
            {
                return;
            }

            // 圆点中心对准图标右上角顶点：一半压在角上、一半探出图标外，
            // 这是系统角标的常见位置，也避免整颗点漂在图标上方。
            left = corner.X - UpdateDotSize / 2;
            top = corner.Y - UpdateDotSize / 2;
        }
        else
        {
            Point origin;
            try
            {
                origin = item.TransformToVisual(canvas).TransformPoint(new Point(0, 0));
            }
            catch (ArgumentException)
            {
                // 两者尚未处于同一棵视觉树（模板重建的瞬间）：等下一次布局回调即可，不必报错。
                return;
            }

            // 模板结构变化导致找不到图标时的兜底：按已知的内边距估算
            //（收起态图标在 64px 栏内居中，展开态起于 16px 内边距）。
            double iconRight = view.IsPaneOpen ? 32 : 40;
            left = origin.X + iconRight - UpdateDotSize;
            top = origin.Y + 8;
        }

        if (Math.Abs(Microsoft.UI.Xaml.Controls.Canvas.GetLeft(dot) - left) < 0.5
            && Math.Abs(Microsoft.UI.Xaml.Controls.Canvas.GetTop(dot) - top) < 0.5)
        {
            return;
        }

        // 走 Debug：只在坐标真的变化时输出，排查"点跑到别处"时把日志级别调到 0 即可。
        logger.LogDebug(
            "更新红点定位：left={Left} top={Top} paneOpen={Open} canvas={CW}x{CH}",
            left, top, view.IsPaneOpen, canvas.ActualWidth, canvas.ActualHeight);

        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(dot, left);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(dot, top);
    }

    /// <summary>
    /// 给「设置」菜单项补上无障碍提示与悬停说明。
    ///
    /// 红点是纯视觉元素（<c>IsHitTestVisible=false</c>，不在无障碍树里），
    /// 屏幕阅读器与悬停提示都读不到它，所以「有新版本 vX」必须挂到菜单项本身。
    /// </summary>
    internal static void ApplyUpdateHint(NavigationView view, bool available, string? version)
    {
        var item = FindSettingsMenuItem(view);
        if (item is null)
        {
            return;
        }

        string helpText = !available
            ? string.Empty
            : string.IsNullOrEmpty(version) ? "有新版本可用" : $"有新版本 v{version} 可用";

        AutomationProperties.SetHelpText(item, helpText);
        ToolTipService.SetToolTip(item, helpText.Length == 0 ? null : helpText);
    }

    /// <summary>
    /// 把更新失败原因码翻成人话。
    ///
    /// 原因码由延迟脚本写入（英文，避免 cmd 代码页问题），中文只在 UI 层做，
    /// 这样脚本改动不会牵动文案，文案调整也不用动脚本。
    /// </summary>
    private static string DescribeUpdateFailure(string code) => code switch
    {
        YuumiX.Core.Updates.UpdateService.FailureCodeWaitExit => "应用未能在超时前退出，替换已放弃",
        YuumiX.Core.Updates.UpdateService.FailureCodeCopyFailed => "文件替换中断，程序可能不完整",
        YuumiX.Core.Updates.UpdateService.FailureCodeSetupFailed => "安装程序返回失败",
        _ => "原因未知",
    };

    /// <summary>
    /// 找到底部操作区「设置」项对应的原生容器。
    ///
    /// 优先按 Tag 匹配：Reactor 把 <c>NavigationViewItemData.Tag</c> 映射到原生
    /// <c>NavigationViewItem.Tag</c>，而 <see cref="RouteCatalog.Footer"/> 的 Key 是稳定字符串。
    /// Tag 未透传时（框架映射方式变化）再按文案兜底——文案取自路由目录，不在这里写死，
    /// 菜单改名时不会静默失配。
    /// </summary>
    private static NavigationViewItem? FindSettingsMenuItem(NavigationView view)
    {
        var descriptor = RouteCatalog.Footer[0];

        foreach (var entry in view.FooterMenuItems)
        {
            if (entry is NavigationViewItem item
                && string.Equals(item.Tag as string, descriptor.Key, StringComparison.Ordinal))
            {
                return item;
            }
        }

        foreach (var entry in view.FooterMenuItems)
        {
            if (entry is NavigationViewItem item
                && string.Equals(item.Content as string, descriptor.Label, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>
    /// 启动红点呼吸动画：1.0 → 0.35 → 1.0，2 秒一轮，无限循环。
    ///
    /// 两端与中点都用同一条缓入缓出曲线，亮度变化比线性更「像呼吸」；
    /// 时长定 2 秒是因为更短的周期在余光里会显得焦躁（接近闪烁告警），更长则容易被忽略。
    ///
    /// 系统开启「减少动画」（设置 → 辅助功能 → 视觉效果）时常亮不呼吸：
    /// 持续明暗变化对前庭敏感用户不友好，而红点本身的可见性并不依赖动画。
    /// </summary>
    private static void StartBreathing(FrameworkElement target, ILogger logger)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(target);

            if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                visual.Opacity = 1f;
                return;
            }

            var compositor = visual.Compositor;
            var easing = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.4f, 0f),
                new System.Numerics.Vector2(0.6f, 1f));

            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0f, 1f, easing);
            animation.InsertKeyFrame(0.5f, 0.35f, easing);
            animation.InsertKeyFrame(1f, 1f, easing);
            animation.Duration = TimeSpan.FromSeconds(2);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;

            visual.StartAnimation("Opacity", animation);
        }
        catch (Exception ex)
        {
            // 动画只是锦上添花：合成器/无障碍查询在极端环境下可能不可用，
            // 此时红点保持常亮即可——不能因为「灯不呼吸」把整个外壳渲染打断。
            logger.LogWarning(ex, "更新红点的呼吸动画启动失败，已降级为常亮");
            target.Opacity = 1;
        }
    }
}
