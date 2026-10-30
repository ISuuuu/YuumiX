using System.Runtime.InteropServices;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using Windows.Foundation;
using YuumiX.App.Composition;
using YuumiX.App.Pages.Opgg;
using YuumiX.App.Theming;

namespace YuumiX.App.Windowing;

/// <summary>
/// 管理 OP.GG 独立窗口的单例创建、聚焦、右侧坐标定位、AlwaysOnTop 动态同步与生命周期。
/// </summary>
public static class OpggWindowHost
{
    public const string Title = "OP.GG 英雄梯度与出装推荐";
    public const double DefaultWidth = 760;
    public const double DefaultHeight = 820;

    private static readonly Lock _syncLock = new();
    private static bool _isOpen;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNoactivate = 0x0010;
    private const uint SpiGetWorkArea = 0x0030;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref Rect pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// 打开或聚焦已有的 OP.GG 独立窗口。
    /// </summary>
    public static void ShowOrActivate(int? initialChampionId = null)
    {
        lock (_syncLock)
        {
            var existingHwnd = FindWindow(null, Title);
            if (existingHwnd != IntPtr.Zero && _isOpen)
            {
                SetForegroundWindow(existingHwnd);
                if (AppHost.Services.Config.Functions.EnableOpggOnTop)
                {
                    UpdateAlwaysOnTop(true);
                }
                return;
            }

            // 计算屏幕右侧坐标：x = monitorRight - 760 - 2, y = monitorTop + 2
            double posX = 100;
            double posY = 100;
            var startPos = WindowStartPosition.CenterOnCurrent;

            var workArea = new Rect();
            if (SystemParametersInfo(SpiGetWorkArea, 0, ref workArea, 0))
            {
                posX = Math.Max(0, workArea.Right - DefaultWidth - 2);
                posY = Math.Max(0, workArea.Top + 2);
                startPos = WindowStartPosition.Manual;
            }

            var spec = new WindowSpec
            {
                Title = Title,
                Width = DefaultWidth,
                Height = DefaultHeight,
                MinWidth = 600,
                MinHeight = 500,
                StartPosition = startPos,
                ManualPosition = startPos == WindowStartPosition.Manual ? (posX, posY) : null,
                Backdrop = BackdropChoice.Of(BackdropKind.Mica),
                Icon = File.Exists(AppAssets.AppIconPath) ? WindowIcon.FromPath(AppAssets.AppIconPath) : null,
            };

            _isOpen = true;

            ReactorApp.OpenWindow(spec, () =>
            {
                return new OpggWindowRoot(initialChampionId);
            });

            // 窗口打开后延迟微调置顶（如果配置开启）
            if (AppHost.Services.Config.Functions.EnableOpggOnTop)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    UpdateAlwaysOnTop(true);
                });
            }
        }
    }

    /// <summary>
    /// 动态更新 OP.GG 窗口的 AlwaysOnTop（置顶）状态。
    /// </summary>
    public static void UpdateAlwaysOnTop(bool alwaysOnTop)
    {
        var hwnd = FindWindow(null, Title);
        if (hwnd == IntPtr.Zero) return;

        var insertAfter = alwaysOnTop ? HwndTopmost : HwndNoTopmost;
        SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
    }

    /// <summary>
    /// 窗口关闭时重置单例标记。
    /// </summary>
    internal static void NotifyClosed()
    {
        lock (_syncLock)
        {
            _isOpen = false;
        }
    }
}

/// <summary>
/// OP.GG 窗口根容器，提供 AppHost.Context 与 ThemeContext。
/// </summary>
internal sealed class OpggWindowRoot(int? initialChampionId) : Component
{
    public override Element Render()
    {
        // 本窗口独立的 Toast 队列（R5）：与主窗口互不串扰；UseRef 缓存保证跨渲染同实例。
        var toastQueueRef = UseRef<ToastQueue?>(null);
        var toastQueue = toastQueueRef.Current ??= new ToastQueue();

        // OP.GG 是独立窗口，视觉树与主窗口不相通：AppShell 的主题订阅与 RequestedTheme
        // 都只覆盖主窗口根元素。这里照搬 AppShell 的三件套（AGENTS.md §8），独立窗口才能跟随深浅：
        // 1) 订阅 ThemeState → 设置页改浅色/深色后 setPalette 触发本窗口重渲染；
        // 2) 根元素 .RequestedTheme(palette.ElementTheme) → 固定模式下不再停在启动主题；
        // 3) ActualThemeChanged 写回 ThemeState → 跟随系统模式下运行中切 Windows 深浅重算业务色。
        // threadSafe: true —— 推送可能来自保存完成的回调线程（ADR-07）。
        var (palette, setPalette) = UseState(AppHost.Theme.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = AppHost.Theme.Subscribe(setPalette);
                return () => subscription.Dispose();
            },
            0);

        // 委托必须跨渲染保持同一实例（AGENTS.md §5.7），否则 -= 摘不掉。
        var themeChangedHandler = UseRef<TypedEventHandler<FrameworkElement, object>?>(null);
        themeChangedHandler.Current ??= (sender, _) => AppHost.Theme.UpdateActualTheme(sender.ActualTheme);

        UseEffect(() =>
        {
            return () =>
            {
                OpggWindowHost.NotifyClosed();
            };
        }, []);

        var root = Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                [
                    Component<OpggPage, OpggPageProps>(new OpggPageProps(initialChampionId)),
                    // 本窗口自己的 Toast 宿主：OpggPage 的成功/失败提示（应用符文页、应用出装）
                    // 都走 IToastService，之前只 Provide 了队列却没有宿主，那些提示全部石沉大海。
                    // 宿主压在页面内容之上，与主窗口同一套视觉。
                    Component<ToastHost>(),
                ])
            .RequestedTheme(palette.ElementTheme)
            .Provide(AppHost.Context, AppHost.Services)
            .Provide(AppHost.ThemeContext, AppHost.Theme)
            .Provide(AppHost.ToastContext, toastQueue);

        // 挂载即同步一次实际主题（同 AppShell）：若应用运行期间系统已切过深浅，这里立即校准。
        // 「固定主题」模式由 UpdateActualTheme 内部守卫忽略，不会污染调色板。
        root = root
            .OnMountAdd(element =>
            {
                var fe = (FrameworkElement)element;
                fe.ActualThemeChanged += themeChangedHandler.Current!;
                AppHost.Theme.UpdateActualTheme(fe.ActualTheme);
            })
            .OnUnmountAdd(element =>
                ((FrameworkElement)element).ActualThemeChanged -= themeChangedHandler.Current!);

        return root;
    }
}
