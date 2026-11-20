using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using YuumiX.App.Composition;
using YuumiX.App.Windowing;
using YuumiX.Core;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Shell;

/// <summary>
/// 主窗口生命周期与系统托盘交互辅助：关闭到托盘拦截、首次激活可见性自检、
/// 静默启动透明隐藏重试以及托盘单击/双击/右键菜单。
/// </summary>
internal static class ShellWindowLifecycle
{
    /// <summary>
    /// 挂接关闭拦截与单例唤醒：EnableCloseToTray 为 true 时隐藏窗口而不是结束进程，
    /// 后台服务（LCU 监听 / 自动化 / 上传队列）继续运行。
    /// 配置在每次关闭时实时读取，设置页改完立即生效，不依赖重渲染。
    /// </summary>
    internal static Action AttachCloseToTray(
        ReactorWindow? window,
        ReactorTrayIcon? tray,
        Ref<bool> exitRequested,
        AppServices services)
    {
        if (window is null)
        {
            return static () => { };
        }

        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        if (dispatcherQueue is not null)
        {
            AppLifetime.RegisterExitAction(() => RequestExit(window, exitRequested), dispatcherQueue);

            // 单例唤醒：重复启动的第二个实例被拦下时会点亮命名事件，由这里把主窗拉回前台。
            // 复用 ShowMainWindow 而不是另写一套 Win32 置前：窗口可能正藏在托盘里
            //（关闭到托盘），Show + Activate 与「点托盘图标」完全同一条路径，
            // 不会出现「窗口存在但没有焦点 / AppWindow 可见性与内核窗口不一致」。
            SingleInstance.RegisterActivation(() => ShowMainWindow(window), dispatcherQueue);
        }

        void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            // 关闭（含被取消的「关闭到托盘」）前先存窗口布局：这是所有退出路径上
            // 位置仍有效的最后时机——藏进托盘的窗口不会再移动，托盘「退出」
            // 在隐藏态走 ReactorApp.Exit 不再触发 Closing，靠的就是这里存下的那份。
            window.SavePlacement();

            // 托盘菜单的「退出」或外部退出（控制台 Ctrl+C / 进程关闭）必须放行，否则窗口永远关不掉。
            if (exitRequested.Current ||
                AppLifetime.IsExitRequested ||
                !services.Config.General.EnableCloseToTray.GetValueOrDefault(
                    GeneralConfig.DefaultEnableCloseToTray))
            {
                return;
            }

            // 托盘不可用（创建失败/测试环境）时宁可正常退出：
            // 藏起来的窗口没有恢复入口，比直接退出更糟。
            if (tray is null)
            {
                return;
            }

            e.Cancel = true;
            window.Hide();
        }

        MainWindowRegistry.Register(window);
        window.Closing += OnClosing;
        return () =>
        {
            MainWindowRegistry.Unregister(window);
            AppLifetime.UnregisterExitAction();
            SingleInstance.UnregisterActivation();
            window.Closing -= OnClosing;
        };
    }

    /// <summary>
    /// 还原位置可见性自检：检查窗口与各显示器的可见交集，过小（&lt; 100 DIP）则拉回主屏居中。
    ///
    /// 检查挂在首次 Activated：框架在 WM_SHOWWINDOW 才应用存档（TryApplyInitialPlacement），
    /// 组件挂载 effect 直接读位置会赶在还原之前、读到的是还原前的默认位置。
    /// 首次激活时还原已完成，时序有保证；触发一次即自摘，托盘 hide/show 的后续激活不再重复检查。
    /// </summary>
    internal static Action AttachFirstActivationVisibilityCheck(ReactorWindow? window)
    {
        if (window is null)
        {
            return static () => { };
        }

        void OnFirstActivated(object? sender, EventArgs e)
        {
            window.Activated -= OnFirstActivated;

            if (window.DipScale <= 0)
            {
                return;
            }

            var scale = window.DipScale;
            var (left, top) = window.Position;
            var width = window.AppWindow.Size.Width / scale;
            var height = window.AppWindow.Size.Height / scale;

            var maxVisibleWidth = 0d;
            var maxVisibleHeight = 0d;
            foreach (var display in ReactorDisplay.Displays)
            {
                var bounds = display.BoundsDip;
                var visibleWidth = Math.Min(left + width, bounds.X + bounds.Width) - Math.Max(left, bounds.X);
                var visibleHeight = Math.Min(top + height, bounds.Y + bounds.Height) - Math.Max(top, bounds.Y);
                maxVisibleWidth = Math.Max(maxVisibleWidth, visibleWidth);
                maxVisibleHeight = Math.Max(maxVisibleHeight, visibleHeight);
            }

            if (maxVisibleWidth < 100 || maxVisibleHeight < 100)
            {
                window.CenterOnScreen();
            }
        }

        window.Activated += OnFirstActivated;
        return () => { window.Activated -= OnFirstActivated; };
    }

    /// <summary>
    /// 静默启动（设置页「启动时最小化」）：窗口在 App.cs 以 Opacity = 0 创建，
    /// 本逻辑在窗口呈现后将其真正隐藏并还原透明度，让下次点托盘恢复时正常显示。
    /// </summary>
    internal static Action AttachSilentStartupHide(
        ReactorWindow? window,
        ReactorTrayIcon? tray,
        AppServices services)
    {
        if (window is null || !services.Config.General.EnableGameStartMinimize)
        {
            return static () => { };
        }

        var logger = services.LoggerFactory.CreateLogger("YuumiX.Startup");

        if (tray is null)
        {
            // 托盘不可用：configure 阶段可能已经把窗口藏起，而托盘是静默启动后唯一的
            // 恢复入口。此刻「窗口可见」是唯一可用的界面，抢焦点也比彻底消失可接受。
            // 先还原透明度再显示，否则用户拿到的是一块看不见的窗口。
            logger.LogWarning("静默启动：托盘不可用，恢复主窗口显示");
            try
            {
                window.SetOpacity(1);
                window.Show();
                window.Activate();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "静默启动恢复主窗口失败");
            }

            return static () => { };
        }

        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        var attempts = 0;

        // 最多重试 8 次（每次一个低优先级 tick，实测窗口在 100~800ms 内一定已呈现）。
        void HideWhenShown()
        {
            try
            {
                if (window.IsVisible)
                {
                    window.Hide();
                    // 隐藏态改透明度不会被看到，但能让下次显示恢复正常。
                    window.SetOpacity(1);
                    logger.LogInformation("静默启动：主窗口已隐藏到托盘");
                    return;
                }

                if (++attempts >= 8)
                {
                    // 一直没呈现：框架这次确实没显示窗口，退出重试。
                    // 这里**不能**还原透明度——实测框架可能在 effect 之后才显示
                    //（18:56 那次窗口在布局完成 83ms 后才被捕获），提前还原等于亲手把它变回不透明。
                    // 保持透明是安全的：任何后续显示仍会被托盘恢复路径（ShowMainWindow）还原，
                    // 而用户看不见的东西不会造成误操作。
                    logger.LogInformation("静默启动：主窗口始终未呈现，保持透明等待恢复");
                    return;
                }

                dispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, HideWhenShown);
            }
            catch (Exception ex)
            {
                // 藏不掉就把透明度还原，至少窗口还能正常用。
                logger.LogWarning(ex, "静默启动隐藏主窗口失败，保持窗口可见");
                try
                {
                    window.SetOpacity(1);
                }
                catch (Exception restoreEx)
                {
                    logger.LogWarning(restoreEx, "静默启动还原窗口透明度失败");
                }
            }
        }

        if (dispatcherQueue is null)
        {
            HideWhenShown();
            return static () => { };
        }

        dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, HideWhenShown);
        return static () => { };
    }

    /// <summary>
    /// 托盘交互：左键/双击恢复窗口，右键弹出系统原生「退出」菜单（Windows 托盘惯例）。
    /// </summary>
    internal static Action AttachTrayInteractions(
        ReactorWindow? window,
        ReactorTrayIcon? tray,
        Ref<bool> exitRequested,
        AppServices services)
    {
        if (tray is null)
        {
            return static () => { };
        }

        void Restore(object? sender, EventArgs e) => ShowMainWindow(window);

        // 托盘右键走系统原生弹出菜单（TrayMenu），不用 ShowFlyout：
        // 后者的宿主窗口尺寸不受控，单按钮会被居中在大片空白里；
        // MenuItems/MenuItem 产出的是 MenuFlyoutContentElement，又不能独立挂载（会抛异常）。
        void OpenMenu(object? sender, EventArgs e)
        {
            var logger = services.LoggerFactory.CreateLogger("YuumiX.Tray");
            try
            {
                var hwnd = window is null
                    ? IntPtr.Zero
                    : Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);

                // 诊断信息走 Debug：默认 Info 级别下不产生噪音，排查时把 LogLevel 调到 0 即可。
                logger.LogDebug("托盘右键：window={HasWindow} hwnd={Hwnd}", window is not null, hwnd);

                if (TrayMenu.ShowExitMenu(hwnd, message => logger.LogDebug("托盘菜单: {Message}", message)))
                {
                    RequestExit(window, exitRequested);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "托盘右键菜单失败");
            }
        }

        tray.Click += Restore;
        tray.DoubleClick += Restore;
        tray.RightClick += OpenMenu;
        return () =>
        {
            tray.Click -= Restore;
            tray.DoubleClick -= Restore;
            tray.RightClick -= OpenMenu;
        };
    }

    /// <summary>恢复隐藏或透明的主窗口并激活到前台。</summary>
    internal static void ShowMainWindow(ReactorWindow? window)
    {
        if (window is null)
        {
            return;
        }

        // Hide 会把窗口连同任务栏按钮一起移除；Show 之后不会自动获得焦点，必须显式 Activate。
        // 先还原透明度：静默启动的窗口以 Opacity = 0 创建，正常路径由启动 effect 还原，
        // 但任何一条没走到的路径（异常、时序变化）都不该让用户得到一块看得见边框却全透明的窗口。
        window.SetOpacity(1);
        window.Show();
        window.Activate();
    }

    /// <summary>置位退出标志并关闭主窗口（或窗口隐藏时直接结束进程）。</summary>
    internal static void RequestExit(ReactorWindow? window, Ref<bool> exitRequested)
    {
        // 先置位再关闭：Closing 里靠它放行，避免"退出"被自己的守卫取消。
        exitRequested.Current = true;
        AppLifetime.IsExitRequested = true;

        // 窗口已被隐藏（关闭到托盘）时不再"复活"它：直接请求进程退出，避免闪一帧窗口。
        if (window is null || !window.IsVisible)
        {
            ReactorApp.Exit();
            return;
        }

        window.Close();
    }
}
