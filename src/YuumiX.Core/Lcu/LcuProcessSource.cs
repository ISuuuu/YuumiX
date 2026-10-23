using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using YuumiX.Core.Interop;

namespace YuumiX.Core.Lcu;

/// <summary>一个候选客户端进程。三个字段都可能缺失——权限、32/64 位差异、进程刚退出都会导致读不到。</summary>
public sealed record LcuProcessInfo(int ProcessId, string? ExecutablePath, string? CommandLine);

/// <summary>
/// 进程来源抽象。抽出接口的目的只有一个：让发现逻辑能在没有英雄联盟客户端的机器上被单测。
/// </summary>
public interface ILcuProcessSource
{
    /// <summary>列出当前所有 LeagueClientUx 进程（可能为空，也可能包含无权限读取的僵尸项）。</summary>
    IReadOnlyList<LcuProcessInfo> FindClientProcesses();

    /// <summary>判断进程是否存活。用于识别 lockfile 残留。</summary>
    bool IsProcessAlive(int processId);
}

/// <summary>真实实现：枚举 LeagueClientUx 进程，读可执行路径与命令行。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLcuProcessSource : ILcuProcessSource
{
    /// <summary>进程名（<see cref="Process.GetProcessesByName"/> 不接受 .exe 后缀）。</summary>
    public const string ClientProcessName = "LeagueClientUx";

    public IReadOnlyList<LcuProcessInfo> FindClientProcesses()
    {
        var processes = new List<LcuProcessInfo>();

        foreach (var process in Process.GetProcessesByName(ClientProcessName))
        {
            using (process)
            {
                processes.Add(new LcuProcessInfo(
                    process.Id,
                    TryGetExecutablePath(process),
                    ProcessCommandLine.TryGet(process.Id)));
            }
        }

        return processes;
    }

    public bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // 进程不存在
            return false;
        }
        catch (InvalidOperationException)
        {
            // 进程已退出
            return false;
        }
    }

    /// <summary>
    /// 取 exe 路径。<c>MainModule</c> 在权限不足或位数不匹配时抛异常；
    /// 这里刻意不中断整个枚举——同一个名字下可能有多个进程，其中一个读不到不代表全部无效。
    /// </summary>
    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
