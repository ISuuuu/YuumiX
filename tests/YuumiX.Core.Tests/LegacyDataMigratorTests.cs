using Xunit;
using YuumiX.Core.Persistence;
using YuumiX.Core.Runtime;

namespace YuumiX.Core.Tests;

/// <summary>
/// 旧版 Yuumi 数据目录的一次性迁移。关键不变量：
/// <list type="number">
///   <item>以「自己的 config.json 是否已存在」为幂等边界，已完成迁移后绝不覆盖自己的文件；</item>
///   <item>复制是拷贝而非移动，旧版 Yuumi 目录必须原样保留。</item>
/// </list>
/// </summary>
public sealed class LegacyDataMigratorTests
{
    [Fact]
    public void CopiesConfigAndUserDataFromLegacyDirectory()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();
        var legacy = appData.File("Yuumi");
        Directory.CreateDirectory(legacy);
        WriteFile(Path.Combine(legacy, "config.json"), """{"Version":1,"General":{"LogLevel":2}}""");
        WriteFile(Path.Combine(legacy, "saved_players.db"), "db-bytes");
        WriteFile(Path.Combine(legacy, "saved_players.db-wal"), "wal-bytes");
        WriteFile(Path.Combine(legacy, "search_history.json"), """["阿狸"]""");
        // 可重建的缓存不迁移
        WriteFile(Path.Combine(legacy, "cache", "assets", "abc.png"), "png");

        var paths = RuntimePaths.Detect(exeDir.Path, appData.Path);
        LegacyDataMigrator.Migrate(paths);

        var own = appData.File("YuumiX");
        Assert.Equal(
            """{"Version":1,"General":{"LogLevel":2}}""",
            File.ReadAllText(Path.Combine(own, "config.json")));
        Assert.Equal("db-bytes", File.ReadAllText(Path.Combine(own, "saved_players.db")));
        Assert.Equal("wal-bytes", File.ReadAllText(Path.Combine(own, "saved_players.db-wal")));
        Assert.Equal("""["阿狸"]""", File.ReadAllText(Path.Combine(own, "search_history.json")));
        Assert.False(File.Exists(Path.Combine(own, "cache", "assets", "abc.png")));

        // 旧目录原样保留（拷贝而非移动），Yuumi 不受影响
        Assert.True(File.Exists(Path.Combine(legacy, "config.json")));
        Assert.True(File.Exists(Path.Combine(legacy, "saved_players.db")));
    }

    [Fact]
    public void SkipsWhenOwnConfigAlreadyExists()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();
        var legacy = appData.File("Yuumi");
        Directory.CreateDirectory(legacy);
        WriteFile(Path.Combine(legacy, "config.json"), """{"General":{"LogLevel":2}}""");
        WriteFile(Path.Combine(legacy, "search_history.json"), """["旧历史"]""");

        var own = appData.File("YuumiX");
        Directory.CreateDirectory(own);
        WriteFile(Path.Combine(own, "config.json"), """{"General":{"LogLevel":0}}""");
        WriteFile(Path.Combine(own, "search_history.json"), """["新历史"]""");

        LegacyDataMigrator.Migrate(RuntimePaths.Detect(exeDir.Path, appData.Path));

        Assert.Equal("""{"General":{"LogLevel":0}}""", File.ReadAllText(Path.Combine(own, "config.json")));
        Assert.Equal("""["新历史"]""", File.ReadAllText(Path.Combine(own, "search_history.json")));
    }

    [Fact]
    public void SkipsWhenLegacyConfigMissing()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();
        // 旧目录存在但从未保存过配置：只有孤立的用户数据，没有可迁移的锚点
        var legacy = appData.File("Yuumi");
        Directory.CreateDirectory(legacy);
        WriteFile(Path.Combine(legacy, "saved_players.db"), "db-bytes");

        LegacyDataMigrator.Migrate(RuntimePaths.Detect(exeDir.Path, appData.Path));

        Assert.False(Directory.Exists(appData.File("YuumiX")));
    }

    [Fact]
    public void SkipsWhenLegacyDirectoryMissing()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();

        LegacyDataMigrator.Migrate(RuntimePaths.Detect(exeDir.Path, appData.Path));

        Assert.False(Directory.Exists(appData.File("YuumiX")));
    }

    [Fact]
    public void PortableModeNeverMigrates()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();
        File.WriteAllBytes(Path.Combine(exeDir.Path, RuntimePaths.PortableMarkerFile), []);
        var legacy = appData.File("Yuumi");
        Directory.CreateDirectory(legacy);
        WriteFile(Path.Combine(legacy, "config.json"), """{}""");

        var paths = RuntimePaths.Detect(exeDir.Path, appData.Path);
        LegacyDataMigrator.Migrate(paths);

        Assert.Null(paths.LegacyDataDirectory);
        Assert.False(File.Exists(Path.Combine(exeDir.Path, "data", "config.json")));
    }

    private static void WriteFile(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content);
    }
}
