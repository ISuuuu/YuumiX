using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Persistence;

/// <summary>
/// 配置文件读写，对标原项目 <c>config.rs</c> 的 <c>load</c> / <c>save</c> / <c>take_load_error</c>。
///
/// 三条关键行为（均为延续原实现的既有保障）：
/// <list type="number">
///   <item>写入采用「先写 <c>.tmp</c> 再原子替换」，避免进程在写入中途退出导致配置文件被截断；</item>
///   <item>解析失败时把原文件备份为 <c>config.json.backup.&lt;unix秒&gt;</c>，并写 <c>config.json.error</c>
///         供 UI 读取提示，然后降级为默认配置；</item>
///   <item>文件不存在时生成默认配置并立即落盘。</item>
/// </list>
/// </summary>
public sealed class ConfigStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>写回备份的保留份数（ADR-12）。</summary>
    public const int MaxBackups = 10;

    private readonly ILogger<ConfigStore> _logger;

    /// <summary>本次进程是否已做过「会话首写备份」。见 ADR-12 决策二。</summary>
    private bool _backedUpThisSession;

    public ConfigStore(string configFilePath, ILogger<ConfigStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);
        ArgumentNullException.ThrowIfNull(logger);

        FilePath = configFilePath;
        _logger = logger;
    }

    /// <summary>配置文件的完整路径。</summary>
    public string FilePath { get; }

    /// <summary>损坏备份文件路径前缀所对应的错误提示文件。</summary>
    public string ErrorFilePath => FilePath + ".error";

    /// <summary>
    /// 从磁盘加载配置。任何失败都不会抛出：解析失败走备份 + 降级路径，IO 失败仅记录日志。
    /// </summary>
    public AppConfig Load()
    {
        EnsureDirectory();

        if (!File.Exists(FilePath))
        {
            var created = new AppConfig();
            _logger.LogInformation("配置文件不存在，已生成默认配置: {Path}", FilePath);
            Save(created);
            return created;
        }

        string text;
        try
        {
            text = File.ReadAllText(FilePath, Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "读取配置文件失败，使用默认配置: {Path}", FilePath);
            return new AppConfig();
        }

        try
        {
            return ParseAndMigrate(text);
        }
        catch (JsonException ex)
        {
            return RecoverFromCorrupt(ex);
        }
    }

    /// <summary>
    /// 将配置写回磁盘。失败时记录日志并原样抛出，交由调用方（设置页）决定如何提示用户——
    /// 静默吞掉写失败会让用户以为设置已保存。
    /// </summary>
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        EnsureDirectory();

        var tmpPath = FilePath + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(config, ConfigSchema.JsonOptions);
            File.WriteAllText(tmpPath, json, Utf8NoBom);
            File.Move(tmpPath, FilePath, overwrite: true);
            _logger.LogDebug("配置已保存: {Path}", FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存配置失败: {Path}", FilePath);
            TryDelete(tmpPath);
            throw;
        }
    }

    /// <summary>
    /// 设置页专用的写回入口：本次进程首次写回前先备份原文件，再落盘（ADR-12）。
    ///
    /// 为什么不是「每次写都备份」：设置页的每次点击都是一次写回，用户连改六项就会留下六个
    /// 时间戳文件，而真正需要保护的是「本进程第一次改写配置文件（可能刚从旧版 Yuumi 拷贝而来）」这一步。
    ///
    /// 备份失败**不写盘**并抛出：无法复制原文件通常意味着磁盘或权限已出问题，
    /// 此时继续覆盖原配置，风险远大于「这次没保存上」。
    /// </summary>
    /// <returns>本次生成的备份文件路径；本会话已备份过则为 <c>null</c>。</returns>
    public string? SaveWithBackup(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var backupPath = _backedUpThisSession ? null : CreateSessionBackup();
        Save(config);
        return backupPath;
    }

    /// <summary>列出写回备份，按时间戳倒序（最新在前）。供设置页「关于」与排障使用。</summary>
    public IReadOnlyList<string> ListBackups()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var pattern = Path.GetFileName(FilePath) + ".bak.*";
        try
        {
            return [.. Directory.GetFiles(directory, pattern).OrderByDescending(static p => p, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "枚举配置备份失败: {Path}", FilePath);
            return [];
        }
    }

    /// <summary>
    /// 读取并清除上一次加载失败留下的错误提示（供 UI 展示后即弃）。
    /// </summary>
    public string? TakeLoadError()
    {
        if (!File.Exists(ErrorFilePath))
        {
            return null;
        }

        try
        {
            var message = File.ReadAllText(ErrorFilePath, Utf8NoBom);
            File.Delete(ErrorFilePath);
            return message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "读取配置错误提示失败: {Path}", ErrorFilePath);
            return null;
        }
    }

    /// <summary>
    /// 复制当前配置文件为 <c>config.json.bak.&lt;yyyyMMdd-HHmmss&gt;</c>。
    /// 文件不存在时只置会话标记（用户首次启动还没有「原版内容」可保护）。
    /// </summary>
    private string? CreateSessionBackup()
    {
        if (!File.Exists(FilePath))
        {
            _backedUpThisSession = true;
            return null;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = $"{FilePath}.bak.{stamp}";

        // 同一秒内多次启动（自动化测试常见）时追加序号，避免覆盖上一份备份
        var attempt = 1;
        while (File.Exists(path))
        {
            path = $"{FilePath}.bak.{stamp}-{attempt++}";
        }

        try
        {
            File.Copy(FilePath, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "写回前备份失败，已中止本次写回: {Path}", FilePath);
            throw;
        }

        _backedUpThisSession = true;
        _logger.LogInformation("写回前已备份配置: {BackupPath}", path);
        PruneBackups();
        return path;
    }

    /// <summary>只保留最近 <see cref="MaxBackups"/> 份写回备份，其余物理删除。</summary>
    private void PruneBackups()
    {
        var backups = ListBackups();
        if (backups.Count <= MaxBackups)
        {
            return;
        }

        foreach (var stale in backups.Skip(MaxBackups))
        {
            TryDelete(stale);
        }
    }

    private AppConfig ParseAndMigrate(string text)    {
        if (JsonNode.Parse(text) is not JsonObject root)
        {
            throw new JsonException("配置文件的根节点不是 JSON 对象");
        }

        ConfigSchema.NormalizeLegacyKeys(root);

        var config = root.Deserialize<AppConfig>(ConfigSchema.JsonOptions) ?? new AppConfig();
        ConfigSchema.ApplySerdeFieldDefaults(root, config);
        config.Normalize();

        if (ConfigMigration.Migrate(config, _logger))
        {
            // 迁移结果需要落盘，否则用户每次启动都会重跑迁移。
            // 但落盘失败不应影响本次会话可用性，故此处吞掉异常（Save 内部已记录日志）。
            try
            {
                Save(config);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "迁移后的配置落盘失败，本次会话仍使用内存中的配置");
            }
        }

        return config;
    }

    private AppConfig RecoverFromCorrupt(JsonException ex)
    {
        _logger.LogError(ex, "配置文件解析失败，使用默认值: {Path}", FilePath);

        var backupPath = $"{FilePath}.backup.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        try
        {
            File.Copy(FilePath, backupPath, overwrite: true);
            _logger.LogWarning("配置文件解析失败，已备份到: {BackupPath}", backupPath);
        }
        catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(copyEx, "备份损坏配置文件失败: {Path}", FilePath);
        }

        var message = $"配置文件格式错误，已恢复为默认设置。\n错误详情: {ex.Message}\n原文件已备份至: {backupPath}";
        try
        {
            File.WriteAllText(ErrorFilePath, message, Utf8NoBom);
        }
        catch (Exception writeEx) when (writeEx is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(writeEx, "写入配置错误提示失败: {Path}", ErrorFilePath);
        }

        return new AppConfig();
    }

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件残留无碍，下一次写入会覆盖
        }
    }
}
