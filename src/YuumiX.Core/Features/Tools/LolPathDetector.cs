using System.Diagnostics;

namespace YuumiX.Core.Features.Tools;

/// <summary>
/// 注册表读取抽象。测试注入点：单测用内存字典模拟注册表内容。
/// </summary>
public interface IRegistryReader
{
    /// <summary>读取 REG_SZ 字符串值；键或值不存在时返回 null。</summary>
    string? GetString(string key, string valueName);

    /// <summary>列出子键名；键不存在时返回空集合。</summary>
    IReadOnlyList<string> GetSubKeyNames(string key);
}

/// <summary>
/// 进程可执行路径查询抽象。测试注入点：单测不需要真的去枚举系统进程。
/// </summary>
public interface IProcessPathSource
{
    /// <summary>
    /// 按进程名（不带 <c>.exe</c>，忽略大小写）返回一个运行中进程的 exe 完整路径；
    /// 没有运行或都读不到路径时返回 null。
    /// </summary>
    string? FindExecutablePath(string processName);
}

/// <summary>
/// 英雄联盟客户端 / WeGame 安装路径的自动检测（对应旧 <c>commands/tools.rs</c> 的
/// <c>detect_lol_path</c> 与 <c>find_wegame_exe</c>）。
///
/// 两条路径检测规则的降级链：
/// <list type="number">
///   <item>LOL：运行中的 <c>LeagueClientUx.exe</c> 进程回溯安装目录 →
///         注册表 <c>HKCU\SOFTWARE\Tencent\LOL</c> 的 <c>InstallPath</c>（国服 TCLS 目录适配）。</item>
///   <item>WeGame：注册表 InstallPath×3 → 卸载项 DisplayName → 常见安装目录 → 进程兜底。</item>
/// </list>
///
/// 路径统一转成正斜杠 + 盘符大写（与旧实现一致，保证与配置里已有条目可比对）。
/// 注册表读取走 <c>reg query</c> 子进程而非 Win32 API：Core 的 TFM 不带 <c>-windows</c>，
/// Registry API 会引发 CA1416 平台告警外溢，子进程方式与旧实现的 <c>reg query</c> 行为也逐字一致。
/// </summary>
public sealed class LolPathDetector
{
    /// <summary>国服 LOL 注册表键（旧实现同款）。</summary>
    public const string LolRegistryKey = @"HKCU\SOFTWARE\Tencent\LOL";

    /// <summary>WeGame 注册表键 ×3（用户级 / 系统级 / WOW64 视图）。</summary>
    public static readonly string[] WeGameRegistryKeys =
    [
        @"HKCU\SOFTWARE\Tencent\WeGame",
        @"HKLM\SOFTWARE\Tencent\WeGame",
        @"HKLM\SOFTWARE\WOW6432Node\Tencent\WeGame",
    ];

    private static readonly string[] UninstallRoots =
    [
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    private readonly IRegistryReader _registry;
    private readonly IProcessPathSource _processes;

    /// <summary>
    /// 是否探测真实磁盘的常见安装目录（各盘符 Program Files、D:/E:\WeGame 等）。
    /// 单测置 false：否则会命中测试机真实安装的 WeGame，探测结果不可注入。
    /// </summary>
    private readonly bool _probeCommonDirectories;

    public LolPathDetector(IRegistryReader registry, IProcessPathSource processes, bool probeCommonDirectories = true)
    {
        _registry = registry;
        _processes = processes;
        _probeCommonDirectories = probeCommonDirectories;
    }

    /// <summary>
    /// 检测 LOL 安装目录。进程未运行且注册表也读不到时返回 null。
    /// </summary>
    public string? DetectLolPath()
    {
        // 1. 优先从运行中的客户端进程回溯
        string? exePath = _processes.FindExecutablePath("LeagueClientUx");
        if (exePath is not null)
        {
            string? resolved = ResolveInstallDirectoryFromClientExecutable(exePath);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        // 2. 进程未运行：注册表读取国服安装路径
        string? raw = _registry.GetString(LolRegistryKey, "InstallPath");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string normalized = NormalizePath(raw);
        return ApplyTclsAdapter(normalized);
    }

    /// <summary>
    /// 探测 WeGame 安装目录（<c>wegame.exe</c> 所在目录）。找不到返回 null。
    /// </summary>
    public string? DetectWeGameDirectory()
    {
        string? exe = FindWeGameExecutable();
        if (exe is null)
        {
            return null;
        }

        // 返回目录形态（与 detect_wegame_path 一致），统一正斜杠
        string? dir = Path.GetDirectoryName(Path.GetFullPath(exe));
        return dir is null ? null : NormalizePath(dir);
    }

    /// <summary>
    /// 探测 <c>wegame.exe</c> 完整路径（旧 <c>find_wegame_exe</c> 的降级链）。
    /// </summary>
    public string? FindWeGameExecutable()
    {
        // 1. 注册表 InstallPath（WeGame 可能装在用户级或系统级）
        foreach (string key in WeGameRegistryKeys)
        {
            string? dir = _registry.GetString(key, "InstallPath");
            if (!string.IsNullOrWhiteSpace(dir))
            {
                string exe = Path.Combine(dir, "wegame.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
        }

        // 2. 注册表卸载项（DisplayName 含 WeGame 的项，读 InstallLocation / DisplayIcon）
        string? fromUninstall = FindWeGameFromUninstall();
        if (fromUninstall is not null)
        {
            return fromUninstall;
        }

        // 3. 常见安装目录（各盘符的 Program Files / Program Files (x86)）
        if (_probeCommonDirectories)
        {
            for (int letter = 'A'; letter <= 'Z'; letter++)
            {
                string drive = $"{(char)letter}:\\";
                if (!Directory.Exists(drive))
                {
                    continue;
                }

                foreach (string subDir in new[] { "Program Files", "Program Files (x86)" })
                {
                    string exe = Path.Combine(drive, subDir, "WeGame", "wegame.exe");
                    if (File.Exists(exe))
                    {
                        return exe;
                    }
                }
            }

            // 补充常见非标准安装目录（未必在注册表卸载项中）
            foreach (string dir in new[] { @"D:\WeGame", @"E:\WeGame" })
            {
                string exe = Path.Combine(dir, "wegame.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }
            }

            string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                string exe = Path.Combine(localAppData, "WeGame", "wegame.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
        }

        // 4. 进程兜底：WeGame 运行中时从其 exe 路径推断
        return _processes.FindExecutablePath("wegame");
    }

    /// <summary>从注册表卸载项中查找 WeGame（旧 <c>find_wegame_from_uninstall</c>）。</summary>
    private string? FindWeGameFromUninstall()
    {
        foreach (string root in UninstallRoots)
        {
            foreach (string subKey in _registry.GetSubKeyNames(root))
            {
                string fullKey = $"{root}\\{subKey}";
                string? displayName = _registry.GetString(fullKey, "DisplayName");
                if (displayName is null || !displayName.Contains("wegame", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 优先 InstallLocation
                string? location = _registry.GetString(fullKey, "InstallLocation");
                if (!string.IsNullOrWhiteSpace(location))
                {
                    string exe = Path.Combine(location, "wegame.exe");
                    if (File.Exists(exe))
                    {
                        return exe;
                    }
                }

                // 其次 DisplayIcon（形如 "路径,图标序号"，取逗号前的路径）
                string? icon = _registry.GetString(fullKey, "DisplayIcon");
                if (string.IsNullOrWhiteSpace(icon))
                {
                    continue;
                }

                string clean = icon.Split(',')[0].Trim().Trim('"');
                if (clean.Length == 0)
                {
                    continue;
                }

                if (string.Equals(Path.GetFileName(clean), "wegame.exe", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(clean))
                {
                    return clean;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 从 LeagueClientUx 的 exe 路径回溯安装目录：逐级向上找包含客户端主程序的目录；
    /// 找不到时兜底返回 exe 的上两级（旧实现同款）。
    /// </summary>
    internal static string? ResolveInstallDirectoryFromClientExecutable(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
        while (dir is not null)
        {
            if (DirectoryContainsClientExecutable(dir))
            {
                return NormalizePath(dir);
            }

            dir = Path.GetDirectoryName(dir);
        }

        // 兜底：返回 exe 的上两级
        string? parent = Path.GetDirectoryName(Path.GetFullPath(exePath));
        if (parent is not null)
        {
            string? root = Path.GetDirectoryName(parent);
            if (root is not null)
            {
                return NormalizePath(root);
            }
        }

        return null;
    }

    /// <summary>目录下是否存在客户端主程序（LeagueClient.exe / Client.exe / client.exe）。</summary>
    internal static bool DirectoryContainsClientExecutable(string dir) =>
        File.Exists(Path.Combine(dir, "LeagueClient.exe"))
        || File.Exists(Path.Combine(dir, "Client.exe"))
        || File.Exists(Path.Combine(dir, "client.exe"));

    /// <summary>
    /// 国服适配：归一化后的安装目录下存在 <c>TCLS</c> 子目录时返回 TCLS 目录
    /// （国服由 TCLS/client.exe 启动，路径列表存 TCLS 目录才有意义）。
    /// </summary>
    internal static string? ApplyTclsAdapter(string normalizedPath)
    {
        if (Directory.Exists(Path.Combine(normalizedPath, "TCLS")))
        {
            return NormalizePath(Path.Combine(normalizedPath, "TCLS"));
        }

        return normalizedPath;
    }

    /// <summary>路径统一为正斜杠 + 盘符大写（与旧实现的格式化规则一致，保证条目可比对）。</summary>
    public static string NormalizePath(string rawPath)
    {
        string path = rawPath.Replace('\\', '/').Trim();
        if (path.Length >= 2 && path[1] == ':')
        {
            path = char.ToUpperInvariant(path[0]) + path[1..];
        }

        return path;
    }
}

/// <summary>
/// 注册表读取的 Windows 实现：经 <c>reg query</c> 子进程（与旧实现一致）。
/// 非平台上不做守卫——调用点只在用户点击「自动检测」时触达，Windows 之外本就不存在 LOL。
/// </summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    public static readonly WindowsRegistryReader Instance = new();

    public string? GetString(string key, string valueName)
    {
        string stdout = RunReg($"query \"{key}\" /v \"{valueName}\"");
        foreach (string line in stdout.Split('\n'))
        {
            if (!line.Contains(valueName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int pos = line.IndexOf("REG_SZ", StringComparison.Ordinal);
            if (pos < 0)
            {
                continue;
            }

            string raw = line[(pos + 6)..].Trim();
            if (raw.Length > 0)
            {
                return raw;
            }
        }

        return null;
    }

    public IReadOnlyList<string> GetSubKeyNames(string key)
    {
        string stdout = RunReg($"query \"{key}\"");
        var result = new List<string>();
        foreach (string rawLine in stdout.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase) || line.Length <= key.Length)
            {
                continue;
            }

            // reg query 的输出行形如 "HKLM\...\Uninstall\WeGame"（尾随 \r）
            result.Add(line[key.Length..].TrimStart('\\').TrimEnd('\r'));
        }

        return result;
    }

    private static string RunReg(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("reg", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return string.Empty;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch (Exception)
        {
            // reg 不存在或被策略禁用：按键不存在处理
            return string.Empty;
        }
    }
}

/// <summary>进程可执行路径查询的 Windows 实现（按进程名枚举，读 MainModule）。</summary>
public sealed class WindowsProcessPathSource : IProcessPathSource
{
    public static readonly WindowsProcessPathSource Instance = new();

    public string? FindExecutablePath(string processName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        string? path = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path))
                        {
                            return path;
                        }
                    }
                    catch (Exception)
                    {
                        // 权限不足或位数不匹配：同名的下一个进程可能读得到
                    }
                }
            }
        }
        catch (Exception)
        {
            // 进程枚举失败按未运行处理
        }

        return null;
    }
}
