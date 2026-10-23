using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Assets;

/// <summary>
/// 资源聚合：缓存 → 各来源顺序尝试 → 成功即落盘（ADR-11）。
///
/// 两个必须存在的机制：
/// <list type="bullet">
///   <item><b>在途去重</b>：列表滚动时同一个头像会被十几个项同时请求。没有它，
///         一次滚动就能对 CDN 打出十几倍流量，且先到的写入会与后到的互相覆盖；</item>
///   <item><b>并发上限</b>：8 路，对齐原 <c>ASSET_BATCH_CONCURRENCY</c>。
///         不加限制时整屏图标会同时抢占连接池，反而让首个可视项迟迟不出图。</item>
/// </list>
///
/// 取消语义（有意为之）：共享的在途请求**不携带**调用方的取消令牌。
/// 一个页面离开导致某个头像请求取消，不应该让另一个页面正在等的同一个头像也失败；
/// 代价是没人等待的请求仍会跑完，其上界由各来源自身的超时保证。
/// </summary>
public sealed class AssetService : IDisposable
{
    /// <summary>并发上限。与原实现一致。</summary>
    public const int DefaultConcurrency = 8;

    private readonly AssetCache _cache;
    private readonly IReadOnlyList<IAssetSource> _sources;
    private readonly SemaphoreSlim _permits;
    private readonly ConcurrentDictionary<string, Lazy<Task<AssetPayload?>>> _inFlight = new(StringComparer.Ordinal);
    private readonly ILogger<AssetService>? _logger;

    /// <param name="cache">磁盘缓存。</param>
    /// <param name="sources">来源链，按顺序尝试（生产为 LCU → CDragon）。</param>
    /// <param name="maxConcurrency">并发上限。</param>
    /// <param name="logger">可选日志。</param>
    public AssetService(
        AssetCache cache,
        IEnumerable<IAssetSource> sources,
        int maxConcurrency = DefaultConcurrency,
        ILogger<AssetService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        _cache = cache;
        _sources = [.. sources];
        _permits = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _logger = logger;
    }

    /// <summary>来源名称列表，便于诊断页展示链路。</summary>
    public IReadOnlyList<string> SourceNames => [.. _sources.Select(source => source.Name)];

    /// <summary>
    /// 同步尝试从磁盘缓存获取已存在的本地绝对路径；未命中返回 <c>null</c>。
    /// 用于 UI 组件初始化时直接获取初值，避免已缓存的资源闪烁或因异步时序导致占位残留。
    /// </summary>
    public string? TryGetCachedPath(string assetPath) => _cache.TryGetPath(assetPath);

    /// <summary>
    /// 供 UI 使用的主入口（ADR-01）：返回可供 <c>Image("file:///...")</c> 引用的本地绝对路径。
    /// 未命中时触发一次完整解析并落盘；彻底失败返回 <c>null</c>，由调用方显示占位图。
    /// </summary>
    public async Task<string?> GetCachedPathAsync(string assetPath, CancellationToken cancellationToken = default)
    {
        var cached = _cache.TryGetPath(assetPath);
        if (cached is not null)
        {
            return cached;
        }

        var payload = await GetAsync(assetPath, cancellationToken).ConfigureAwait(false);
        return payload is null ? null : _cache.TryGetPath(assetPath);
    }

    /// <summary>取字节。命中缓存时同样直接返回，不产生网络请求。</summary>
    public async Task<AssetPayload?> GetAsync(string assetPath, CancellationToken cancellationToken = default)
    {
        if (!AssetPathMapper.IsAllowedPath(assetPath))
        {
            // 白名单是「本项目到底会拉哪些资源」的边界；越界通常是上游解析出了脏数据，
            // 记一条警告即可，不抛异常——一张图不显示不应该让整个列表崩掉。
            _logger?.LogWarning("拒绝解析不在白名单内的资源路径: {Path}", assetPath);
            return null;
        }

        var lazy = _inFlight.GetOrAdd(
            assetPath,
            key => new Lazy<Task<AssetPayload?>>(() => FetchAndReleaseAsync(key), LazyThreadSafetyMode.ExecutionAndPublication));

        return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _permits.Dispose();

    private async Task<AssetPayload?> FetchAndReleaseAsync(string assetPath)
    {
        try
        {
            if (_cache.TryGetPath(assetPath) is { } cachedPath)
            {
                var cachedBytes = await TryReadAsync(cachedPath).ConfigureAwait(false);
                if (cachedBytes is not null)
                {
                    return new AssetPayload(cachedBytes, AssetPathMapper.GuessContentType(assetPath), "Cache");
                }
            }

            foreach (var source in _sources)
            {
                await _permits.WaitAsync().ConfigureAwait(false);
                AssetPayload? payload;

                try
                {
                    payload = await source.TryGetAsync(assetPath, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _permits.Release();
                }

                if (payload is null)
                {
                    continue;
                }

                _cache.Store(assetPath, payload.Bytes, payload.ContentType);
                return payload;
            }

            _logger?.LogDebug("所有来源均未提供资源: {Path}", assetPath);
            return null;
        }
        catch (Exception ex)
        {
            // 去重表里挂着的是 Task，异常若不在此吞掉会变成未观察异常；
            // 资源失败一律表现为「没有图片」，不向上传播。
            _logger?.LogWarning(ex, "资源解析异常: {Path}", assetPath);
            return null;
        }
        finally
        {
            _inFlight.TryRemove(assetPath, out _);
        }
    }

    private async Task<byte[]?> TryReadAsync(string filePath)
    {
        try
        {
            return await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "读取缓存文件失败: {Path}", filePath);
            return null;
        }
    }
}
