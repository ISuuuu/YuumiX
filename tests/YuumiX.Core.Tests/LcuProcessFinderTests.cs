using Xunit;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Tests;

/// <summary>
/// 发现流程的两级降级与失败路径。
/// 全部基于注入的假进程源与内存文件表，因此不需要本机装英雄联盟也能覆盖。
/// </summary>
public sealed class LcuProcessFinderTests
{
    private const int ClientProcessId = 4321;

    private const string ClientCommandLine =
        "\"LeagueClientUx.exe\" --app-port=54321 --remoting-auth-token=cmdToken --rso_platform_id=CN1";

    [Fact]
    public void TryFind_PrefersLockfile()
    {
        var source = new FakeProcessSource();
        source.Processes.Add(new LcuProcessInfo(ClientProcessId, ClientExePath, ClientCommandLine));
        source.AliveProcessIds.Add(ClientProcessId);

        var finder = NewFinder(source, new()
        {
            [LockfilePath] = $"LeagueClient:{ClientProcessId}:50000:lockfileToken:https",
        });

        var credentials = finder.TryFind();

        Assert.NotNull(credentials);
        Assert.Equal(50000u, credentials.Port);
        Assert.Equal("lockfileToken", credentials.Token);
        Assert.Equal((uint)ClientProcessId, credentials.ProcessId);
        // 大区信息只能从命令行补
        Assert.Equal("CN1", credentials.Server);
    }

    /// <summary>
    /// LOL 异常退出会残留 lockfile。pid 已死时必须判定为「不可用」，
    /// 否则会把一个死端口汇报成在线（原项目标注过的回归点）。
    /// </summary>
    [Fact]
    public void TryFind_StaleLockfileWithoutLiveProcess_ReturnsNull()
    {
        var source = new FakeProcessSource();
        source.Processes.Add(new LcuProcessInfo(ClientProcessId, ClientExePath, null));

        var finder = NewFinder(source, new()
        {
            [LockfilePath] = $"LeagueClient:{ClientProcessId}:50000:lockfileToken:https",
        });

        Assert.Null(finder.TryFind());
    }

    [Fact]
    public void TryFind_StaleLockfile_FallsBackToCommandLine()
    {
        var source = new FakeProcessSource();
        source.Processes.Add(new LcuProcessInfo(ClientProcessId, ClientExePath, ClientCommandLine));

        var finder = NewFinder(source, new()
        {
            [LockfilePath] = $"LeagueClient:{ClientProcessId}:50000:lockfileToken:https",
        });

        var credentials = finder.TryFind();

        Assert.NotNull(credentials);
        Assert.Equal(54321u, credentials.Port);
        Assert.Equal("cmdToken", credentials.Token);
    }

    /// <summary>读不到 exe 路径（权限不足）时只能走命令行，这是最常见的降级场景。</summary>
    [Fact]
    public void TryFind_WithoutExecutablePath_UsesCommandLine()
    {
        var source = new FakeProcessSource();
        source.Processes.Add(new LcuProcessInfo(ClientProcessId, null, ClientCommandLine));

        var credentials = NewFinder(source, new()).TryFind();

        Assert.NotNull(credentials);
        Assert.Equal(54321u, credentials.Port);
        Assert.Equal("cmdToken", credentials.Token);
        Assert.Equal("CN1", credentials.Server);
    }

    [Fact]
    public void TryFind_NoClientProcess_ReturnsNull()
    {
        Assert.Null(NewFinder(new FakeProcessSource(), new()).TryFind());
    }

    [Fact]
    public void TryFind_IncompleteLockfile_FallsBackToCommandLine()
    {
        var source = new FakeProcessSource();
        source.Processes.Add(new LcuProcessInfo(ClientProcessId, ClientExePath, ClientCommandLine));
        source.AliveProcessIds.Add(ClientProcessId);

        var finder = NewFinder(source, new()
        {
            [LockfilePath] = "LeagueClient:4321", // 写到一半
        });

        Assert.Equal("cmdToken", finder.TryFind()?.Token);
    }

    /// <summary>部分安装形态把 lockfile 放在 exe 的上一级。</summary>
    [Fact]
    public void TryFind_LockfileInParentDirectory_IsFound()
    {
        var source = new FakeProcessSource();
        source.Processes.Add(new LcuProcessInfo(ClientProcessId, ClientExePath, null));
        source.AliveProcessIds.Add(ClientProcessId);

        var finder = NewFinder(source, new()
        {
            [Path.Combine(RootPath, "lockfile")] = $"LeagueClient:{ClientProcessId}:50000:parentToken:https",
        });

        Assert.Equal("parentToken", finder.TryFind()?.Token);
    }

    /// <summary>目录缓存命中后不再依赖 exe 路径，避免每轮都扫进程主模块（较慢且可能失败）。</summary>
    [Fact]
    public void TryFind_ReusesCachedDirectoryWhenExecutablePathDisappears()
    {
        var source = new FakeProcessSource();
        source.Processes.Add(new LcuProcessInfo(ClientProcessId, ClientExePath, null));
        source.AliveProcessIds.Add(ClientProcessId);

        var finder = NewFinder(source, new()
        {
            [LockfilePath] = $"LeagueClient:{ClientProcessId}:50000:token:https",
        });

        Assert.NotNull(finder.TryFind());

        // 第二轮拿不到 exe 路径，但缓存仍指向有效 lockfile
        source.Processes.Clear();
        source.Processes.Add(new LcuProcessInfo(ClientProcessId, null, null));

        Assert.NotNull(finder.TryFind());
    }

    [Fact]
    public void Reset_ClearsCachedDirectory()
    {
        var source = new FakeProcessSource();
        source.Processes.Add(new LcuProcessInfo(ClientProcessId, ClientExePath, null));
        source.AliveProcessIds.Add(ClientProcessId);

        var finder = NewFinder(source, new()
        {
            [LockfilePath] = $"LeagueClient:{ClientProcessId}:50000:token:https",
        });

        Assert.NotNull(finder.TryFind());

        finder.Reset();
        source.Processes.Clear();
        source.Processes.Add(new LcuProcessInfo(ClientProcessId, null, null));

        // 缓存已清且拿不到 exe 路径 → 无从定位 lockfile
        Assert.Null(finder.TryFind());
    }

    private static string RootPath => Path.Combine("Game", "LeagueClient");

    private static string ClientExePath => Path.Combine(RootPath, "LeagueClientUx.exe");

    private static string LockfilePath => Path.Combine(RootPath, "lockfile");

    private static LcuProcessFinder NewFinder(ILcuProcessSource source, Dictionary<string, string> files)
        => new(source, path => files.TryGetValue(path, out var text) ? text : null);

    private sealed class FakeProcessSource : ILcuProcessSource
    {
        public List<LcuProcessInfo> Processes { get; } = [];

        public HashSet<int> AliveProcessIds { get; } = [];

        public IReadOnlyList<LcuProcessInfo> FindClientProcesses() => Processes;

        public bool IsProcessAlive(int processId) => AliveProcessIds.Contains(processId);
    }
}
