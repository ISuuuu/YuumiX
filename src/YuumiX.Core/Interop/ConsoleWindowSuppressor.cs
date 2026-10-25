using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Interop;

/// <summary>
/// 布防期内「闪现即隐藏」新出现的控制台顶层窗口。
///
/// 为什么存在：观看回放（POST /lol-replays/v1/rofls/{id}/watch）由客户端进程
/// （LeagueClientUx）自己拉起游戏子进程链，链上有进程会短暂弹出一个控制台窗口
/// （旧版程序同样存在）。窗口的 CreateProcess 参数在客户端手里，无法阻止它被创建，
/// 只能在请求发出后的短暂布防期内监视并 SW_HIDE——启动器类工具的通用做法。
///
/// 安全边界：
/// 1. 只处理「布防开始后新出现」的顶层窗口（布防开始时对全量可见窗口做基线快照），
///    用户自己开着的终端不受影响；
/// 2. 只隐藏 <c>ConsoleWindowClass</c>（conhost/cmd 的窗口类），游戏本体的 GUI 窗口不会被触碰；
/// 3. 其余新窗口只写 Debug 日志不动作——若某次压制无效，日志里的 class/进程名可直接定位真凶。
/// </summary>
public static class ConsoleWindowSuppressor
{
    private const string ConsoleWindowClass = "ConsoleWindowClass";

    /// <summary>布防时长：覆盖状态查询 + 客户端拉起子进程链的整条链路。</summary>
    private static readonly TimeSpan ArmedDuration = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(30);

    private static readonly object Gate = new();
    private static Session? _session;

    /// <summary>
    /// 布防一次。布防期内重复调用只顺延截止时间、不重置基线——
    /// 一次点击会先后发出下载与观看两条请求，各自布防应合并为同一场监视。
    /// </summary>
    public static void Arm(ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (_session is { } active)
            {
                lock (active.Lock)
                {
                    if (now < active.DeadlineUtc)
                    {
                        active.DeadlineUtc = now + ArmedDuration;
                        return;
                    }
                }
            }

            var session = new Session(SnapshotVisibleWindows());
            _session = session;
            _ = Task.Run(() => RunAsync(session, logger));
        }
    }

    private static async Task RunAsync(Session session, ILogger logger)
    {
        try
        {
            while (true)
            {
                lock (session.Lock)
                {
                    if (DateTime.UtcNow >= session.DeadlineUtc)
                    {
                        return;
                    }
                }

                await Task.Delay(PollInterval).ConfigureAwait(false);

                lock (session.Lock)
                {
                    foreach (var (hwnd, className) in SnapshotVisibleWindows())
                    {
                        if (!session.Known.Add(hwnd))
                        {
                            continue;
                        }

                        string processName = ProcessNameOf(hwnd);
                        if (className == ConsoleWindowClass)
                        {
                            ShowWindow(hwnd, SwHide);
                            logger.LogInformation(
                                "已隐藏回放唤起期间弹出的控制台窗口：进程 {Process}（hwnd 0x{Hwnd:X}）",
                                processName, hwnd);
                        }
                        else
                        {
                            logger.LogDebug(
                                "回放唤起期间出现新窗口：class={Class} 进程={Process}",
                                className, processName);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "控制台窗口抑制会话异常结束");
        }
        finally
        {
            lock (Gate)
            {
                if (_session == session)
                {
                    _session = null;
                }
            }
        }
    }

    /// <summary>一次布防会话：基线窗口集 + 截止时间。监视循环与 Arm 线程经由它的锁交互。</summary>
    private sealed class Session(List<(nint Hwnd, string Class)> baseline)
    {
        public readonly object Lock = new();
        public readonly HashSet<nint> Known = new(baseline.Select(w => w.Hwnd));
        public DateTime DeadlineUtc = DateTime.UtcNow + ArmedDuration;
    }

    private static string ProcessNameOf(nint hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            // 控制台进程闪现即灭，取名字时已退出属常态，不是错误路径
            return "已退出";
        }
    }

    private static List<(nint Hwnd, string Class)> SnapshotVisibleWindows()
    {
        var windows = new List<(nint Hwnd, string Class)>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                var buffer = new StringBuilder(256);
                int length = GetClassName(hwnd, buffer, buffer.Capacity);
                windows.Add((hwnd, length > 0 ? buffer.ToString() : string.Empty));
            }

            return true;
        }, nint.Zero);
        return windows;
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int command);

    private const int SwHide = 0;
}
