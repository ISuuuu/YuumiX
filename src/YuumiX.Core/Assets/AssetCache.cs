using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Assets;

/// <summary>
/// 资源磁盘缓存，对应原 <c>try_read_asset_cache</c> / <c>write_asset_cache</c>。
///
/// 文件名为路径的 FNV-1a 哈希，与 Rust 实现逐字节一致——这一点是刻意的：
/// Yuumi 与 YuumiX 共用 <c>%APPDATA%/Yuumi</c>，同名哈希意味着两版可以互相复用缓存，
/// 迁移期来回切换不会退化成「每次都重新下载全站图标」。
///
/// 三条硬性行为：
/// <list type="number">
///   <item><b>7 天 TTL</b>：按文件写入时间判断，过期即物理删除（不删会让目录无限膨胀）；</item>
///   <item><b>原子写入</b>：先写 <c>.tmp</c> 再 <c>File.Move(overwrite)</c>，读方永不会看到半个文件；</item>
///   <item><b>兼容无扩展名文件</b>：旧版本写入的就是裸哈希名，读取时最后再探一次。</item>
/// </list>
/// </summary>
public sealed class AssetCache
{
    /// <summary>缓存有效期。与原实现 <c>CACHE_TTL</c> 对齐。</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    /// <summary>
    /// 读取时依次尝试的扩展名。带扩展名在前（本工程写入形态），裸哈希收尾（旧版本形态）。
    /// 顺序固定意味着同一路径的命中结果是确定的，不会因目录里恰好残留别的扩展名而抖动。
    /// </summary>
    private static readonly string[] CandidateExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", string.Empty,
    ];

    private readonly TimeProvider _clock;
    private readonly ILogger<AssetCache>? _logger;

    /// <param name="directory">缓存目录，形如 <c>&lt;数据目录&gt;/cache/assets</c>。不存在时按需创建。</param>
    /// <param name="clock">时钟（测试注入过期时间用）。</param>
    /// <param name="logger">可选日志。</param>
    public AssetCache(string directory, TimeProvider? clock = null, ILogger<AssetCache>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory = directory;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>缓存根目录。</summary>
    public string Directory { get; }

    /// <summary>
    /// 查缓存。命中返回绝对路径；未命中、已过期或不可读返回 <c>null</c>。
    /// 过期文件在查询过程中直接被删除——读取路径本来就是唯一会遍历到它们的时机。
    /// </summary>
    public string? TryGetPath(string assetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);

        var hash = Hash(assetPath);

        foreach (var extension in CandidateExtensions)
        {
            var candidate = Path.Combine(Directory, hash + extension);

            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var written = File.GetLastWriteTimeUtc(candidate);
                var age = _clock.GetUtcNow().UtcDateTime - written;

                if (age > Ttl)
                {
                    _logger?.LogDebug("资源缓存过期，已删除: {Path}", candidate);
                    TryDelete(candidate);
                    continue;
                }

                return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单条缓存损坏不应让整页图标失败，继续探下一个候选
                _logger?.LogDebug(ex, "读取资源缓存失败: {Path}", candidate);
            }
        }

        return null;
    }

    /// <summary>写入缓存。失败只记日志——缓存是加速手段，不是正确性依赖。</summary>
    public void Store(string assetPath, ReadOnlySpan<byte> content, string? contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);

        var target = Path.Combine(Directory, Hash(assetPath) + AssetPathMapper.ExtensionForContentType(contentType));
        var temporary = target + ".tmp";

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
            }

            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "写入资源缓存失败: {Path}", target);
            TryDelete(temporary);
        }
    }

    /// <summary>清空缓存（保留目录本身）。版本哨兵变更时调用。</summary>
    public void Clear()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
        {
            TryDelete(file);
        }
    }

    /// <summary>
    /// FNV-1a 64 位哈希，输出 16 位小写十六进制。
    /// 与 Rust 侧 <c>stable_hash</c> 完全一致，这是缓存可跨版本复用的前提。
    /// </summary>
    public static string Hash(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        const ulong OffsetBasis = 0xCBF29CE484222325;
        const ulong Prime = 0x100000001B3;

        var hash = OffsetBasis;

        foreach (var value in Encoding.UTF8.GetBytes(path))
        {
            hash ^= value;
            hash *= Prime;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "删除缓存文件失败: {Path}", path);
        }
    }
}
