using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace YuumiX.App.Composition;

/// <summary>
/// 应用进程与控制台生命周期协调。
///
/// 解决场景（开发与调试）：
/// 当在终端使用 <c>dotnet run</c> 或直接在命令行启动应用时，若开启了「关闭到托盘」，
/// 终端按下 Ctrl+C 会被默认的 <c>Window.Closing</c> 拦截（<c>dotnet run</c> 会向主窗发 WM_CLOSE，
/// 被拦截后窗口被 Hide，导致托盘残留且终端无法退出）。
///
/// 通过附着父控制台（<c>AttachConsole</c>）并注册控制台控制处理器（<c>SetConsoleCtrlHandler</c>），
/// 能够在用户按下 Ctrl+C、Ctrl+Break 或关闭控制台窗口时：
/// 1. 标记 <see cref="IsExitRequested"/> 为 true，确保 <c>OnClosing</c> 不做托盘拦截直接放行；
/// 2. 调度 UI 线程优雅关闭窗口并退出应用，确保托盘图标随进程正常退出而销毁；
/// 3. 设置短时兜底超时，确保终端必然能退回命令行。
/// </summary>
public static class AppLifetime
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    private const uint CtrlCEvent = 0;
    private const uint CtrlBreakEvent = 1;
    private const uint CtrlCloseEvent = 2;

    private delegate bool ConsoleCtrlDelegate(uint ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    // 静态持有委托引用，防止被 GC 回收导致原生回调异常
    private static readonly ConsoleCtrlDelegate ConsoleHandler = OnConsoleCtrl;

    private static ILogger? _logger;
    private static Action? _exitAction;
    private static DispatcherQueue? _dispatcherQueue;

    /// <summary>是否已收到显式退出请求（控制台 Ctrl+C / 托盘菜单退出 / 外部关闭信号）。</summary>
    public static volatile bool IsExitRequested;

    /// <summary>是否成功附着到父控制台（即从终端或 dotnet run 启动）。</summary>
    public static bool HasConsoleAttached { get; private set; }

    /// <summary>
    /// 初始化控制台生命周期监听。若进程从控制台启动，附着父控制台并监听 Ctrl+C。
    /// </summary>
    public static void InitializeConsoleLifetime(ILogger? logger)
    {
        _logger = logger;

        try
        {
            if (AttachConsole(AttachParentProcess))
            {
                HasConsoleAttached = true;
                if (SetConsoleCtrlHandler(ConsoleHandler, true))
                {
                    _logger?.LogInformation("已附着到父控制台并注册控制台事件监听（Ctrl+C 将完全退出）");
                }
                else
                {
                    _logger?.LogWarning("注册控制台控制事件处理程序失败, err={Error}", Marshal.GetLastWin32Error());
                }
            }
            else
            {
                _logger?.LogDebug("无父控制台附着（普通 GUI 启动模式）");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "初始化控制台生命周期异常");
        }
    }

    /// <summary>
    /// 注册 UI 线程的退出动作与调度器。
    /// </summary>
    public static void RegisterExitAction(Action exitAction, DispatcherQueue dispatcherQueue)
    {
        _exitAction = exitAction;
        _dispatcherQueue = dispatcherQueue;
    }

    /// <summary>
    /// 注销退出动作与调度器。
    /// </summary>
    public static void UnregisterExitAction()
    {
        _exitAction = null;
        _dispatcherQueue = null;
    }

    /// <summary>
    /// 请求退出应用。优先通过 UI 调度器派发，确保窗口与托盘正常释放。
    /// </summary>
    public static void RequestExit()
    {
        IsExitRequested = true;

        var dispatcher = _dispatcherQueue;
        var action = _exitAction;

        if (dispatcher is not null && action is not null)
        {
            try
            {
                dispatcher.TryEnqueue(DispatcherQueuePriority.High, () =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "执行 UI 退出动作失败");
                    }
                });
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "向 UI 调度器派发退出动作失败");
            }
        }
    }

    private static bool OnConsoleCtrl(uint ctrlType)
    {
        if (ctrlType is CtrlCEvent or CtrlBreakEvent or CtrlCloseEvent)
        {
            _logger?.LogInformation("收到控制台退出信号（ctrlType={CtrlType}），正在退出应用...", ctrlType);
            RequestExit();

            // 兜底超时：若 UI 主循环在 1.5 秒内因任何原因未退出，强制终止进程确保退回终端
            _ = Task.Delay(1500).ContinueWith(_ =>
            {
                _logger?.LogWarning("优雅退出超时，执行强制退出");
                Environment.Exit(0);
            });

            return true;
        }

        return false;
    }

    /// <summary>
    /// 释放控制台资源。
    /// </summary>
    public static void Shutdown()
    {
        if (HasConsoleAttached)
        {
            try
            {
                SetConsoleCtrlHandler(ConsoleHandler, false);
                FreeConsole();
            }
            catch
            {
                // 退出期忽略释放异常
            }

            HasConsoleAttached = false;
        }

        UnregisterExitAction();
    }
}
