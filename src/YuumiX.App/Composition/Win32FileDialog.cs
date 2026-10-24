using System.Runtime.InteropServices;

namespace YuumiX.App.Composition;

/// <summary>
/// 轻量级 Win32 通用文件对话框（P/Invoke GetOpenFileName / GetSaveFileName）。
/// 无需依赖外部包，在非打包桌面程序中跨线程、安全、稳定弹出原生文件选择对话框。
/// </summary>
public static class Win32FileDialog
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct OpenFileName
    {
        public int structSize;
        public IntPtr hwnd;
        public IntPtr hinst;
        public string? filter;
        public string? custFilter;
        public int custFilterMax;
        public int filterIndex;
        public string? file;
        public int maxFile;
        public string? fileTitle;
        public int maxFileTitle;
        public string? initialDir;
        public string? title;
        public int flags;
        public short fileOffset;
        public short fileExtMax;
        public string? defExt;
        public IntPtr custData;
        public IntPtr hook;
        public string? templateName;
        public IntPtr reservedPtr;
        public int reservedInt;
        public int flagsEx;
    }

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GetOpenFileName(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GetSaveFileName(ref OpenFileName ofn);

    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_EXPLORER = 0x00080000;

    /// <summary>
    /// 弹出打开文件对话框。
    /// </summary>
    /// <param name="title">对话框标题</param>
    /// <param name="filter">过滤器，格式如："JSON 文件 (*.json)\0*.json\0所有文件 (*.*)\0*.*\0\0"</param>
    /// <returns>用户选定的文件绝对路径，取消则为 null</returns>
    public static string? ShowOpenFileDialog(string title, string filter = "JSON 文件 (*.json)\0*.json\0所有文件 (*.*)\0*.*\0\0")
    {
        var ofn = new OpenFileName
        {
            structSize = Marshal.SizeOf<OpenFileName>(),
            filter = filter,
            file = new string(new char[1024]),
            maxFile = 1024,
            title = title,
            flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST
        };

        if (GetOpenFileName(ref ofn) && ofn.file is not null)
        {
            var idx = ofn.file.IndexOf('\0');
            return idx >= 0 ? ofn.file[..idx] : ofn.file;
        }

        return null;
    }

    /// <summary>
    /// 弹出另存为文件对话框。
    /// </summary>
    /// <param name="title">对话框标题</param>
    /// <param name="defaultFileName">默认文件名</param>
    /// <param name="filter">过滤器</param>
    /// <param name="defaultExt">默认扩展名（如 json）</param>
    /// <returns>用户选择的保存路径，取消则为 null</returns>
    public static string? ShowSaveFileDialog(
        string title,
        string defaultFileName = "tagged_players.json",
        string filter = "JSON 文件 (*.json)\0*.json\0所有文件 (*.*)\0*.*\0\0",
        string defaultExt = "json")
    {
        var ofn = new OpenFileName
        {
            structSize = Marshal.SizeOf<OpenFileName>(),
            filter = filter,
            file = defaultFileName.PadRight(1024, '\0'),
            maxFile = 1024,
            title = title,
            defExt = defaultExt,
            flags = OFN_EXPLORER | OFN_PATHMUSTEXIST | OFN_OVERWRITEPROMPT
        };

        if (GetSaveFileName(ref ofn) && ofn.file is not null)
        {
            var idx = ofn.file.IndexOf('\0');
            return idx >= 0 ? ofn.file[..idx] : ofn.file;
        }

        return null;
    }
}
