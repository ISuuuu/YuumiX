using Microsoft.Extensions.Logging;
using YuumiX.Core.Runtime;

namespace YuumiX.Core.Persistence;

/// <summary>
/// 旧版 Yuumi 数据目录的一次性迁移：YuumiX 首次启动（自己的 <c>config.json</c> 还不存在）时，
/// 把旧目录里的配置与用户数据复制过来；此后两个版本各自独立读写，互不影响。
///
/// 复制是拷贝而非移动：旧目录保持原样，旧版 Yuumi 继续可用。
/// 幂等保证：以「自己的 config.json 是否已存在」为界——存在即视为已完成迁移（或本就是老用户），
/// 不再触碰任何文件。旧目录里连 config.json 都没有时视为旧版从未初始化，同样不迁移。
/// </summary>
public static class LegacyDataMigrator
{
    /// <summary>
    /// 随配置一起迁移的用户数据文件（相对数据目录的路径）。
    /// <c>saved_players.db</c> 是 WAL 模式 SQLite（路人集 + 上传暂存队列共用），
    /// 必须连 <c>-wal</c>/<c>-shm</c> 一起拷，否则会丢最近一次 checkpoint 之前的写入；
    /// 缓存（<c>cache/</c>、<c>downloads/</c> 等）可自行重建，不迁移。
    /// </summary>
    private static readonly string[] UserDataFiles =
    [
        "saved_players.db",
        "saved_players.db-wal",
        "saved_players.db-shm",
        "search_history.json",
    ];

    /// <summary>
    /// 需要时执行迁移。任何失败都不抛出：迁移失败时本次按默认配置启动，
    /// 旧目录未被改动，下次启动会重试整批。
    /// </summary>
    public static void Migrate(RuntimePaths paths, ILogger? logger = null)
    {
        if (paths.LegacyDataDirectory is not { } legacyDir)
        {
            return; // 便携版没有可探测的旧版目录
        }

        var ownConfigPath = Path.Combine(paths.DataDirectory, "config.json");
        if (File.Exists(ownConfigPath))
        {
            return;
        }

        var legacyConfigPath = Path.Combine(legacyDir, "config.json");
        if (!File.Exists(legacyConfigPath))
        {
            return;
        }

        // 迁移先于 ConfigStore 的建目录动作执行，首次启动时自己的数据目录还不存在
        Directory.CreateDirectory(paths.DataDirectory);

        // config.json 是本次迁移的锚点：它失败时用户数据保持原状，下次启动重试整批，
        // 避免「配置是默认的、收藏却是旧的」这类半迁移状态固化为幂等前提。
        if (!CopyFile(legacyConfigPath, ownConfigPath, logger))
        {
            logger?.LogWarning("从旧版 Yuumi 迁移配置失败，本次跳过: {Source}", legacyConfigPath);
            return;
        }

        foreach (var file in UserDataFiles)
        {
            var source = Path.Combine(legacyDir, file);
            if (File.Exists(source))
            {
                CopyFile(source, Path.Combine(paths.DataDirectory, file), logger);
            }
        }

        logger?.LogInformation(
            "已从旧版 Yuumi 数据目录复制配置与用户数据: {LegacyDir} → {DataDir}", legacyDir, paths.DataDirectory);
    }

    /// <summary>经临时文件复制，避免进程中断留下半个文件（半截 SQLite 库无法打开）。</summary>
    private static bool CopyFile(string source, string target, ILogger? logger)
    {
        var tmp = target + ".migrate.tmp";
        try
        {
            File.Copy(source, tmp, overwrite: true);
            File.Move(tmp, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "复制迁移文件失败: {Source} → {Target}", source, target);
            TryDelete(tmp, logger);
            return false;
        }
    }

    private static void TryDelete(string path, ILogger? logger)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 残留的 .migrate.tmp 无碍，下一次复制会以 overwrite 覆盖它
            logger?.LogDebug(ex, "清理迁移临时文件失败: {Path}", path);
        }
    }
}
