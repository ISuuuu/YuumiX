using System.ComponentModel;
using System.Diagnostics;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Features.Tools;

/// <summary>
/// 英雄联盟客户端启动器（对应旧 <c>commands/tools.rs</c> 的 <c>launch_lol_client</c> 及其辅助函数）。
///
/// 语义与旧实现对齐：
/// <list type="number">
///   <item>客户端已在运行（LeagueClientUx.exe）时直接跳过；</item>
///   <item>路径列表中的 <see cref="AppConfig.WeGameMarker"/> 条目走 WeGame 启动
///         （配置路径 → 自动探测）；遍历列表时首次遇到 marker 即接管启动；</item>
///   <item>普通条目按 LeagueClient.exe / Client.exe / client.exe / TCLS\client.exe 等候选探测；</item>
///   <item>探测到 LeagueClient.exe 且同级（或上一级）存在 RiotClientServices.exe 时，
///         改由 Riot 客户端服务启动（外服形态），PBE 目录用 pbe 补丁线；</item>
///   <item>直接启动失败且错误码为 740（需要提权）或 5（拒绝访问）时，
///         走 PowerShell <c>Start-Process -Verb RunAs</c> 触发 UAC。</item>
/// </list>
/// </summary>
public sealed class LolClientLauncher
{
    private readonly IProcessPathSource _processes;

    public LolClientLauncher(IProcessPathSource processes)
    {
        _processes = processes;
    }

    /// <summary>客户端是否已在运行（旧实现的 <c>already_running</code> 检查）。</summary>
    public bool IsClientRunning() => _processes.FindExecutablePath("LeagueClientUx") is not null;

    /// <summary>
    /// 启动客户端。返回 null 表示成功（含「已在运行，跳过」），否则为可读错误信息
    /// （对齐旧实现的错误文案）。<paramref name="detector"/> 供测试注入，缺省用 Windows 真实现。
    /// </summary>
    public string? Launch(IReadOnlyList<string> paths, string? wegamePath, LolPathDetector? detector = null)
    {
        // 已在运行时跳过（旧实现：客户端已在运行，跳过启动）
        if (IsClientRunning())
        {
            return null;
        }

        foreach (string p in paths)
        {
            if (p == AppConfig.WeGameMarker)
            {
                // 旧实现：遍历中首次遇到 WeGame 条目即接管启动（含失败短路）
                return LaunchWeGame(wegamePath, detector);
            }

            string? exe = FindExecutableInPath(p);
            if (exe is null)
            {
                continue;
            }

            return LaunchResolvedExecutable(exe);
        }

        return "未找到 LeagueClient.exe / Client.exe，请先在设置中配置客户端路径";
    }

    /// <summary>
    /// 启动 WeGame：优先使用配置路径（目录或 exe），失效时自动探测，找不到返回可读错误。
    /// </summary>
    public string? LaunchWeGame(string? configuredPath, LolPathDetector? detector = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string path = configuredPath.Trim();
            string exe = File.Exists(path)
                ? path
                : Path.Combine(path, "wegame.exe");
            if (File.Exists(exe))
            {
                return SpawnExecutable(exe, []);
            }
        }

        string? detected = (detector ?? new LolPathDetector(
            WindowsRegistryReader.Instance,
            WindowsProcessPathSource.Instance)).FindWeGameExecutable();
        if (detected is null)
        {
            return "未找到 WeGame。请先安装 WeGame，或在设置中手动配置 WeGame 路径。";
        }

        return SpawnExecutable(detected, []);
    }

    /// <summary>
    /// 智能探测客户端执行文件（旧实现的 <c>find_executable</code> 候选清单）。
    /// </summary>
    internal static string? FindExecutableInPath(string basePath)
    {
        var candidates = new (string SubDir, string ExeName)[]
        {
            ("", "LeagueClient.exe"),
            ("", "Client.exe"),
            ("", "client.exe"),
            ("TCLS", "client.exe"),
            ("TCLS", "Client.exe"),
            ("LeagueClient", "LeagueClient.exe"),
            ("../TCLS", "client.exe"),
            ("../TCLS", "Client.exe"),
            ("../LeagueClient", "LeagueClient.exe"),
        };

        foreach (var (subDir, exeName) in candidates)
        {
            string exe = subDir.Length == 0
                ? Path.Combine(basePath, exeName)
                : Path.Combine(basePath, subDir, exeName);
            if (File.Exists(exe))
            {
                return Path.GetFullPath(exe);
            }
        }

        return null;
    }

    /// <summary>
    /// 智能转换：探测到 Riot 纳管的外服客户端时改由 RiotClientServices.exe 启动（旧 <c>check_and_launch</c>）。
    /// </summary>
    private static string? LaunchResolvedExecutable(string exe)
    {
        string? riotService = null;
        if (string.Equals(Path.GetFileName(exe), "LeagueClient.exe", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(exe);
            if (parent is not null)
            {
                string sameLevel = Path.Combine(parent, "RiotClientServices.exe");
                if (File.Exists(sameLevel))
                {
                    riotService = sameLevel;
                }
                else
                {
                    string? grandparent = Path.GetDirectoryName(parent);
                    if (grandparent is not null)
                    {
                        string parentLevel = Path.Combine(grandparent, "RiotClientServices.exe");
                        if (File.Exists(parentLevel))
                        {
                            riotService = parentLevel;
                        }
                    }
                }
            }
        }

        if (riotService is not null)
        {
            // PBE 目录用 pbe 补丁线，其余走 live
            string patchline = exe.Contains("pbe", StringComparison.OrdinalIgnoreCase) ? "pbe" : "live";
            return SpawnExecutable(riotService,
            [
                "--launch-product=league_of_legends",
                $"--launch-patchline={patchline}",
            ]);
        }

        return SpawnExecutable(exe, []);
    }

    /// <summary>
    /// 启动可执行文件并处理 UAC 提升（旧 <c>spawn_executable</c>）：
    /// 设置工作目录为 exe 所在目录，防止 DLL 加载报拒绝访问（os error 5）；
    /// 错误 740（需要提升）/ 5（拒绝访问）走 PowerShell <c>Start-Process -Verb RunAs</c>。
    /// </summary>
    internal static string? SpawnExecutable(string exePath, IReadOnlyList<string> args)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
            };
            foreach (string arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            process.Start();
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 740 or 5)
        {
            return TryElevateWithPowerShell(exePath, args) ? null : $"启动失败: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"启动失败: {ex.Message}";
        }
    }

    /// <summary>错误 740/5 的提权重试：PowerShell Start-Process -Verb RunAs（隐藏窗口）。</summary>
    private static bool TryElevateWithPowerShell(string exePath, IReadOnlyList<string> args)
    {
        string EscapePsString(string s) => s.Replace("'", "''");

        string escapedExe = EscapePsString(exePath);
        string escapedWorkingDir = EscapePsString(Path.GetDirectoryName(exePath) ?? string.Empty);
        string argsPart = args.Count == 0
            ? string.Empty
            : " -ArgumentList " + string.Join(", ", args.Select(a => $"'{EscapePsString(a)}'"));
        string command =
            $"Start-Process -FilePath '{escapedExe}'{argsPart} -WorkingDirectory '{escapedWorkingDir}' -Verb RunAs";

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-Command \"{command}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            return process.Start();
        }
        catch (Exception)
        {
            return false;
        }
    }
}
