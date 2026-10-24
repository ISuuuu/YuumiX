using System.Runtime.InteropServices;
using WinRT.Interop;

namespace YuumiX.App.Services;

/// <summary>
/// 原生文件夹选择服务（对应旧 <c>select_folder</c> / <c>select_lol_folder</c> 命令）。
///
/// WinUI 3 的 <see cref="Windows.Storage.Pickers.FolderPicker"/> 在非打包（WindowsPackageType=None）
/// 应用上必须先经 <c>InitializeWithWindow</c> 绑定一个窗口句柄，否则 PickSingleFolderAsync 直接抛异常。
/// 主窗口由 ReactorApp.Run 内部创建，组件树拿不到其实例；但选择器总是由 UI 线程的点击回调触发，
/// 此时 <see cref="GetActiveWindow"/> 返回的正是当前线程上激活的主窗口句柄，等价且无需全局传递 Window。
/// 已知差异：WinUI FolderPicker 没有 rfd 的 set_directory / title API，无法精确定位起始目录与标题。
/// </summary>
public static class FolderPickerService
{
    /// <summary>
    /// 打开文件夹选择对话框。
    /// </summary>
    /// <returns>用户选择的文件夹完整路径；取消或拿不到窗口句柄时为 null。</returns>
    public static async Task<string?> PickFolderAsync()
    {
        IntPtr hwnd = GetActiveWindow();
        if (hwnd == IntPtr.Zero)
        {
            // 不在 UI 线程或窗口未激活：无法初始化选择器，直接放弃而不是抛出难懂的 COM 异常
            return null;
        }

        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, hwnd);

        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
}
