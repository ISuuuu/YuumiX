using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Tests;

/// <summary>
/// 设置写回路径：写前备份策略（ADR-12）、未知字段保留、备份失败即中止写回。
/// </summary>
public sealed class ConfigBackupTests
{
    /// <summary>会话内首次写回必须留下备份，且备份内容是写回前的原文。</summary>
    [Fact]
    public void FirstWriteOfSession_CreatesBackupWithOriginalContent()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(path, OriginalJson);
        var store = NewStore(path);
        var config = store.Load();

        config.General.LogLevel = 2;
        var backup = store.SaveWithBackup(config);

        Assert.NotNull(backup);
        Assert.True(File.Exists(backup));
        Assert.Contains("\"LogLevel\": 1", File.ReadAllText(backup), StringComparison.Ordinal);
        Assert.Contains("\"LogLevel\": 2", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>同一次会话的后续写回不再产生备份文件。</summary>
    [Fact]
    public void SubsequentWritesInSameSession_DoNotCreateMoreBackups()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(path, OriginalJson);
        var store = NewStore(path);
        var config = store.Load();

        Assert.NotNull(store.SaveWithBackup(config));

        config.Functions.CareerGamesNumber = 30;
        Assert.Null(store.SaveWithBackup(config));

        config.Functions.CareerGamesNumber = 40;
        Assert.Null(store.SaveWithBackup(config));

        Assert.Single(store.ListBackups());
    }

    /// <summary>新进程（新实例）代表新会话，会再备份一份。</summary>
    [Fact]
    public void NewStoreInstance_StartsNewBackupSession()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(path, OriginalJson);

        var first = NewStore(path);
        var config = first.Load();
        Assert.NotNull(first.SaveWithBackup(config));

        var second = NewStore(path);
        config = second.Load();
        Assert.NotNull(second.SaveWithBackup(config));

        Assert.Equal(2, second.ListBackups().Count);
    }

    /// <summary>备份份数超过上限时删除最旧的，保留 <see cref="ConfigStore.MaxBackups"/> 份。</summary>
    [Fact]
    public void Backups_ArePrunedToRetentionLimit()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(path, OriginalJson);

        // 预置一批更旧的备份，文件名时间戳严格小于本次生成的（本次一定是当前时间）
        for (var i = 1; i <= ConfigStore.MaxBackups; i++)
        {
            var stale = $"{path}.bak.20000101-0000{i:00}";
            File.WriteAllText(stale, "{}");
        }

        var store = NewStore(path);
        var config = store.Load();
        store.SaveWithBackup(config);

        var backups = store.ListBackups();
        Assert.Equal(ConfigStore.MaxBackups, backups.Count);
        Assert.DoesNotContain(backups, p => p.Contains("20000101-000001", StringComparison.Ordinal));
    }

    /// <summary>
    /// 写回保留未建模的键：设置页只改已知字段，<c>[JsonExtensionData]</c> 收集的键必须原样留在文件里，
    /// 否则一次保存就会抹掉原版 Yuumi 新增的字段（ADR-08）。
    /// </summary>
    [Fact]
    public void SaveWithBackup_PreservesUnknownKeys()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(
            path,
            """
            {
              "Version": 3,
              "General": { "LogLevel": 1, "FutureFlagForYuumi": true },
              "Personalization": { "ThemeColor": "#009faa" },
              "Functions": { "EnableReserveGameinfo": true },
              "FutureSection": { "Nested": [1, 2, 3] }
            }
            """);

        var store = NewStore(path);
        var config = store.Load();
        config.General.EnableCheckUpdate = false;
        store.SaveWithBackup(config);

        var reloaded = NewStore(path).Load();
        Assert.False(reloaded.General.EnableCheckUpdate);

        var text = File.ReadAllText(path);
        Assert.Contains("FutureFlagForYuumi", text, StringComparison.Ordinal);
        Assert.Contains("FutureSection", text, StringComparison.Ordinal);
        Assert.Contains("\"Nested\"", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 备份失败时不写盘：与共用配置文件打交道时，「没备份成功还继续覆盖」比「这次没保存上」危险得多。
    /// </summary>
    [Fact]
    public void BackupFailure_AbortsWrite()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(path, OriginalJson);

        var store = NewStore(path);
        var config = store.Load();
        config.General.LogLevel = 2;

        // 独占打开原文件，File.Copy 必然失败（Windows 不允许读共享冲突下的复制）
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => store.SaveWithBackup(config));
        }

        Assert.Equal(OriginalJson, File.ReadAllText(path));
    }

    /// <summary>
    /// 磁盘上还没有配置文件时不产生备份，但会话标记要置上，避免后续每次写回都尝试复制。
    /// 注意 <c>Load</c> 会在文件缺失时立即生成默认配置，因此本用例刻意不走 Load。
    /// </summary>
    [Fact]
    public void MissingConfigFile_BacksUpNothing()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        var store = NewStore(path);

        Assert.Null(store.SaveWithBackup(new AppConfig()));
        Assert.Empty(store.ListBackups());
        Assert.True(File.Exists(path));

        // 会话标记已置上：即使文件此刻存在，也不再补一份备份
        Assert.Null(store.SaveWithBackup(new AppConfig()));
    }

    private const string OriginalJson = """
        {
          "Version": 3,
          "General": { "LogLevel": 1 },
          "Personalization": {},
          "Functions": {},
          "Other": {}
        }
        """;

    private static ConfigStore NewStore(string path) => new(path, NullLogger<ConfigStore>.Instance);
}
