using Xunit;
using YuumiX.Core.Interop;

namespace YuumiX.Core.Tests;

/// <summary>
/// Win32 命令行读取。这段代码没有 mock 价值，唯一的验证方式是对自身进程取一次：
/// 能读到自己的命令行，就说明 P/Invoke 签名、缓冲区布局、UNICODE_STRING 偏移都正确。
/// </summary>
public sealed class ProcessCommandLineTests
{
    [Fact]
    public void TryGet_ReadsOwnCommandLine()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var commandLine = ProcessCommandLine.TryGet(Environment.ProcessId);

        Assert.False(string.IsNullOrWhiteSpace(commandLine));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TryGet_InvalidProcessId_ReturnsNull(int processId)
    {
        Assert.Null(ProcessCommandLine.TryGet(processId));
    }

    /// <summary>不存在的 pid 必须安静返回 null，而不是抛 Win32 异常。</summary>
    [Fact]
    public void TryGet_NonexistentProcess_ReturnsNull()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 取一个几乎不可能存在的 pid
        Assert.Null(ProcessCommandLine.TryGet(int.MaxValue - 1));
    }
}
