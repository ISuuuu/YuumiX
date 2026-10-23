using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 开发辅助工具：把目标进程窗口前置后抓取主屏幕，用于 UI 迁移过程中的视觉比对。
// 用法：dotnet run --project tools/ScreenCapture -- <输出路径> [进程名]
//   例：dotnet run --project tools/ScreenCapture -- out.png YuumiX

const int SW_RESTORE = 9;

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
    var target = Process.GetProcessesByName(processName)
        .FirstOrDefault(process => process.MainWindowHandle != IntPtr.Zero);

    if (target is null)
    {
        Console.Error.WriteLine($"未找到窗口: {processName}");
        return;
    }

    ShowWindow(target.MainWindowHandle, SW_RESTORE);
    SetForegroundWindow(target.MainWindowHandle);
}

[DllImport("user32.dll")]
static extern bool SetForegroundWindow(IntPtr hWnd);

[DllImport("user32.dll")]
static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
