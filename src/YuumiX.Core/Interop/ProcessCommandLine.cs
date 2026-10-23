using System.Runtime.InteropServices;

namespace YuumiX.Core.Interop;

/// <summary>
/// 读取任意进程的命令行。
///
/// .NET 没有托管 API 能取到「别的进程」的命令行（<see cref="System.Diagnostics.Process"/>
/// 只暴露当前进程的 <c>CommandLine</c>），因此必须下到 Win32：
/// <c>NtQueryInformationProcess(ProcessCommandLineInformation = 60)</c> 会把命令行写进
/// 调用方自己的缓冲区（不是目标进程地址空间），无需 <c>ReadProcessMemory</c>，
/// 也不需要 WMI（<c>wmic</c> 在 Windows 11 24H2 起默认禁用，原项目的方式三已不可靠，故不移植）。
///
/// 为什么这是 Core 而不是 App：P/Invoke 不属于 WinRT，<c>net10.0</c> 目标完全允许，
/// 且这段逻辑必须能被单测（见 <c>ProcessCommandLineTests</c> 对自身进程的验证）。
/// ADR-09 限制的是 UI/WinRT 类型，不是 P/Invoke。
///
/// 刻意不标 <c>[SupportedOSPlatform("windows")]</c>：方法内部已用
/// <see cref="OperatingSystem.IsWindows"/> 做了运行期守卫，非 Windows 平台返回 <c>null</c>，
/// 对调用方而言它就是全平台可用的。标上属性只会把平台守卫的责任推给每个调用点。
/// </summary>
internal static class ProcessCommandLine
{
    /// <summary>PROCESSINFOCLASS.ProcessCommandLineInformation</summary>
    private const int ProcessCommandLineInformation = 60;

    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION，Win8.1+ 即可用于取命令行。</summary>
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>读取成功时返回命令行原文；权限不足或进程已退出时返回 <c>null</c>。</summary>
    public static string? TryGet(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0)
        {
            return null;
        }

        var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            // 第一次调用只探所需长度，必然返回 STATUS_INFO_LENGTH_MISMATCH（0xC0000004），属预期。
            var status = NativeMethods.NtQueryInformationProcess(
                handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var requiredLength);

            if (requiredLength == 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal((int)requiredLength);
            try
            {
                status = NativeMethods.NtQueryInformationProcess(
                    handle, ProcessCommandLineInformation, buffer, requiredLength, out _);

                // NTSTATUS：>= 0 表示成功（0 为 STATUS_SUCCESS，正值是信息类返回）
                if (status < 0)
                {
                    return null;
                }

                return ReadUnicodeString(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// 缓冲区起始处是一个 UNICODE_STRING：{ ushort Length; ushort MaximumLength; wchar* Buffer; }。
    /// Buffer 指针指向同一块缓冲区内部（因此无需跨进程读取）。
    /// </summary>
    private static string? ReadUnicodeString(IntPtr buffer)
    {
        var lengthInBytes = (ushort)Marshal.ReadInt16(buffer);

        // 结构体内 Buffer 字段的偏移：x64/ARM64 为 8（2+2+4 字节对齐填充），x86 为 4
        var pointerOffset = IntPtr.Size == 8 ? 8 : 4;
        var stringPointer = Marshal.ReadIntPtr(buffer, pointerOffset);

        if (lengthInBytes == 0 || stringPointer == IntPtr.Zero)
        {
            return null;
        }

        return Marshal.PtrToStringUni(stringPointer, lengthInBytes / 2);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        internal static extern int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            IntPtr processInformation,
            uint processInformationLength,
            out uint returnLength);
    }
}
