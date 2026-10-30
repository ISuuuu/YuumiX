using YuumiX.Core.Features.Tools;
using YuumiX.Core.Persistence;
using Xunit;

namespace YuumiX.Core.Tests;

/// <summary>
/// 客户端路径检测与启动探测的离线验收（requirements-settings.md §5：
/// 注册表 InstallPath / TCLS 适配 / 进程回溯 / WeGame 三处注册表 + 盘符兜底）。
/// 注册表与进程均为内存假实现，文件系统用临时目录承载。
/// </summary>
public sealed class LolPathDetectorTests : IDisposable
{
    private readonly string _tempDir;

    public LolPathDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"yuumix-detector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>注册表假实现：(key, valueName) → value，(key) → 子键列表。</summary>
    internal sealed class FakeRegistry : IRegistryReader
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> SubKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? GetString(string key, string valueName) =>
            Values.TryGetValue($"{key}\\{valueName}", out var value) ? value : null;

        public IReadOnlyList<string> GetSubKeyNames(string key) =>
            SubKeys.TryGetValue(key, out var keys) ? keys : [];
    }

    private sealed class FakeProcesses : IProcessPathSource
    {
        public string? ExePath { get; set; }

        public string? FindExecutablePath(string processName) => ExePath;
    }

    // ── 路径归一化 ──────────────────────────────────────────────

    [Theory]
    [InlineData(@"d:\Games\LOL", "D:/Games/LOL")]
    [InlineData("C:/already/slash", "C:/already/slash")]
    [InlineData(@"e:\TCLS", "E:/TCLS")]
    public void NormalizePath_ConvertsSlashesAndUppercasesDrive(string raw, string expected)
    {
        Assert.Equal(expected, LolPathDetector.NormalizePath(raw));
    }

    // ── 国服 TCLS 适配 ──────────────────────────────────────────

    [Fact]
    public void ApplyTclsAdapter_ReturnsTclsDirectoryWhenPresent()
    {
        string install = Path.Combine(_tempDir, "install");
        Directory.CreateDirectory(Path.Combine(install, "TCLS"));

        string? result = LolPathDetector.ApplyTclsAdapter(LolPathDetector.NormalizePath(install));

        Assert.Equal(LolPathDetector.NormalizePath(Path.Combine(install, "TCLS")), result);
    }

    [Fact]
    public void ApplyTclsAdapter_WithoutTcls_ReturnsNormalizedPath()
    {
        string install = Path.Combine(_tempDir, "plain");
        Directory.CreateDirectory(install);

        string normalized = LolPathDetector.NormalizePath(install);
        Assert.Equal(normalized, LolPathDetector.ApplyTclsAdapter(normalized));
    }

    // ── 进程回溯 ────────────────────────────────────────────────

    [Fact]
    public void ResolveFromClientExecutable_WalksUpToDirectoryContainingClientExe()
    {
        string root = Path.Combine(_tempDir, "lol");
        string sub = Path.Combine(root, "LeagueClient");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(root, "LeagueClient.exe"), "stub");
        File.WriteAllText(Path.Combine(sub, "LeagueClientUx.exe"), "stub");

        string? resolved = LolPathDetector.ResolveInstallDirectoryFromClientExecutable(
            Path.Combine(sub, "LeagueClientUx.exe"));

        Assert.Equal(LolPathDetector.NormalizePath(root), resolved);
    }

    [Fact]
    public void ResolveFromClientExecutable_WithoutClientExe_FallsBackToGrandparent()
    {
        string root = Path.Combine(_tempDir, "bare");
        string sub = Path.Combine(root, "LeagueClient");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "LeagueClientUx.exe"), "stub");

        string? resolved = LolPathDetector.ResolveInstallDirectoryFromClientExecutable(
            Path.Combine(sub, "LeagueClientUx.exe"));

        Assert.Equal(LolPathDetector.NormalizePath(root), resolved);
    }

    [Fact]
    public void DetectLolPath_ProcessTakesPriorityOverRegistry()
    {
        string root = Path.Combine(_tempDir, "proc");
        string sub = Path.Combine(root, "LeagueClient");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(root, "Client.exe"), "stub");
        File.WriteAllText(Path.Combine(sub, "LeagueClientUx.exe"), "stub");

        var registry = new FakeRegistry
        {
            Values = { [@"HKCU\SOFTWARE\Tencent\LOL\InstallPath"] = @"z:\should\not\be\used" },
        };

        var detector = new LolPathDetector(
            registry,
            new FakeProcesses { ExePath = Path.Combine(sub, "LeagueClientUx.exe") });

        Assert.Equal(LolPathDetector.NormalizePath(root), detector.DetectLolPath());
    }

    [Fact]
    public void DetectLolPath_RegistryFallback_AppliesTclsAdapterAndNormalizes()
    {
        string install = Path.Combine(_tempDir, "tencent");
        Directory.CreateDirectory(Path.Combine(install, "TCLS"));

        var registry = new FakeRegistry
        {
            Values = { [@"HKCU\SOFTWARE\Tencent\LOL\InstallPath"] = install },
        };

        var detector = new LolPathDetector(registry, new FakeProcesses { ExePath = null });

        Assert.Equal(
            LolPathDetector.NormalizePath(Path.Combine(install, "TCLS")),
            detector.DetectLolPath());
    }

    [Fact]
    public void DetectLolPath_NothingFound_ReturnsNull()
    {
        var detector = new LolPathDetector(new FakeRegistry(), new FakeProcesses { ExePath = null });

        Assert.Null(detector.DetectLolPath());
    }

    // ── WeGame 探测 ─────────────────────────────────────────────

    [Fact]
    public void FindWeGameExecutable_RegistryInstallPath_Wins()
    {
        string dir = Path.Combine(_tempDir, "wegame-install");
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "wegame.exe");
        File.WriteAllText(exe, "stub");

        var registry = new FakeRegistry
        {
            Values = { [@"HKCU\SOFTWARE\Tencent\WeGame\InstallPath"] = dir },
        };

        var detector = new LolPathDetector(registry, new FakeProcesses { ExePath = null });

        Assert.Equal(exe, detector.FindWeGameExecutable());
    }

    [Fact]
    public void FindWeGameExecutable_UninstallEntryDisplayIcon_Fallback()
    {
        string dir = Path.Combine(_tempDir, "wegame-uninstall");
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "wegame.exe");
        File.WriteAllText(exe, "stub");

        const string root = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var registry = new FakeRegistry
        {
            SubKeys = { [root] = ["{WEGAME-GUID}"] },
            Values =
            {
                [$"{root}\\{{WEGAME-GUID}}\\DisplayName"] = "WeGame",
                [$"{root}\\{{WEGAME-GUID}}\\DisplayIcon"] = $"{exe},0",
            },
        };

        var detector = new LolPathDetector(registry, new FakeProcesses { ExePath = null });

        Assert.Equal(exe, detector.FindWeGameExecutable());
    }

    [Fact]
    public void FindWeGameExecutable_ProcessFallback()
    {
        string dir = Path.Combine(_tempDir, "wegame-proc");
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "wegame.exe");
        File.WriteAllText(exe, "stub");

        var detector = new LolPathDetector(
            new FakeRegistry(),
            new FakeProcesses { ExePath = exe },
            probeCommonDirectories: false);

        Assert.Equal(exe, detector.FindWeGameExecutable());
    }

    [Fact]
    public void DetectWeGameDirectory_ReturnsNormalizedDirectory()
    {
        string dir = Path.Combine(_tempDir, "wegame-dir");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "wegame.exe"), "stub");

        var detector = new LolPathDetector(
            new FakeRegistry
            {
                Values = { [@"HKCU\SOFTWARE\Tencent\WeGame\InstallPath"] = dir },
            },
            new FakeProcesses());

        Assert.Equal(LolPathDetector.NormalizePath(dir), detector.DetectWeGameDirectory());
    }
}

/// <summary>
/// 启动器的纯探测逻辑验收（不含真实进程启动）。
/// </summary>
public sealed class LolClientLauncherTests : IDisposable
{
    private readonly string _tempDir;

    public LolClientLauncherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"yuumix-launcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeProcesses : IProcessPathSource
    {
        public bool ClientRunning { get; set; }

        public string? FindExecutablePath(string processName) =>
            ClientRunning && processName == "LeagueClientUx" ? @"C:\fake\LeagueClientUx.exe" : null;
    }

    [Fact]
    public void FindExecutableInPath_PrefersLeagueClientOverClient()
    {
        string dir = Path.Combine(_tempDir, "both");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "LeagueClient.exe"), "stub");
        File.WriteAllText(Path.Combine(dir, "Client.exe"), "stub");

        string? found = LolClientLauncher.FindExecutableInPath(dir);

        Assert.Equal(Path.Combine(dir, "LeagueClient.exe"), found);
    }

    [Fact]
    public void FindExecutableInPath_FindsTclsClient()
    {
        string dir = Path.Combine(_tempDir, "tcls-root");
        string tcls = Path.Combine(dir, "TCLS");
        Directory.CreateDirectory(tcls);
        File.WriteAllText(Path.Combine(tcls, "client.exe"), "stub");

        string? found = LolClientLauncher.FindExecutableInPath(dir);

        Assert.Equal(Path.Combine(tcls, "client.exe"), found);
    }

    [Fact]
    public void FindExecutableInPath_NothingFound_ReturnsNull()
    {
        Assert.Null(LolClientLauncher.FindExecutableInPath(_tempDir));
    }

    [Fact]
    public void Launch_ClientAlreadyRunning_Skips()
    {
        var launcher = new LolClientLauncher(new FakeProcesses { ClientRunning = true });

        Assert.Null(launcher.Launch(["whatever"], null));
    }

    [Fact]
    public void Launch_EmptyPaths_ReturnsConfigError()
    {
        var launcher = new LolClientLauncher(new FakeProcesses());

        Assert.Equal("未找到 LeagueClient.exe / Client.exe，请先在设置中配置客户端路径", launcher.Launch([], null));
    }

    [Fact]
    public void Launch_WeGameMarker_TakesOverTheList()
    {
        // marker 条目首次出现即接管：未配置 WegamePath 且探测不到 WeGame → 返回 WeGame 错误文案，
        // 证明它没有继续遍历后面的普通条目（探测器用空假实现，保证不读本机注册表与磁盘）。
        string dir = Path.Combine(_tempDir, "real-client");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "LeagueClient.exe"), "stub");

        var launcher = new LolClientLauncher(new FakeProcesses());

        var error = launcher.Launch(
            [AppConfig.WeGameMarker, dir],
            wegamePath: null,
            // 探测器用空假实现 + 关闭真实磁盘探测，保证不读本机注册表、不命中真实 WeGame
            detector: new LolPathDetector(
                new LolPathDetectorTests.FakeRegistry(),
                new FakeProcesses(),
                probeCommonDirectories: false));

        Assert.NotNull(error);
        Assert.Contains("WeGame", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Launch_WeGameMarkerAnywhere_TakesPriorityOverEarlierClientPath()
    {
        // marker 排在列表末位也要优先启动 WeGame：即便前面的真实客户端目录能探测到 exe，
        // 也不应抢先启动（错误文案含 WeGame 即证明走的是 WeGame 分支）。
        string dir = Path.Combine(_tempDir, "real-client-priority");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "LeagueClient.exe"), "stub");

        var launcher = new LolClientLauncher(new FakeProcesses());

        var error = launcher.Launch(
            [dir, AppConfig.WeGameMarker],
            wegamePath: null,
            detector: new LolPathDetector(
                new LolPathDetectorTests.FakeRegistry(),
                new FakeProcesses(),
                probeCommonDirectories: false));

        Assert.NotNull(error);
        Assert.Contains("WeGame", error, StringComparison.Ordinal);
    }
}
