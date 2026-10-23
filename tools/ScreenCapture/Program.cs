using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 开发辅助工具：把目标进程窗口前置后抓取主屏幕，用于 UI 迁移过程中的视觉比对。
// 用法：ScreenCapture.exe <输出路径> [进程名]
//   例：ScreenCapture.exe out.png YuumiX

const int SW_RESTORE = 9;
const uint SWP_NOSIZE = 0x0001;
const uint SWP_SHOWWINDOW = 0x0040;

var outputPath = args.Length > 0
    ? args[0]
    : Path.Combine(Path.GetTempPath(), "screen.png");

if (args.Length > 1)
{
    BringToForeground(args[1]);
    Thread.Sleep(700); // 等待窗口完成前置动画与重绘
}

var bounds = Screen.PrimaryScreen!.Bounds;
using var bitmap = new Bitmap(bounds.Width, bounds.Height);
using var graphics = Graphics.FromImage(bitmap);
graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bitmap.Size);

bitmap.Save(outputPath, ImageFormat.Png);
Console.WriteLine(outputPath);

void BringToForeground(string processName)
{
    var processes = Process.GetProcessesByName(processName);
    if (processes.Length == 0)
    {
        Console.Error.WriteLine($"未找到进程: {processName}");
        return;
    }

    var candidates = new List<(IntPtr Handle, long Area)>();

    foreach (var process in processes)
    {
        var handle = process.MainWindowHandle;
        if (handle != IntPtr.Zero)
        {
            candidates.Add((handle, AreaOf(handle)));
        }
    }

    // WinUI 3 的主窗口经常拿不到 Process.MainWindowHandle（返回 0），
    // 此时退化为按 PID 枚举顶层可见窗口，取面积最大的那个。
    if (candidates.Count == 0)
    {
        var processIds = processes.Select(process => (uint)process.Id).ToHashSet();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var ownerId);
            if (processIds.Contains(ownerId) && IsWindowVisible(hwnd))
            {
                var area = AreaOf(hwnd);
                if (area > 40_000) // 过滤掉工具提示、阴影等小窗
                {
                    candidates.Add((hwnd, area));
                }
            }

            return true;
        }, IntPtr.Zero);
    }

    if (candidates.Count == 0)
    {
        Console.Error.WriteLine($"未找到窗口: {processName}");
        return;
    }

    var target = candidates.OrderByDescending(candidate => candidate.Area).First().Handle;

    ShowWindow(target, SW_RESTORE);

    // 顺手搬到主屏左上角：即使系统拒绝抢焦点，截图里也一定能拍到。
    SetWindowPos(target, IntPtr.Zero, 80, 60, 0, 0, SWP_NOSIZE | SWP_SHOWWINDOW);
    SetForegroundWindow(target);
    Thread.Sleep(400);
}

static long AreaOf(IntPtr hwnd)
{
    if (!GetWindowRect(hwnd, out var rect))
    {
        return 0;
    }

    return (long)Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);
}

[DllImport("user32.dll")]
static extern bool SetForegroundWindow(IntPtr hWnd);

[DllImport("user32.dll")]
static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

[DllImport("user32.dll")]
static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

[DllImport("user32.dll")]
static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

[DllImport("user32.dll")]
static extern bool IsWindowVisible(IntPtr hWnd);

[DllImport("user32.dll")]
static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

[DllImport("user32.dll")]
static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

[StructLayout(LayoutKind.Sequential)]
struct Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
