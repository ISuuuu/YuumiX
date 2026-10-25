using System.Runtime.InteropServices;

namespace YuumiX.App.Windowing;

/// <summary>
/// 托盘右键菜单（原生 Win32 弹出菜单）。
///
/// 为什么不用 <c>ReactorTrayIcon.ShowFlyout</c>：它把传入的元素挂载进一个无边框宿主窗口
/// （<c>TrayFlyoutHostWindow</c>），窗口尺寸由该宿主决定、调用方无法控制；单个按钮落在大窗口里
/// 被垂直居中，表现为"内容很大、退出在中间、周围一片空白"。
/// <c>MenuFlyoutContentElement</c>（<c>MenuItems</c>/<c>MenuItem</c>）又不能直接喂给 ShowFlyout
/// （Reactor 明确它 NOT independently mountable），所以 Reactor 侧没有合适的托盘菜单形态。
///
/// 原生 <c>TrackPopupMenuEx</c> 的尺寸、字体、主题、悬停反馈全部由系统决定，与资源管理器、
/// 输入法等系统托盘菜单完全一致，是托盘菜单的正确形态。
///
/// 采用 <c>TPM_RETURNCMD</c>：菜单是模态循环，选中的命令 id 同步返回，
/// 因此不需要窗口过程、也不需要 WM_COMMAND 路由。
/// </summary>
internal static class TrayMenu
{
    private const uint MfString = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmBottomAlign = 0x0020;
    private const uint TpmNoNotify = 0x0080;
    private const uint TpmReturnCmd = 0x0100;
    private const uint WmNull = 0x0000;

    private const int CommandExit = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIdNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 在光标处弹出只含「退出」一项的托盘菜单。返回 <c>true</c> 表示用户选择了退出。
    /// </summary>
    /// <param name="ownerWindow">属主窗口 HWND（主窗口）。为零时才退回前台窗口。</param>
    /// <param name="trace">诊断回调：记录每一步的原生返回值，便于定位"菜单不出现"。</param>
    public static bool ShowExitMenu(IntPtr ownerWindow, Action<string>? trace = null)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            trace?.Invoke($"CreatePopupMenu 失败, err={Marshal.GetLastWin32Error()}");
            return false;
        }

        try
        {
            var appended = AppendMenuW(menu, MfString, CommandExit, "退出 YuumiX");
            var appendedError = Marshal.GetLastWin32Error();

            var hasCursor = GetCursorPos(out var cursor);
            if (!hasCursor)
            {
                cursor.X = 0;
                cursor.Y = 0;
            }

            // 优先用自己进程的窗口作属主：跨进程属主（前台窗口常是 explorer）在某些
            // 前台锁定状态下会让 TrackPopupMenuEx 立即返回 0，表现为「右键没反应」。
            var owner = ownerWindow != IntPtr.Zero ? ownerWindow : GetForegroundWindow();
            var foregroundSet = owner != IntPtr.Zero && SetForegroundWindow(owner);

            trace?.Invoke(
                $"append={appended}(err={appendedError}) cursor={cursor.X},{cursor.Y}(ok={hasCursor}) " +
                $"owner={owner} setForeground={foregroundSet}(err={Marshal.GetLastWin32Error()})");

            if (!appended || owner == IntPtr.Zero)
            {
                return false;
            }

            // 托盘菜单的经典前置条件：先把属主推到前台再弹，否则点空白处菜单不会消失。
            var command = TrackPopupMenuEx(
                menu,
                TpmRightButton | TpmBottomAlign | TpmNoNotify | TpmReturnCmd,
                cursor.X,
                cursor.Y,
                owner,
                IntPtr.Zero);

            var trackError = Marshal.GetLastWin32Error();
            trace?.Invoke($"TrackPopupMenuEx 返回 {command}, err={trackError}");

            // 菜单关闭后补一条空消息，释放前台锁定（Raymond Chen 的经典 workaround）。
            PostMessageW(owner, WmNull, IntPtr.Zero, IntPtr.Zero);

            return command == CommandExit;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }
}
