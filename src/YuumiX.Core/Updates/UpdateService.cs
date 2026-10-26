using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
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
/// 语义化版本（SemVer 2.0）解析与比较。
///
/// 为什么不用 <see cref="Version"/>：它不认 <c>1.2.3-rc.1</c> 这类预发布后缀，
/// <c>TryParse</c> 直接失败，调用方按「解析失败 → 不误报更新」退化成返回 false，
/// 于是预发布版本永远收不到更新提示。这里按 SemVer 规则自己比：
/// 数字段逐位比，数字相同则「无预发布 &gt; 有预发布」，预发布标识逐段比
/// （纯数字段按数值、字母段按序数、数字段小于字母段），构建元数据 <c>+xxx</c> 不参与比较。
/// </summary>
internal readonly record struct SemVerVersion(int[] Numbers, string[] PreRelease)
    : IComparable<SemVerVersion>
{
    /// <summary>是否为预发布版本（带 <c>-rc.1</c> 一类后缀）。</summary>
    public bool IsPrerelease => PreRelease.Length > 0;

    /// <summary>
    /// 解析版本字符串。容忍前导 <c>v</c>（tag 形态）与 <c>+build</c> 后缀；
    /// 数字段允许 2~4 段（缺失段视为 0），兼容历史 tag 里出现过的 <c>v1.2</c> 与四段版本。
    /// </summary>
    public static bool TryParse(string? text, out SemVerVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string raw = text.Trim();
        if (raw.StartsWith('v') || raw.StartsWith('V'))
        {
            raw = raw[1..];
        }

        int plus = raw.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            raw = raw[..plus];
        }

        string[] preRelease = [];
        int dash = raw.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            string preText = raw[(dash + 1)..];
            raw = raw[..dash];
            if (preText.Length == 0)
            {
                return false;
            }

            preRelease = preText.Split('.');
        }

        string[] parts = raw.Split('.');
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new SemVerVersion(numbers, preRelease);
        return true;
    }

    public int CompareTo(SemVerVersion other)
    {
        int length = Math.Max(Numbers.Length, other.Numbers.Length);
        for (int i = 0; i < length; i++)
        {
            int mine = i < Numbers.Length ? Numbers[i] : 0;
            int theirs = i < other.Numbers.Length ? other.Numbers[i] : 0;
            if (mine != theirs)
            {
                return mine.CompareTo(theirs);
            }
        }

        // 数字段相同：正式版高于同号预发布（1.0.0 > 1.0.0-rc.1）
        if (IsPrerelease != other.IsPrerelease)
        {
            return IsPrerelease ? -1 : 1;
        }

        if (!IsPrerelease)
        {
            return 0;
        }

        int shared = Math.Min(PreRelease.Length, other.PreRelease.Length);
        for (int i = 0; i < shared; i++)
        {
            int compared = CompareIdentifier(PreRelease[i], other.PreRelease[i]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return PreRelease.Length.CompareTo(other.PreRelease.Length);
    }

    private static int CompareIdentifier(string mine, string theirs)
    {
        bool mineNumeric = int.TryParse(mine, NumberStyles.None, CultureInfo.InvariantCulture, out int mineValue);
        bool theirsNumeric = int.TryParse(theirs, NumberStyles.None, CultureInfo.InvariantCulture, out int theirsValue);
        if (mineNumeric && theirsNumeric)
        {
            return mineValue.CompareTo(theirsValue);
        }

        // SemVer 规定纯数字标识符优先级低于字母标识符
        if (mineNumeric)
        {
            return -1;
        }

        if (theirsNumeric)
        {
            return 1;
        }

        return string.CompareOrdinal(mine, theirs);
    }
}

/// <summary>
/// GitHub Releases 更新日志与更新检查服务。
///
/// 更新日志：24h 本地缓存；缓存缺当前版本（刚升级场景）强制刷新。
/// 更新检查：从完整 releases 列表挑选候选（见 <see cref="SelectCandidate"/>），
/// 两种安装形态各认自己的资产——便携版要含 <c>portable</c> 的 zip，安装版要含
/// <c>setup</c> 的 exe；缺对应资产一律视为不可更新（宁可不提示，也不给用户一个下不动的按钮）。
/// </summary>
public sealed class UpdateService
{
    /// <summary>缓存有效期 24 小时（旧 CACHE_TTL_SECS）。</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// 更新失败标记文件名，落在数据目录下。
    ///
    /// 为什么用文件而不是任何进程内状态：失败发生在主进程退出之后（由延迟脚本执行替换），
    /// 那时应用已经没了，只有磁盘上的东西能跨越「退出 → 下次启动」把结果带回来。
    /// </summary>
    public const string FailureMarkerFileName = "update-failed.txt";

    /// <summary>失败原因码：主进程在脚本的超时窗口内没有退出。</summary>
    public const string FailureCodeWaitExit = "wait-exit";

    /// <summary>失败原因码：便携版目录覆盖失败（robocopy 返回 ≥ 8）。</summary>
    public const string FailureCodeCopyFailed = "copy-failed";

    /// <summary>失败原因码：安装版静默安装返回失败。</summary>
    public const string FailureCodeSetupFailed = "setup-failed";

    private readonly RuntimePaths _paths;
    private readonly Func<AppConfig> _configProvider;
    private readonly ILogger<UpdateService>? _logger;

    public UpdateService(RuntimePaths paths, Func<AppConfig> configProvider, ILogger<UpdateService>? logger = null)
    {
        _paths = paths;
        _configProvider = configProvider;
        _logger = logger;
    }

    /// <summary>
    /// 缓存文件路径：<c>&lt;数据目录&gt;/releases_cache.v3.json</c>。
    ///
    /// 为什么每次加字段都换文件名而不是加迁移逻辑：
    /// <list type="bullet">
    ///   <item>v1 → v2：旧条目缺少 <c>portableAssetUrl</c> 与 <c>prerelease</c>；</item>
    ///   <item>v2 → v3：旧条目缺少 <c>setupAssetUrl</c>，会让**安装版**把所有版本都判成
    ///         「没有可下载资产 → 无更新」，且该误判会一直持续到缓存过期（最长 24h）。</item>
    /// </list>
    /// 缓存只是可再生的派生数据，换文件名让它自然失效，比写迁移代码更稳。
    /// </summary>
    public string ReleasesCachePath => Path.Combine(_paths.DataDirectory, "releases_cache.v3.json");

    /// <summary>
    /// 更新日志条目（对齐旧 ReleaseEntry）。
    /// <paramref name="PortableAssetUrl"/> 供便携版挑选 zip，
    /// <paramref name="SetupAssetUrl"/> 供安装版挑选 setup.exe，
    /// <paramref name="IsPrerelease"/> 用于稳定通道过滤——三者都有默认值，
    /// 所以只关心更新日志的调用方仍可按三参数构造。
    /// </summary>
    public sealed record ReleaseEntry(
        string Tag,
        string PublishedAt,
        string Body,
        string? PortableAssetUrl = null,
        bool IsPrerelease = false,
        string? SetupAssetUrl = null);

    /// <summary>
    /// 当前进程版本（去 v 前缀与 <c>+build</c> 后归一化）。
    ///
    /// 优先读 <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/>：
    /// <c>AssemblyVersion</c> 只有数字段（<c>0.2.0-rc.1</c> 会被压成 <c>0.2.0.0</c>），
    /// 用它比较会把预发布用户误判成正式版用户——稳定通道过滤随之失效，
    /// 用户明明装的 rc 版本却收不到 rc.2。InformationalVersion 完整保留后缀。
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            var assembly = System.Reflection.Assembly.GetEntryAssembly();
            string? informational = assembly
                ?.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // MSBuild 默认会把 commit 拼成 +hash，构建元数据不参与版本比较
                int plus = informational.IndexOf('+', StringComparison.Ordinal);
                if (plus >= 0)
                {
                    informational = informational[..plus];
                }

                if (informational.Length > 0)
                {
                    return GitHubClient.NormalizeVersion(informational);
                }
            }

            return GitHubClient.NormalizeVersion(
                assembly?.GetName().Version?.ToString(3) ?? "0.0.0");
        }
    }

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

    /// <summary>
    /// 解析 GitHub Releases API 数组载荷（tag_name / published_at / body / prerelease / assets）。
    /// 同一份解析结果同时服务更新日志与更新检查，因此这里一次把两种安装形态的资产 URL
    /// 与预发布标志都取出来。
    /// draft 直接跳过：草稿不该出现在更新日志里，也不该被当成可更新目标。
    /// </summary>
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
            if (rel.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            result.Add(new ReleaseEntry(
                Tag: rel.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() ?? string.Empty : string.Empty,
                PublishedAt: rel.TryGetProperty("published_at", out var pub) && pub.ValueKind == JsonValueKind.String ? pub.GetString() ?? string.Empty : string.Empty,
                Body: rel.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? string.Empty : string.Empty,
                PortableAssetUrl: FindPortableAssetUrl(rel),
                IsPrerelease: rel.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                SetupAssetUrl: FindSetupAssetUrl(rel)));
        }

        return result;
    }

    // ── 更新检查 ────────────────────────────────────────────────

    /// <summary>
    /// 检查更新。
    ///
    /// 走完整的 releases 列表而不是 <c>releases/latest</c>，原因有两条：
    /// <list type="number">
    ///   <item><c>latest</c> 只返回最新**正式**版，预发布版本永远不会被看到；</item>
    ///   <item>列表请求与更新日志共用同一份 24h 缓存，一次请求服务两处，省一次 API 配额。</item>
    /// </list>
    /// 通道规则见 <see cref="SelectCandidate"/>；两种形态各认自己的资产
    /// （便携版 <c>portable*.zip</c>，安装版 <c>*setup*.exe</c>）。
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(bool isPortable, CancellationToken token = default)
    {
        var releases = await GetReleaseChangelogAsync(forceRefresh: false, token).ConfigureAwait(false);
        string current = CurrentVersion;
        var candidate = SelectCandidate(releases, current, isPortable);
        if (candidate is null)
        {
            return null;
        }

        return new UpdateInfo(
            GitHubClient.NormalizeVersion(candidate.Tag),
            current,
            candidate.Body,
            candidate.PublishedAt,
            // 资产按形态取：安装版拿 setup.exe，便携版拿 zip。
            // 两者都不为 null——SelectCandidate 已把缺资产的版本排除掉了。
            isPortable ? candidate.PortableAssetUrl : candidate.SetupAssetUrl);
    }

    /// <summary>
    /// 从发布列表中挑选升级目标（纯函数，便于单测）。规则：
    /// <list type="number">
    ///   <item>版本必须高于当前版本（SemVer 比较，含预发布）；</item>
    ///   <item>已装正式版的用户只收正式版——否则 <c>0.2.0-rc.1</c> 会被推给全部稳定版用户；</item>
    ///   <item>已装预发布的用户留在预发布通道，可继续收到 <c>rc.2</c> 或随后转正；</item>
    ///   <item>各形态要求该版本带对应资产：便携版要 <c>portable*.zip</c>，安装版要
    ///         <c>*setup*.exe</c>。缺资产视为不可用——只发了一种包时，另一种形态的
    ///         用户不该看到一条点下去必然失败的更新提示。</item>
    /// </list>
    /// </summary>
    internal static ReleaseEntry? SelectCandidate(
        IReadOnlyList<ReleaseEntry> releases,
        string currentVersion,
        bool isPortable)
    {
        bool currentIsPrerelease = SemVerVersion.TryParse(currentVersion, out var current)
            && current.IsPrerelease;

        ReleaseEntry? best = null;
        SemVerVersion bestVersion = default;

        foreach (var release in releases)
        {
            string tag = GitHubClient.NormalizeVersion(release.Tag);
            if (!SemVerVersion.TryParse(tag, out var version))
            {
                continue;
            }

            // 预发布判定取「tag 带后缀」或「GitHub 标了 prerelease」的并集：
            // 两者理论上由 CI 同步，但手工发版时经常只勾预发布而不改 tag，
            // 只认其中一边就会把 rc 推给稳定版用户。
            if ((version.IsPrerelease || release.IsPrerelease) && !currentIsPrerelease)
            {
                continue;
            }

            string? asset = isPortable ? release.PortableAssetUrl : release.SetupAssetUrl;
            if (string.IsNullOrEmpty(asset))
            {
                continue;
            }

            if (!IsNewerVersion(tag, currentVersion))
            {
                continue;
            }

            if (best is null || version.CompareTo(bestVersion) > 0)
            {
                best = release;
                bestVersion = version;
            }
        }

        return best;
    }

    /// <summary>语义化版本比较：latest > current 时返回 true（解析失败视为相等，不误报更新）。</summary>
    internal static bool IsNewerVersion(string latest, string current)
    {
        if (!SemVerVersion.TryParse(latest, out var latestVersion)
            || !SemVerVersion.TryParse(current, out var currentVersion))
        {
            return false;
        }

        return latestVersion.CompareTo(currentVersion) > 0;
    }

    /// <summary>便携资产：browser_download_url 中名称含 portable 的 zip（旧 windows-x86_64-portable target 语义）。</summary>
    internal static string? FindPortableAssetUrl(JsonElement release)
        => FindAssetUrl(release, static name =>
            name.Contains("portable", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("setup", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 安装版资产：名称含 <c>setup</c> 且以 <c>.exe</c> 结尾。
    ///
    /// 刻意排除含 <c>portable</c> 的名字：发布脚本按「便携 zip / 安装 exe」两类命名，
    /// 一旦哪天出现 <c>YuumiX_portable_setup.exe</c> 这类混合命名，两边都不该认它。
    /// </summary>
    internal static string? FindSetupAssetUrl(JsonElement release)
        => FindAssetUrl(release, static name =>
            name.Contains("setup", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("portable", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    private static string? FindAssetUrl(JsonElement release, Func<string, bool> predicate)
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
            if (!predicate(name))
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

        // 先写 .tmp、校验通过才改名：中断或截断的包绝不能以最终文件名落盘，
        // 否则用户下次点「安装」时才发现是半个包（那时应用已经退出，没法再给提示）。
        string tempPath = targetPath + ".tmp";
        long downloaded = 0;
        try
        {
            await using var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
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
        }
        catch
        {
            // 取消、断网、磁盘写满：一律清掉半截文件再往上抛，不给磁盘留垃圾。
            TryDeleteFile(tempPath, "清理未完成的更新包失败");
            throw;
        }

        // 服务器给了长度就必须对得上：连接被中途掐断时流读取未必报错，
        // 少几个字节的 zip/exe 到安装阶段才炸，代价比在这里拦住高得多。
        if (total is > 0 && downloaded != total.Value)
        {
            TryDeleteFile(tempPath, "清理被截断的更新包失败");
            throw new IOException($"更新包下载不完整（{downloaded}/{total.Value} 字节），已丢弃，请重试");
        }

        File.Move(tempPath, targetPath, overwrite: true);
        return targetPath;
    }

    /// <summary>
    /// 清理数据目录下的过期更新包：中断留下的 <c>.tmp</c>，以及版本不高于当前版本的旧包。
    ///
    /// 只删「确定用不上」的文件：正在等安装的新版本包（版本高于当前）必须保留，
    /// 否则用户下载完还没装、下次启动就被清掉，等于白下 300MB。
    /// 认不出命名的文件一律不动——这个目录是同级的派生数据区，宁可多留也不误删。
    /// </summary>
    public int CleanupDownloadCache()
    {
        string dir = Path.Combine(_paths.DataDirectory, "downloads");
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        const string prefix = "YuumiX-";
        int removed = 0;

        // 当前版本解析不出来就完全不清理：`SemVer` 比较失败等价于"不高于当前"，
        // 若照那个结果删文件，等于用一个解析错误去决定「删掉用户刚下好的包」。
        if (!SemVerVersion.TryParse(CurrentVersion, out var currentVersion))
        {
            return 0;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                bool stale;

                if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    stale = true;
                }
                else
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // 版本解析不出来（手工放进来的备份、重命名过的包）→ 认不出就不动，
                    // 与上面注释的「宁可多留也不误删」保持一致。
                    if (!SemVerVersion.TryParse(name[prefix.Length..], out var fileVersion))
                    {
                        continue;
                    }

                    stale = fileVersion.CompareTo(currentVersion) <= 0;
                }

                if (!stale)
                {
                    continue;
                }

                if (TryDeleteFile(file, "清理旧更新包失败"))
                {
                    removed++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "枚举更新包缓存目录失败");
        }

        if (removed > 0)
        {
            _logger?.LogInformation("已清理 {Count} 个过期更新包", removed);
        }

        return removed;
    }

    /// <summary>删除文件；失败只记日志（清理属尽力而为，不该影响调用方的正常流程）。</summary>
    private bool TryDeleteFile(string path, string failureMessage)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, failureMessage);
            return false;
        }
    }

    /// <summary>失败标记文件的完整路径。</summary>
    public string FailureMarkerPath => Path.Combine(_paths.DataDirectory, FailureMarkerFileName);

    /// <summary>
    /// 读取并清除「上次更新未完成」标记，返回原因码（见 <see cref="FailureCodeWaitExit"/> 等）。
    ///
    /// 两条刻意的规则：
    /// <list type="bullet">
    ///   <item>读一次即删：同一份失败不该每次启动都弹；</item>
    ///   <item>标记里的目标版本若已被追平（用户手动装了新版、或后来更新成功过一次），
    ///         直接当作无标记返回——此时再提示「更新未完成」只会误导。</item>
    /// </list>
    /// 任何 IO 失败都返回 <c>null</c>：这是提示功能，不能因为它挡住启动。
    /// </summary>
    public string? ConsumeFailureMarker()
    {
        string path = FailureMarkerPath;

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string raw = File.ReadAllText(path).Trim();
            File.Delete(path);

            if (raw.Length == 0)
            {
                return "unknown";
            }

            // 标记格式：<原因码> <目标版本>（脚本用空格分隔写入）
            string[] parts = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return "unknown";
            }

            if (parts.Length == 2 && !IsNewerVersion(parts[1], CurrentVersion))
            {
                _logger?.LogInformation(
                    "上次更新失败标记已过期（目标 {Target} ≤ 当前 {Current}），不再提示", parts[1], CurrentVersion);
                return null;
            }

            _logger?.LogInformation("检测到上次更新未完成的标记：{Code}（目标 {Target}）", parts[0], parts.Length == 2 ? parts[1] : "未知");
            return parts[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "读取更新失败标记失败，本次不提示");
            return null;
        }
    }

    /// <summary>
    /// 应用已下载的更新。两种形态都必须「先退出、后替换」，因此返回 <c>true</c> 时
    /// 调用方必须立即 <c>Application.Exit()</c>：
    /// <list type="bullet">
    ///   <item>安装版（setup.exe）：拉起延迟脚本，等本进程退出后以
    ///         <c>/SILENT /NORESTART /CLOSEAPPLICATIONS /DIR=&lt;当前目录&gt;</c> 静默重装，再启动新版本；</item>
    ///   <item>便携版（zip）：解压到临时目录并校验版本，然后拉起延迟脚本，
    ///         退出后用 robocopy 整目录覆盖，再启动新版本。</item>
    /// </list>
    ///
    /// 为什么两种形态都要等进程退出：
    /// <list type="bullet">
    ///   <item>便携版：运行中的 exe 与已映射的 dll 被锁住，只能覆盖未加载的文件；</item>
    ///   <item>安装版：Inno 的 <c>CloseApplications</c> 走 Restart Manager，会把进程直接杀掉，
    ///         我们的退出路径（刷日志、释放容器、写配置）来不及跑，未落盘的配置会丢。
    ///         自己先退出再让脚本启动安装器，才能保证「退出是干净的」。</item>
    /// </list>
    ///
    /// 为什么不再只替换 exe：发布包是自包含的（380+ 个 dll，含 Windows App SDK 与 WinUI 运行时），
    /// 只换主程序等于让新 exe 跑在旧 dll 上；某次升级一旦同时换了依赖库版本，进程启动即崩。
    /// <para>
    /// 失败留痕：脚本在替换/安装失败时会往 <c>&lt;数据目录&gt;/update-failed.txt</c> 写一行
    /// 「原因码 + 目标版本」，下次启动由 <see cref="ConsumeFailureMarker"/> 读出来提示一次。
    /// 为什么必须由脚本写：失败发生在主进程退出之后，那时应用已经没了，没有别的通道能告诉用户。
    /// </para>
    /// </summary>
    public static bool ApplyPendingUpdate(string downloadedPath, bool isPortable, string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        string currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位当前可执行文件");

        string targetDir = Path.GetDirectoryName(currentExe)
            ?? throw new InvalidOperationException("无法定位当前程序目录");

        // 标记目录必须存在，脚本里的重定向才不会失败（数据目录正常都已存在，这里只是兜底）。
        Directory.CreateDirectory(dataDirectory);

        if (!isPortable)
        {
            // 安装版：脚本落在临时目录，避免安装在只读位置时写不进去。
            string scriptRoot = Path.Combine(
                Path.GetTempPath(),
                $"YuumiX-setup-{CurrentVersion}-{Environment.ProcessId}");
            if (Directory.Exists(scriptRoot))
            {
                Directory.Delete(scriptRoot, recursive: true);
            }

            Directory.CreateDirectory(scriptRoot);
            string setupScript = Path.Combine(scriptRoot, "run-setup.cmd");
            File.WriteAllText(
                setupScript,
                BuildInstallerScript(
                    downloadedPath, targetDir, currentExe, Environment.ProcessId, dataDirectory, CurrentVersion),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            LaunchDetached(scriptRoot, setupScript);
            return true;
        }

        // 解压到系统临时目录而不是 data\downloads：robocopy 的源目录必须完全位于
        // 目标目录之外，否则「源在目标内」会让覆盖过程自我递归。
        string stagingRoot = Path.Combine(
            Path.GetTempPath(),
            $"YuumiX-update-{CurrentVersion}-{Environment.ProcessId}");
        if (Directory.Exists(stagingRoot))
        {
            Directory.Delete(stagingRoot, recursive: true);
        }

        Directory.CreateDirectory(stagingRoot);
        ZipFile.ExtractToDirectory(downloadedPath, stagingRoot, overwriteFiles: true);

        string newExe = Directory.EnumerateFiles(stagingRoot, "YuumiX.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("压缩包中未找到 YuumiX.exe");
        string newRoot = Path.GetDirectoryName(newExe)!;

        // 包内版本必须严格高于当前版本：下载被截断后恰好能解压的坏包、或指向旧版本的
        // 回退包，会把本来可用的程序覆盖成更旧的一份，这里直接拦住。
        string? incoming = FileVersionInfo.GetVersionInfo(newExe).FileVersion;
        if (incoming is null || !IsNewerVersion(incoming, CurrentVersion))
        {
            throw new InvalidOperationException(
                $"更新包版本 {incoming ?? "未知"} 不高于当前版本 {CurrentVersion}，已取消替换");
        }

        // 用户数据目录绝不能出现在覆盖源里：robocopy /E 会覆盖同名文件，
        // 包内一旦误带 data\config.json，用户配置就被静默冲掉。
        // 发布脚本目前只放 data\.keep，但那只是打包约定；这里用代码兜一层。
        // 删不掉就让它抛——宁可这次更新失败，也不能带着"可能覆盖用户数据"的源目录继续跑。
        foreach (string userDirectory in UserDataDirectoryNames)
        {
            string path = Path.Combine(newRoot, userDirectory);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        string scriptPath = Path.Combine(stagingRoot, "apply-update.cmd");
        File.WriteAllText(
            scriptPath,
            BuildApplyScript(
                newRoot, targetDir, currentExe, Environment.ProcessId, dataDirectory, CurrentVersion),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        LaunchDetached(stagingRoot, scriptPath);
        return true;
    }

    /// <summary>
    /// 以隐藏窗口方式启动延迟脚本。双层引号是 cmd /c 的既定写法：
    /// 整条命令要加引号，路径本身又要带引号。
    /// </summary>
    private static void LaunchDetached(string workingDirectory, string scriptPath)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{scriptPath}\"\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    /// <summary>
    /// 生成「进程退出后替换」的批处理。四个要点：
    /// <list type="bullet">
    ///   <item>轮询父进程 PID，最多等 60 秒，超时放弃而非硬覆盖，避免替换到一半的目录；</item>
    ///   <item>robocopy 用 <c>/E</c> 覆盖但不加 <c>/MIR</c>：<c>/MIR</c> 会删除目标里多出来的文件，
    ///         那会把用户的 <c>data</c>、<c>log</c> 一起清掉；</item>
    ///   <item>等待用 <c>ping</c> 而不是 <c>timeout</c>：无控制台窗口时 timeout 会因 stdin 重定向直接报错；</item>
    ///   <item><c>chcp 65001</c> + 脚本以 UTF-8 无 BOM 写入，保证用户名含中文时路径能正确解析。</item>
    /// </list>
    /// 设为 internal 是为了让单测能断言脚本内容（<c>/MIR</c> 误入会静默删掉用户数据，必须在门禁里锁住）。
    /// </summary>
    internal static string BuildApplyScript(
        string sourceDir, string targetDir, string exePath, int processId, string dataDirectory, string targetVersion)
    {
        string flagPath = Path.Combine(dataDirectory, FailureMarkerFileName);

        return $"""
            @echo off
            chcp 65001 >nul
            setlocal
            set "PID={processId}"
            set "SRC={EscapeForBatchSet(sourceDir)}"
            set "DST={EscapeForBatchSet(targetDir)}"
            set "FLAG={EscapeForBatchSet(flagPath)}"
            set "TARGET={EscapeForBatchSet(targetVersion)}"
            set /a tries=0
            :wait
            tasklist /FI "PID eq %PID%" /NH 2>nul | find /I "YuumiX.exe" >nul
            if errorlevel 1 goto apply
            set /a tries+=1
            if %tries% gtr 60 goto failwait
            ping -n 2 127.0.0.1 >nul
            goto wait
            :apply
            robocopy "%SRC%" "%DST%" /E /IS /IT /R:3 /W:1 /NFL /NDL /NJH /NJS /NP >nul
            if errorlevel 8 goto failcopy
            del /q "%FLAG%" 2>nul
            start "" /D "%DST%" "{EscapeForBatchSet(exePath)}"
            goto cleanup
            :failwait
            >"%FLAG%" echo {FailureCodeWaitExit} %TARGET%
            goto cleanup
            :failcopy
            >"%FLAG%" echo {FailureCodeCopyFailed} %TARGET%
            :cleanup
            rd /s /q "%SRC%" 2>nul
            del /q "%~f0" 2>nul
            """;
    }

    /// <summary>
    /// 生成「等主进程退出后静默重装」的批处理（安装版）。
    ///
    /// 参数说明：
    /// <list type="bullet">
    ///   <item><c>/SILENT</c>：不显示向导但保留进度窗口——用户点了「安装更新并重启」后
    ///         应用会消失，必须有可见反馈，否则会被当成闪退；</item>
    ///   <item><c>/DIR</c>：显式指定当前 exe 目录。不传的话 Inno 用注册表里记录的上次
    ///         安装路径，用户改过目录时会装到意想不到的位置；</item>
    ///   <item><c>/CLOSEAPPLICATIONS</c>：兜底关掉残留实例，避免文件被锁导致安装失败；</item>
    ///   <item>等待上限 120 次轮询（约 120 秒）：安装器本身要解压 380+ 个文件，比便携版的
    ///         目录覆盖慢，超时上限相应放宽；</item>
    ///   <item>失败分支不启动应用：装坏了还拉起旧版本会掩盖失败。</item>
    /// </list>
    /// 设为 internal 是为了让单测能断言参数组合（缺 <c>/DIR</c> 会装到别处，属静默事故）。
    /// </summary>
    internal static string BuildInstallerScript(
        string setupPath, string targetDir, string exePath, int processId, string dataDirectory, string targetVersion)
    {
        string flagPath = Path.Combine(dataDirectory, FailureMarkerFileName);

        return $"""
            @echo off
            chcp 65001 >nul
            setlocal
            set "PID={processId}"
            set "SETUP={EscapeForBatchSet(setupPath)}"
            set "DST={EscapeForBatchSet(targetDir)}"
            set "EXE={EscapeForBatchSet(exePath)}"
            set "FLAG={EscapeForBatchSet(flagPath)}"
            set "TARGET={EscapeForBatchSet(targetVersion)}"
            set /a tries=0
            :wait
            tasklist /FI "PID eq %PID%" /NH 2>nul | find /I "YuumiX.exe" >nul
            if errorlevel 1 goto apply
            set /a tries+=1
            if %tries% gtr 120 goto failwait
            ping -n 2 127.0.0.1 >nul
            goto wait
            :apply
            "%SETUP%" /SILENT /NORESTART /CLOSEAPPLICATIONS /SUPPRESSMSGBOXES /DIR="%DST%"
            if errorlevel 1 goto failsetup
            del /q "%FLAG%" 2>nul
            start "" /D "%DST%" "%EXE%"
            goto cleanup
            :failwait
            >"%FLAG%" echo {FailureCodeWaitExit} %TARGET%
            goto cleanup
            :failsetup
            >"%FLAG%" echo {FailureCodeSetupFailed} %TARGET%
            :cleanup
            del /q "%~f0" 2>nul
            """;
    }

    /// <summary>
    /// 覆盖源里必须剔除的用户目录名。
    ///
    /// <c>data</c> 是便携版的数据目录（配置、数据库、缓存），<c>log</c> 是固定日志目录；
    /// 两者都属于用户资产，不接受被更新包覆盖。安装版数据在 <c>%APPDATA%\Yuumi</c>，
    /// exe 目录下即使出现同名目录也不应被包内内容替换。
    /// </summary>
    private static readonly string[] UserDataDirectoryNames = ["data", "log"];

    /// <summary>
    /// 转义批处理 <c>set</c> 赋值里的 <c>%</c>。
    ///
    /// cmd 解析 <c>set "VAR=%USERPROFILE%\x"</c> 时会先展开变量；路径里出现字面量百分号
    /// （例如 <c>C:\100%\app</c>）会被吃掉或展开成空串，脚本随后就去覆盖一个错的目录——
    /// 这是静默事故，不会报错。写成 <c>%%</c> 才能原样落进变量。
    /// 引号内的空格与 <c>&amp;</c> 本身是安全的（<c>set "VAR=a&amp;b"</c> 正常），无需额外处理。
    /// </summary>
    private static string EscapeForBatchSet(string value) => value.Replace("%", "%%", StringComparison.Ordinal);
}
