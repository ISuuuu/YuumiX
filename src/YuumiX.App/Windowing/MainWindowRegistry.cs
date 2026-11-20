using Microsoft.UI.Reactor;

namespace YuumiX.App.Windowing;

/// <summary>
/// 主窗口实例注册表：供子窗口宿主（如 <see cref="AramDataWindowHost"/>）读取主窗口 HWND，
/// 替代按标题硬编码的 <c>FindWindow(null, "YuumiX")</c>。
/// </summary>
public static class MainWindowRegistry
{
    private static readonly Lock Gate = new();
    private static WeakReference<ReactorWindow>? _mainWindow;

    /// <summary>注册当前主窗口实例。</summary>
    public static void Register(ReactorWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (Gate)
        {
            _mainWindow = new WeakReference<ReactorWindow>(window);
        }
    }

    /// <summary>注销主窗口实例（仅当传入实例与当前已注册实例一致时清空）。</summary>
    public static void Unregister(ReactorWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (Gate)
        {
            if (_mainWindow is not null &&
                _mainWindow.TryGetTarget(out var current) &&
                ReferenceEquals(current, window))
            {
                _mainWindow = null;
            }
        }
    }

    /// <summary>尝试获取已注册主窗口的有效原生句柄（HWND）。</summary>
    public static bool TryGetHandle(out nint hwnd)
    {
        ReactorWindow? window;
        lock (Gate)
        {
            if (_mainWindow is null || !_mainWindow.TryGetTarget(out window))
            {
                _mainWindow = null;
                hwnd = 0;
                return false;
            }
        }

        return Win32WindowInterop.TryGetHandle(window, out hwnd);
    }
}
