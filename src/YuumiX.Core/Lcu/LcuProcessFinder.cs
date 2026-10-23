using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Lcu;

/// <summary>
/// 发现正在运行的 LCU 并解析出可用凭据。
///
/// 两级来源，逐级降级（对应原 <c>monitor.rs</c> 的方式一/方式二）：
/// <list type="number">
///   <item><b>lockfile</b>：权威来源，含 pid 便于校验是否残留；不含大区信息。</item>
///   <item><b>进程命令行</b>：lockfile 读不到时兜底，`--app-port=` 与 `--remoting-auth-token=` 齐全才算成功。</item>
/// </list>
///
/// 原实现在此之后还有「WMIC」第三级，此处**不移植**：<c>wmic</c> 自 Windows 11 24H2 起默认移除，
/// 而它要解决的问题（取命令行）已由 <see cref="Interop.ProcessCommandLine"/> 直接覆盖。
///
/// 本类只做「查一次」，轮询与状态机由上层连接层负责——这样单次发现可以纯函数式测试。
/// </summary>
public sealed class LcuProcessFinder
{
    private readonly ILcuProcessSource _source;
    private readonly Func<string, string?> _readTextFile;
    private readonly ILogger<LcuProcessFinder>? _logger;

    /// <summary>上次成功定位到的客户端目录，避免每轮都扫进程列表。</summary>
    private string? _cachedExecutableDirectory;

    /// <param name="source">进程来源。</param>
    /// <param name="readTextFile">读取文本文件；返回 <c>null</c> 表示不可读。默认走真实文件系统。</param>
    /// <param name="logger">可选日志。</param>
    public LcuProcessFinder(
        ILcuProcessSource source,
        Func<string, string?>? readTextFile = null,
        ILogger<LcuProcessFinder>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _readTextFile = readTextFile ?? DefaultReadTextFile;
        _logger = logger;
    }

    /// <summary>清空目录缓存。客户端重启或连接断开后调用，避免用旧路径反复撞空。</summary>
    public void Reset() => _cachedExecutableDirectory = null;

    /// <summary>执行一次发现。找不到、或凭据已失效时返回 <c>null</c>。</summary>
    public LcuCredentials? TryFind()
    {
        var processes = _source.FindClientProcesses();
        if (processes.Count == 0)
        {
            // 客户端不在，缓存必然失效
            _cachedExecutableDirectory = null;
            return null;
        }

        var fromLockfile = TryFindViaLockfile(processes);
        if (fromLockfile is not null)
        {
            return fromLockfile;
        }

        return TryFindViaCommandLine(processes);
    }

    private LcuCredentials? TryFindViaLockfile(IReadOnlyList<LcuProcessInfo> processes)
    {
        var directory = ResolveExecutableDirectory(processes);
        if (directory is null)
        {
            return null;
        }

        // 安装形态有两类：lockfile 与 LeagueClientUx.exe 同级，或在其上一级
        var candidates = new List<string> { Path.Combine(directory, "lockfile") };
        var parent = Path.GetDirectoryName(directory);
        if (!string.IsNullOrEmpty(parent))
        {
            candidates.Add(Path.Combine(parent, "lockfile"));
        }

        foreach (var candidate in candidates)
        {
            var content = _readTextFile(candidate);
            if (content is null)
            {
                continue;
            }

            if (!LockfileParser.TryParse(content, out var entry) || entry is null)
            {
                _logger?.LogDebug("lockfile 内容不完整，等待下一轮: {Path}", candidate);
                continue;
            }

            // LOL 异常退出会残留 lockfile。不校验 pid 就会误判 LCU 仍在线——
            // 这是原项目明确标注过的回归点，不能省。
            if (!_source.IsProcessAlive((int)entry.ProcessId))
            {
                _logger?.LogDebug("lockfile 残留：pid {ProcessId} 已退出", entry.ProcessId);
                _cachedExecutableDirectory = null;
                return null;
            }

            var server = FindServer(processes, (int)entry.ProcessId);
            _logger?.LogDebug(
                "从 lockfile 解析到 LCU：pid={ProcessId}, port={Port}, server={Server}",
                entry.ProcessId, entry.Port, server ?? "?");

            return new LcuCredentials(entry.Port, entry.Password, entry.ProcessId, server);
        }

        return null;
    }

    private LcuCredentials? TryFindViaCommandLine(IReadOnlyList<LcuProcessInfo> processes)
    {
        foreach (var process in processes)
        {
            if (!CommandLineCredentials.TryExtract(process.CommandLine, out var port, out var token, out var server))
            {
                // 无名/无权限进程很常见，不是错误
                continue;
            }

            _logger?.LogDebug(
                "从命令行解析到 LCU：pid={ProcessId}, port={Port}, server={Server}",
                process.ProcessId, port, server ?? "?");

            return new LcuCredentials(port, token, (uint)process.ProcessId, server);
        }

        return null;
    }

    /// <summary>
    /// 定位客户端目录。优先复用缓存，但缓存必须仍指向有效的 lockfile，
    /// 否则客户端换盘安装后会一直读旧路径。
    /// </summary>
    private string? ResolveExecutableDirectory(IReadOnlyList<LcuProcessInfo> processes)
    {
        if (_cachedExecutableDirectory is { } cached && HasLockfile(cached))
        {
            return cached;
        }

        foreach (var process in processes)
        {
            if (string.IsNullOrEmpty(process.ExecutablePath))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(process.ExecutablePath);
            if (!string.IsNullOrEmpty(directory))
            {
                _cachedExecutableDirectory = directory;
                return directory;
            }
        }

        return null;
    }

    private bool HasLockfile(string directory)
    {
        if (_readTextFile(Path.Combine(directory, "lockfile")) is not null)
        {
            return true;
        }

        var parent = Path.GetDirectoryName(directory);
        return !string.IsNullOrEmpty(parent)
               && _readTextFile(Path.Combine(parent, "lockfile")) is not null;
    }

    /// <summary>
    /// 从命令行补大区标识。lockfile 不含该信息；优先取 pid 匹配的进程，
    /// 匹配不到时退化为「任一进程有就用」——同一台机器上通常只有一个客户端在跑。
    /// </summary>
    private static string? FindServer(IReadOnlyList<LcuProcessInfo> processes, int preferredProcessId)
    {
        string? fallback = null;

        foreach (var process in processes)
        {
            if (!CommandLineCredentials.TryExtract(process.CommandLine, out _, out _, out var server) || server is null)
            {
                continue;
            }

            if (process.ProcessId == preferredProcessId)
            {
                return server;
            }

            fallback ??= server;
        }

        return fallback;
    }

    private static string? DefaultReadTextFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
