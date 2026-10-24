using System.Runtime.InteropServices;
using YuumiX.Core.Common;

namespace YuumiX.Core.Automation;

/// <summary>
/// GDI BitBlt 屏幕截图（多杀截图的落盘实现，对应旧版 xcap 的 <c>Monitor.capture_image</c>）。
///
/// 落点说明（ADR-09）：全部是纯 Win32 P/Invoke（user32/gdi32），没有 WinRT 类型，
/// 因此可以留在 <c>net10.0</c> 的 Core 层并被单测覆盖到纯函数部分。
/// 简化决策：固定截取主显示器。旧版会先定位 LOL 游戏窗口所在显示器再回退主屏，
/// 需要额外枚举窗口与显示器几何计算；单屏为绝大多数玩家形态，多屏玩家可在报告的
/// 待办中跟进（游戏通常全屏在主屏）。
/// </summary>
public static class GdiScreenCapture
{
    private const int SRCCOPY = 0x00CC0020;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint DIB_RGB_COLORS = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc, IntPtr hBitmap, uint startScan, uint scanLines,
        byte[]? buffer, ref BITMAPINFO info, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;   // 负值 = 自上而下的行序
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors; // 32 位 BI_RGB 不需要调色板
    }

    /// <summary>
    /// 截取主显示器并保存为 PNG。成功返回 true；非 Windows 或 GDI 调用失败返回 false
    /// （截图是锦上添花的能力，失败只记日志不打断轮询）。
    /// </summary>
    public static bool TryCaptureToFile(string path)
    {
        try
        {
            CaptureToFile(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>截取主显示器并保存为 PNG；失败抛异常（供测试断言错误路径）。</summary>
    public static void CaptureToFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("GDI 截图仅支持 Windows");
        }

        int width = GetSystemMetrics(SM_CXSCREEN);
        int height = GetSystemMetrics(SM_CYSCREEN);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("无法获取主显示器尺寸");
        }

        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC 失败");
        }

        try
        {
            IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateCompatibleDC 失败");
            }

            IntPtr hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
            if (hBitmap == IntPtr.Zero)
            {
                DeleteDC(hdcMem);
                throw new InvalidOperationException("CreateCompatibleBitmap 失败");
            }

            try
            {
                IntPtr original = SelectObject(hdcMem, hBitmap);
                try
                {
                    if (!BitBlt(hdcMem, 0, 0, width, height, hdcScreen, 0, 0, SRCCOPY))
                    {
                        throw new InvalidOperationException("BitBlt 失败");
                    }
                }
                finally
                {
                    // GetDIBits 要求位图不在设备上下文中被选中
                    SelectObject(hdcMem, original);
                }

                var info = new BITMAPINFO();
                info.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                info.bmiHeader.biWidth = width;
                info.bmiHeader.biHeight = -height; // 负高 = 自上而下
                info.bmiHeader.biPlanes = 1;
                info.bmiHeader.biBitCount = 32;
                info.bmiHeader.biCompression = 0;  // BI_RGB

                var pixels = new byte[width * height * 4];
                if (GetDIBits(hdcMem, hBitmap, 0, (uint)height, pixels, ref info, DIB_RGB_COLORS) == 0)
                {
                    throw new InvalidOperationException("GetDIBits 失败");
                }

                File.WriteAllBytes(path, PngEncoder.Encode(pixels, width, height, width * 4));
            }
            finally
            {
                DeleteObject(hBitmap);
                DeleteDC(hdcMem);
            }
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }
}
