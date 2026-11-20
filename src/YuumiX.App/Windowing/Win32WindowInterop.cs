using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Reactor;

namespace YuumiX.App.Windowing;

/// <summary>
/// 统一封装 <c>user32.dll</c> 窗口互操作（基于 <see cref="LibraryImportAttribute"/> 源生成，兼容 Native AOT）。
/// 供 <see cref="AramBenchWindowHost"/>、<see cref="OpggWindowHost"/>、<see cref="AramDataWindowHost"/>
/// 及 <see cref="MainWindowRegistry"/> 共用，避免各窗口宿主重复声明 P/Invoke 或使用 <c>FindWindow</c>。
/// </summary>
internal static partial class Win32WindowInterop
{
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNoTopmost = new(-2);

    private const uint SpiGetWorkArea = 0x0030;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int SwShow = 5;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoNative(uint uiAction, uint uiParam, ref Win32Rect pvParam, uint fWinIni);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRectNative(nint hWnd, out Win32Rect lpRect);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static partial uint GetDpiForWindowNative(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPosNative(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindowNative(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowNative(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll", EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconicNative(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisibleNative(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowNative(nint hWnd);

    /// <summary>尝试从 <see cref="ReactorWindow"/> 获取仍有效的 Win32 HWND。</summary>
    public static bool TryGetHandle(ReactorWindow? window, out nint hwnd)
    {
        hwnd = 0;
        if (window is null)
        {
            return false;
        }

        try
        {
            var appWindow = window.AppWindow;
            if (appWindow is null)
            {
                return false;
            }

            var candidate = Win32Interop.GetWindowFromWindowId(appWindow.Id);
            if (candidate == 0 || !IsWindowNative(candidate))
            {
                return false;
            }

            hwnd = candidate;
            return true;
        }
        catch (COMException)
        {
            // 窗口正在销毁或已销毁时读取 AppWindow.Id 可能抛 COMException
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>查询主显示器工作区（不含任务栏）。</summary>
    public static bool TryGetWorkArea(out Win32Rect workArea)
    {
        workArea = default;
        return SystemParametersInfoNative(SpiGetWorkArea, 0, ref workArea, 0);
    }

    /// <summary>查询指定窗口在屏幕坐标系下的物理像素边界。</summary>
    public static bool TryGetWindowRect(nint hwnd, out Win32Rect rect)
    {
        rect = default;
        return hwnd != 0 && GetWindowRectNative(hwnd, out rect);
    }

    /// <summary>查询指定窗口的 DPI（96 为 100% 缩放）。</summary>
    public static uint GetDpiForWindow(nint hwnd) =>
        hwnd != 0 ? GetDpiForWindowNative(hwnd) : 0;

    /// <summary>切换指定窗口的置顶状态（不改变位置、尺寸，不抢焦点）。</summary>
    public static bool SetAlwaysOnTop(nint hwnd, bool alwaysOnTop)
    {
        if (hwnd == 0)
        {
            return false;
        }

        return SetWindowPosNative(
            hwnd,
            alwaysOnTop ? HwndTopmost : HwndNoTopmost,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>调整窗口位置与物理像素尺寸，并按需设置置顶状态。</summary>
    public static bool SetWindowBounds(
        nint hwnd,
        int x,
        int y,
        int width,
        int height,
        bool alwaysOnTop,
        bool preserveZOrderWhenNotTopmost = false,
        bool noActivate = true)
    {
        if (hwnd == 0)
        {
            return false;
        }

        nint insertAfter;
        uint flags = noActivate ? SwpNoActivate : 0u;
        if (alwaysOnTop)
        {
            insertAfter = HwndTopmost;
        }
        else if (preserveZOrderWhenNotTopmost)
        {
            insertAfter = 0;
            flags |= SwpNoZOrder;
        }
        else
        {
            insertAfter = HwndNoTopmost;
        }

        return SetWindowPosNative(hwnd, insertAfter, x, y, width, height, flags);
    }

    /// <summary>将指定窗口前置激活。</summary>
    public static bool BringToFront(nint hwnd) =>
        hwnd != 0 && SetForegroundWindowNative(hwnd);

    /// <summary>显示窗口（标准 SW_SHOW）。</summary>
    public static bool ShowWindow(nint hwnd) =>
        hwnd != 0 && ShowWindowNative(hwnd, SwShow);

    /// <summary>显示窗口但不抢占前台焦点（SW_SHOWNOACTIVATE）。</summary>
    public static bool ShowWindowNoActivate(nint hwnd) =>
        hwnd != 0 && ShowWindowNative(hwnd, SwShowNoActivate);

    /// <summary>隐藏窗口（SW_HIDE）。</summary>
    public static bool HideWindow(nint hwnd) =>
        hwnd != 0 && ShowWindowNative(hwnd, SwHide);

    /// <summary>判断窗口是否处于最小化状态。</summary>
    public static bool IsMinimized(nint hwnd) =>
        hwnd != 0 && IsIconicNative(hwnd);

    /// <summary>判断窗口当前是否可见。</summary>
    public static bool IsWindowVisible(nint hwnd) =>
        hwnd != 0 && IsWindowVisibleNative(hwnd);

    /// <summary>判断 HWND 是否仍指向有效窗口。</summary>
    public static bool IsWindow(nint hwnd) =>
        hwnd != 0 && IsWindowNative(hwnd);
}
