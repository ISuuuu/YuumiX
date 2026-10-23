using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Assets;

/// <summary>
/// 缓存版本哨兵：LCU 客户端版本变化时清空资源缓存。
///
/// 为什么 7 天 TTL 不够：TTL 只能覆盖「文件坏了」，覆盖不了「版本升级后图标语义变了」——
/// 例如同一个英雄换图标、装备重做后同名文件内容不同。这类残留在 7 天内表现为
/// 「首页几张图是旧的」，而用户看到的现象是「缓存坏了」，归因成本极高。
///
/// 版本号来自探活响应 <c>/system/v1/builds</c> 的 <c>version</c> 字段，
/// 由 <c>LcuConnection</c> 在每次探活成功后交给这里。
/// </summary>
public sealed class AssetCacheStamp
{
    /// <summary>哨兵文件名。刻意用点开头，与哈希命名的缓存文件区分。</summary>
    public const string StampFileName = ".patch";

    private readonly string _directory;
    private readonly ILogger<AssetCacheStamp>? _logger;
    private readonly object _gate = new();

    /// <param name="cacheDirectory">缓存目录，与 <see cref="AssetCache"/> 同一个目录。</param>
    /// <param name="logger">可选日志。</param>
    public AssetCacheStamp(string cacheDirectory, ILogger<AssetCacheStamp>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        _directory = cacheDirectory;
        _logger = logger;
    }

    /// <summary>
    /// 应用客户端版本。版本变化时清空缓存并写入新版本，返回是否发生了清空。
    /// 版本为空或读写失败时不做任何事——缓存清理失败必须降级为「继续用旧缓存」，
    /// 不能让一个运维动作把资源加载链整体打断。
    /// </summary>
    public bool Apply(string? buildVersion)
    {
        if (string.IsNullOrWhiteSpace(buildVersion))
        {
            return false;
        }

        lock (_gate)
        {
            var stampPath = Path.Combine(_directory, StampFileName);

            try
            {
                var previous = File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : null;
                if (string.Equals(previous, buildVersion, StringComparison.Ordinal))
                {
                    return false;
                }

                var removed = ClearExceptStamp();
                Directory.CreateDirectory(_directory);
                File.WriteAllText(stampPath, buildVersion);

                _logger?.LogInformation(
                    "客户端版本变化（{Previous} → {Current}），已清空 {Removed} 个资源缓存文件",
                    previous ?? "无",
                    buildVersion,
                    removed);

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "资源缓存版本哨兵写入失败，本次沿用旧缓存");
                return false;
            }
        }
    }

    private int ClearExceptStamp()
    {
        if (!Directory.Exists(_directory))
        {
            return 0;
        }

        var removed = 0;

        foreach (var file in Directory.EnumerateFiles(_directory))
        {
            if (string.Equals(Path.GetFileName(file), StampFileName, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(ex, "删除缓存文件失败: {Path}", file);
            }
        }

        return removed;
    }
}
