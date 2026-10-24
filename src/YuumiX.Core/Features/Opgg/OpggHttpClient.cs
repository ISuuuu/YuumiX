using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Persistence;
using YuumiX.Core.Runtime;

namespace YuumiX.Core.Features.Opgg;

/// <summary>
/// OP.GG HTTP 客户端与双级缓存（一级内存 10 分钟 / 100 条淘汰，二级磁盘 24 小时 FNV-1a 哈希原子写）。
/// </summary>
public sealed class OpggHttpClient : IDisposable
{
    public const int MaxMemoryEntries = 100;
    public static readonly TimeSpan MemoryCacheTtl = TimeSpan.FromSeconds(600);
    public static readonly TimeSpan DiskCacheTtl = TimeSpan.FromSeconds(86400);
    public const int MaxRetries = 3;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly Func<AppConfig> _configProvider;
    private readonly RuntimePaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpggHttpClient>? _logger;
    private readonly HttpMessageHandler? _mockHandler;

    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, (string Data, DateTimeOffset InsertedAt)> _memoryCache = new();

    private readonly Lock _clientLock = new();
    private bool _cachedEnableProxy;
    private string _cachedProxyAddr = "";
    private HttpClient? _httpClient;
    private bool _disposed;

    public OpggHttpClient(
        Func<AppConfig> configProvider,
        RuntimePaths paths,
        ILogger<OpggHttpClient>? logger = null,
        HttpMessageHandler? mockHandler = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(paths);

        _configProvider = configProvider;
        _paths = paths;
        _logger = logger;
        _mockHandler = mockHandler;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// OP.GG 磁盘缓存目录路径：&lt;DataDirectory&gt;/cache/opgg/。
    /// </summary>
    public string DiskCacheDirectory => Path.Combine(_paths.DataDirectory, "cache", "opgg");

    /// <summary>
    /// 计算输入字符串的 FNV-1a 64 位哈希值（与旧版 Rust 保持跨平台跨版本绝对一致）。
    /// </summary>
    public static ulong ComputeFnv1a64(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ulong hash = 0xcbf29ce484222325UL;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= 0x00000100000001B3UL;
        }
        return hash;
    }

    /// <summary>
    /// 获取缓存文件名：16 位小写十六进制 + ".json"。
    /// </summary>
    public static string GetCacheFileName(string key) => $"{ComputeFnv1a64(key):x16}.json";

    /// <summary>
    /// 生成标准梯队/出装缓存键。
    /// </summary>
    public static string BuildCacheKey(string region, string mode, string tier, int? championId, string? position)
    {
        return $"{region}_{mode}_{tier}_{(championId.HasValue ? championId.Value.ToString() : "null")}_{(string.IsNullOrWhiteSpace(position) ? "null" : position)}";
    }

    /// <summary>
    /// 读取缓存（内存未命中时回退磁盘缓存并回填一级内存）。
    /// </summary>
    public async Task<string?> GetCachedAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        // 1. 尝试一级内存缓存
        var now = _timeProvider.GetUtcNow();
        lock (_cacheLock)
        {
            if (_memoryCache.TryGetValue(cacheKey, out var memEntry))
            {
                if (now - memEntry.InsertedAt < MemoryCacheTtl)
                {
                    _logger?.LogDebug("OP.GG 一级内存缓存命中: {Key}", cacheKey);
                    return memEntry.Data;
                }

                _memoryCache.Remove(cacheKey);
            }
        }

        // 2. 尝试二级磁盘缓存
        var diskData = await ReadDiskCachedAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (diskData is not null)
        {
            _logger?.LogDebug("OP.GG 二级磁盘缓存命中并回填内存: {Key}", cacheKey);
            PutMemoryCached(cacheKey, diskData);
            return diskData;
        }

        return null;
    }

    /// <summary>
    /// 写入缓存（内存 + 磁盘异步双写）。
    /// </summary>
    public void PutCached(string cacheKey, string data)
    {
        PutMemoryCached(cacheKey, data);
        _ = WriteDiskCachedAsync(cacheKey, data);
    }

    private void PutMemoryCached(string cacheKey, string data)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_cacheLock)
        {
            if (_memoryCache.Count >= MaxMemoryEntries && !_memoryCache.ContainsKey(cacheKey))
            {
                // 淘汰最早插入的条目
                string? oldestKey = null;
                var oldestTime = DateTimeOffset.MaxValue;
                foreach (var (k, v) in _memoryCache)
                {
                    if (v.InsertedAt < oldestTime)
                    {
                        oldestTime = v.InsertedAt;
                        oldestKey = k;
                    }
                }

                if (oldestKey is not null)
                {
                    _memoryCache.Remove(oldestKey);
                }
            }

            _memoryCache[cacheKey] = (data, now);
        }
    }

    private async Task<string?> ReadDiskCachedAsync(string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            var dir = DiskCacheDirectory;
            if (!Directory.Exists(dir)) return null;

            var fileName = GetCacheFileName(cacheKey);
            var filePath = Path.Combine(dir, fileName);
            if (!File.Exists(filePath)) return null;

            var lastWrite = File.GetLastWriteTimeUtc(filePath);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (now - lastWrite > DiskCacheTtl)
            {
                try
                {
                    File.Delete(filePath);
                }
                catch
                {
                    // 物理删除过期文件失败忽略
                }
                return null;
            }

            return await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取 OP.GG 磁盘缓存异常: {Key}", cacheKey);
            return null;
        }
    }

    private async Task WriteDiskCachedAsync(string cacheKey, string data)
    {
        try
        {
            var dir = DiskCacheDirectory;
            Directory.CreateDirectory(dir);

            var fileName = GetCacheFileName(cacheKey);
            var targetPath = Path.Combine(dir, fileName);
            var tempPath = Path.Combine(dir, $"{fileName}.tmp");

            await File.WriteAllTextAsync(tempPath, data).ConfigureAwait(false);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "写入 OP.GG 磁盘缓存异常: {Key}", cacheKey);
        }
    }

    /// <summary>
    /// 获取或按代理配置重建 HttpClient。
    /// </summary>
    private HttpClient GetOrCreateHttpClient()
    {
        if (_mockHandler is not null)
        {
            lock (_clientLock)
            {
                return _httpClient ??= new HttpClient(_mockHandler, disposeHandler: false)
                {
                    Timeout = RequestTimeout
                };
            }
        }

        var cfg = _configProvider();
        var enableProxy = cfg.General.EnableHttpProxy;
        var proxyAddr = cfg.General.HttpProxyAddr ?? "";

        lock (_clientLock)
        {
            if (_httpClient is not null &&
                _cachedEnableProxy == enableProxy &&
                string.Equals(_cachedProxyAddr, proxyAddr, StringComparison.OrdinalIgnoreCase))
            {
                return _httpClient;
            }

            _httpClient?.Dispose();

            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };

            if (enableProxy && !string.IsNullOrWhiteSpace(proxyAddr))
            {
                var proxyUrl = proxyAddr.Contains("://", StringComparison.Ordinal)
                    ? proxyAddr
                    : $"http://{proxyAddr}";

                try
                {
                    handler.Proxy = new WebProxy(proxyUrl);
                    _logger?.LogInformation("OP.GG 请求已配置代理: {ProxyUrl}", proxyUrl);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "无效的 OP.GG 代理地址: {ProxyAddr}", proxyAddr);
                }
            }

            _httpClient = new HttpClient(handler)
            {
                Timeout = RequestTimeout
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);

            _cachedEnableProxy = enableProxy;
            _cachedProxyAddr = proxyAddr;
            return _httpClient;
        }
    }

    /// <summary>
    /// GET 请求：带双级缓存与最多 3 次传输层重试（500ms 间隔）。
    /// </summary>
    public async Task<string> GetJsonAsync(
        string url,
        IReadOnlyList<KeyValuePair<string, string>> queryParams,
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);

        // 尝试缓存
        var cached = await GetCachedAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        // 拼接 Query
        var requestUrl = url;
        if (queryParams.Count > 0)
        {
            var query = string.Join("&", queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            requestUrl = url.Contains('?', StringComparison.Ordinal)
                ? $"{url}&{query}"
                : $"{url}?{query}";
        }

        var client = GetOrCreateHttpClient();
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await client.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                PutCached(cacheKey, body);
                return body;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                lastError = ex.Message;
                _logger?.LogWarning(
                    ex,
                    "OP.GG 请求失败（{Attempt}/{MaxRetries}），将在 {Delay}ms 后重试: {Url}",
                    attempt,
                    MaxRetries,
                    RetryDelay.TotalMilliseconds,
                    requestUrl);

                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        throw new HttpRequestException($"OP.GG 请求失败（已重试 {MaxRetries} 次）: {lastError}");
    }

    /// <summary>
    /// 清除内存缓存。
    /// </summary>
    public void ClearMemoryCache()
    {
        lock (_cacheLock)
        {
            _memoryCache.Clear();
        }
    }

    /// <summary>
    /// 清除磁盘缓存文件。
    /// </summary>
    public void ClearDiskCache()
    {
        try
        {
            var dir = DiskCacheDirectory;
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "清理 OP.GG 磁盘缓存目录异常");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_clientLock)
        {
            _httpClient?.Dispose();
            _httpClient = null;
        }
    }
}
