using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Persistence;
using YuumiX.Core.Runtime;

namespace YuumiX.Core.Updates;

/// <summary>GitHub 拉取的版本信息（对齐旧 updater.rs 的 UpdateInfo）。</summary>
public sealed record UpdateInfo(
    string Version,
    string CurrentVersion,
    string? Notes,
    string? PubDate,
    string? DownloadUrl);

/// <summary>下载进度快照（对齐旧 DownloadProgress；total 未知时 Percent 为 null）。</summary>
public sealed record DownloadProgress(long Downloaded, long? Total, double? Percent);

/// <summary>
/// GitHub 域名白名单 + 代理复用的 HTTP 客户端（对应旧 <c>commands/tools.rs</c> 的
/// <c>fetch_github_text</c> 与 <c>get_github_client</c>）。
///
/// 三条硬约束（全部继承自旧实现）：
/// <list type="number">
///   <item>仅放行 github.com / api.github.com / *.githubusercontent.com，其余域名直接拒绝；</item>
///   <item>代理开关与地址来自配置，进程级复用客户端，配置不变不重建；</item>
///   <item>15 秒超时；User-Agent 标识应用（GitHub API 必需）。</item>
/// </list>
/// </summary>
public static class GitHubClient
{
    /// <summary>更新检查的仓库（工程 git remote 与 README 指向 ISuuuu/YuumiX）。</summary>
    public const string Repository = "ISuuuu/YuumiX";

    private static readonly Lock Gate = new();
    private static string? _cachedProxyKey;
    private static HttpClient? _cachedClient;

    /// <summary>URL 是否在 GitHub 域名白名单内（旧 fetch_github_text 的域名校验）。</summary>
    public static bool IsAllowedGitHubUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        string host = uri.Host;
        return host == "github.com"
            || host == "api.github.com"
            || host == "raw.githubusercontent.com"
            || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
    }

    /// <summary>进程级复用客户端：代理键不变时直接返回（旧 get_github_client）。</summary>
    public static HttpClient GetClient(bool enableProxy, string proxyAddr)
    {
        lock (Gate)
        {
            string key = $"{enableProxy}|{proxyAddr}";
            if (_cachedClient is not null && _cachedProxyKey == key)
            {
                return _cachedClient;
            }

            var handler = new SocketsHttpHandler();
            if (enableProxy && !string.IsNullOrWhiteSpace(proxyAddr))
            {
                string proxyUrl = proxyAddr.Contains("://", StringComparison.Ordinal)
                    ? proxyAddr
                    : $"http://{proxyAddr}";
                if (Uri.TryCreate(proxyUrl, UriKind.Absolute, out var proxyUri))
                {
                    handler.Proxy = new System.Net.WebProxy(proxyUri);
                    handler.UseProxy = true;
                }
            }
            else
            {
                handler.UseProxy = false;
            }

            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("YuumiX/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            _cachedClient?.Dispose();
            _cachedClient = client;
            _cachedProxyKey = key;
            return client;
        }
    }

    /// <summary>经白名单校验拉取远程文本。非 GitHub 域名抛出异常（旧实现返回 Err）。</summary>
    public static async Task<string> FetchTextAsync(string url, bool enableProxy, string proxyAddr, CancellationToken token = default)
    {
        if (!IsAllowedGitHubUrl(url))
        {
            throw new InvalidOperationException($"仅允许访问 GitHub 域名: {url}");
        }

        using var response = await GetClient(enableProxy, proxyAddr).GetAsync(url, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }

    /// <summary>归一化版本号：去首尾空白与 v 前缀（旧 normalize_version）。</summary>
    public static string NormalizeVersion(string version)
    {
        string trimmed = version.Trim();
        return trimmed.StartsWith('v') ? trimmed[1..].Trim() : trimmed;
    }
}

/// <summary>
/// GitHub Releases 更新日志与更新检查服务（对应旧 get_release_changelog / updater.rs / portable_updater.rs）。
///
/// 更新日志：24h 本地缓存 <c>releases_cache.json</c>；缓存缺当前版本（刚升级场景）强制刷新。
/// 更新检查：releases/latest 对比归一化版本号；便携版只认名称含 <c>portable</c> 的资产
/// （对齐旧 <c>windows-x86_64-portable</c> target 语义——没有该资产视为无更新）。
/// </summary>
public sealed class UpdateService
{
    /// <summary>缓存有效期 24 小时（旧 CACHE_TTL_SECS）。</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly RuntimePaths _paths;
    private readonly Func<AppConfig> _configProvider;
    private readonly ILogger<UpdateService>? _logger;

    public UpdateService(RuntimePaths paths, Func<AppConfig> configProvider, ILogger<UpdateService>? logger = null)
    {
        _paths = paths;
        _configProvider = configProvider;
        _logger = logger;
    }

    /// <summary>缓存文件路径：<c>&lt;数据目录&gt;/releases_cache.json</c>（旧 releases_cache_path 同构）。</summary>
    public string ReleasesCachePath => Path.Combine(_paths.DataDirectory, "releases_cache.json");

    /// <summary>更新日志条目（对齐旧 ReleaseEntry）。</summary>
    public sealed record ReleaseEntry(string Tag, string PublishedAt, string Body);

    /// <summary>当前进程版本（入口程序集，去 v 前缀归一化后参与比较）。</summary>
    public static string CurrentVersion =>
        GitHubClient.NormalizeVersion(
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0");

    // ── 更新日志 ────────────────────────────────────────────────

    /// <summary>
    /// 获取更新日志（带 24h 缓存）。缓存命中且含当前版本时直接返回；
    /// 缓存缺失当前版本（刚升级）或过期时强制刷新。
    /// </summary>
    public async Task<IReadOnlyList<ReleaseEntry>> GetReleaseChangelogAsync(bool forceRefresh = false, CancellationToken token = default)
    {
        if (!forceRefresh && TryReadCache(out var cached) && cached.Count > 0)
        {
            _logger?.LogInformation("[更新日志] 命中本地缓存，跳过 GitHub 请求");
            return cached;
        }

        var config = _configProvider();
        string text = await GitHubClient.FetchTextAsync(
            $"https://api.github.com/repos/{GitHubClient.Repository}/releases",
            config.General.EnableHttpProxy,
            config.General.HttpProxyAddr,
            token).ConfigureAwait(false);

        var releases = ParseReleases(text);
        WriteCache(releases);
        return releases;
    }

    /// <summary>缓存新鲜判定（纯函数）：非空、未过期、包含当前版本。</summary>
    internal static bool IsCacheFresh(
        IReadOnlyList<ReleaseEntry> cache,
        string? currentVersion,
        TimeSpan age)
    {
        if (cache.Count == 0)
        {
            return false;
        }

        if (age > CacheTtl)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            return true;
        }

        string normalized = GitHubClient.NormalizeVersion(currentVersion);
        return cache.Any(e => GitHubClient.NormalizeVersion(e.Tag) == normalized);
    }

    private bool TryReadCache(out IReadOnlyList<ReleaseEntry> cache)
    {
        cache = [];
        try
        {
            if (!File.Exists(ReleasesCachePath))
            {
                return false;
            }

            var entries = JsonSerializer.Deserialize<List<ReleaseEntry>>(
                File.ReadAllText(ReleasesCachePath),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            if (entries is null)
            {
                return false;
            }

            TimeSpan age = TimeSpan.Zero;
            var info = new FileInfo(ReleasesCachePath);
            if (info.LastWriteTimeUtc != default)
            {
                age = DateTime.UtcNow - info.LastWriteTimeUtc;
            }

            if (!IsCacheFresh(entries, CurrentVersion, age))
            {
                _logger?.LogInformation("[更新日志] 缓存过期或缺少当前版本，强制刷新");
                return false;
            }

            cache = entries;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void WriteCache(IReadOnlyList<ReleaseEntry> releases)
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            string json = JsonSerializer.Serialize(
                releases,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            string tmp = ReleasesCachePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ReleasesCachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "写入版本日志缓存失败");
        }
    }

    /// <summary>解析 GitHub Releases API 数组载荷（tag_name / published_at / body）。</summary>
    internal static List<ReleaseEntry> ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("版本数据格式错误");
        }

        var result = new List<ReleaseEntry>();
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            result.Add(new ReleaseEntry(
                rel.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() ?? string.Empty : string.Empty,
                rel.TryGetProperty("published_at", out var pub) && pub.ValueKind == JsonValueKind.String ? pub.GetString() ?? string.Empty : string.Empty,
                rel.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? string.Empty : string.Empty));
        }

        return result;
    }

    // ── 更新检查 ────────────────────────────────────────────────

    /// <summary>
    /// 检查更新。安装版取 releases/latest 与归一化版本比较；
    /// 便携版额外要求资产名含 <c>portable</c>（缺该资产视为无更新，对齐旧 portable target 语义）。
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(bool isPortable, CancellationToken token = default)
    {
        var config = _configProvider();
        string text = await GitHubClient.FetchTextAsync(
            $"https://api.github.com/repos/{GitHubClient.Repository}/releases/latest",
            config.General.EnableHttpProxy,
            config.General.HttpProxyAddr,
            token).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        string latestTag = root.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String
            ? tag.GetString() ?? string.Empty
            : string.Empty;
        if (latestTag.Length == 0)
        {
            return null;
        }

        string latest = GitHubClient.NormalizeVersion(latestTag);
        string current = CurrentVersion;
        if (!IsNewerVersion(latest, current))
        {
            return null;
        }

        if (isPortable)
        {
            string? downloadUrl = FindPortableAssetUrl(root);
            if (downloadUrl is null)
            {
                // 便携版没有对应 target 资产 → 视为无更新（旧 check_portable_update 语义）
                _logger?.LogInformation("[更新] 便携版未找到 portable 资产，视为无更新");
                return null;
            }

            return new UpdateInfo(latest, current,
                root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null,
                root.TryGetProperty("published_at", out var pub) && pub.ValueKind == JsonValueKind.String ? pub.GetString() : null,
                downloadUrl);
        }

        return new UpdateInfo(latest, current,
            ReadStringOrNull(root, "body"),
            ReadStringOrNull(root, "published_at"),
            null);
    }

    private static string? ReadStringOrNull(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>语义化版本比较：latest > current 时返回 true（解析失败视为相等，不误报更新）。</summary>
    internal static bool IsNewerVersion(string latest, string current)
    {
        if (!Version.TryParse(latest, out var latestVersion)
            || !Version.TryParse(current, out var currentVersion))
        {
            return false;
        }

        return latestVersion > currentVersion;
    }

    /// <summary>便携资产：browser_download_url 中名称含 portable 的 zip（旧 windows-x86_64-portable target 语义）。</summary>
    internal static string? FindPortableAssetUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            string name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? string.Empty
                : string.Empty;
            if (!name.Contains("portable", StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (asset.TryGetProperty("browser_download_url", out var url) && url.ValueKind == JsonValueKind.String)
            {
                return url.GetString();
            }
        }

        return null;
    }

    // ── 下载与安装 ──────────────────────────────────────────────

    /// <summary>
    /// 后台静默下载更新到 <c>&lt;数据目录&gt;/downloads/</c>，进度经 <paramref name="progress"/> 上报。
    /// 返回下载文件路径。
    /// </summary>
    public async Task<string> DownloadUpdateAsync(
        UpdateInfo info,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl))
        {
            throw new InvalidOperationException("该版本没有可下载的资产");
        }

        if (!GitHubClient.IsAllowedGitHubUrl(info.DownloadUrl))
        {
            throw new InvalidOperationException("下载地址不在 GitHub 域名白名单内");
        }

        string downloadDir = Path.Combine(_paths.DataDirectory, "downloads");
        Directory.CreateDirectory(downloadDir);
        string targetPath = Path.Combine(downloadDir, $"YuumiX-{info.Version}"
            + (Path.GetExtension(info.DownloadUrl) is { Length: > 0 } ext ? ext : ".zip"));

        var config = _configProvider();
        using var response = await GitHubClient.GetClient(config.General.EnableHttpProxy, config.General.HttpProxyAddr)
            .GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var target = new FileStream(targetPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            downloaded += read;
            progress?.Report(new DownloadProgress(
                downloaded,
                total,
                total is > 0 ? Math.Min(100.0, downloaded * 100.0 / total.Value) : null));
        }

        await target.FlushAsync(token).ConfigureAwait(false);
        target.Close();
        File.Move(targetPath + ".tmp", targetPath, overwrite: true);
        return targetPath;
    }

    /// <summary>
    /// 应用已下载的更新并重启：
    /// <list type="bullet">
    ///   <item>安装版：启动安装程序后结束当前进程；</item>
    ///   <item>便携版（zip）：解压取出 YuumiX.exe，先把当前 exe 改名 <c>.old</c> 再移入新 exe
    ///         （Windows 允许运行中的 exe 改名，无需外部辅助进程），随后启动新版本并退出。</item>
    /// </list>
    /// </summary>
    public static void ApplyPendingUpdate(string downloadedPath, bool isPortable)
    {
        string currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位当前可执行文件");

        if (!isPortable)
        {
            // 安装版：交由安装程序接管
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = downloadedPath,
                UseShellExecute = true,
            });
            return;
        }

        // 便携版：zip 覆盖（旧 replace_executable 的简化形态）
        string extractDir = Path.Combine(Path.GetDirectoryName(downloadedPath)!, $"extract-{Path.GetFileNameWithoutExtension(downloadedPath)}");
        if (Directory.Exists(extractDir))
        {
            Directory.Delete(extractDir, recursive: true);
        }

        System.IO.Compression.ZipFile.ExtractToDirectory(downloadedPath, extractDir);
        string newExe = Directory.EnumerateFiles(extractDir, "YuumiX.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("压缩包中未找到 YuumiX.exe");

        string stagedExe = Path.Combine(Path.GetDirectoryName(currentExe)!, ".yuumix-update-new.exe");
        File.Copy(newExe, stagedExe, overwrite: true);

        string backupExe = currentExe + ".old";
        if (File.Exists(backupExe))
        {
            File.Delete(backupExe);
        }

        File.Move(currentExe, backupExe);
        File.Move(stagedExe, currentExe);

        // 启动新版本；.old 文件留给下次启动清理（运行中无法删除自身）
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = currentExe,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(currentExe),
        });
    }
}
