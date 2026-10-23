using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Tests;

/// <summary>
/// 配置文件读写的边界行为：缺失、损坏、遗留键名、serde 缺省值语义。
/// </summary>
public sealed class ConfigStoreTests
{
    [Fact]
    public void MissingFile_CreatesDefaultConfigOnDisk()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        var store = NewStore(path);

        var config = store.Load();

        Assert.Equal(AppConfig.CurrentVersion, config.Version);
        Assert.True(File.Exists(path), "配置文件不存在时应立即生成默认配置");
    }

    [Fact]
    public void CorruptFile_IsBackedUp_AndErrorIsRecorded()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(path, "{ 这不是合法 JSON ");

        var store = NewStore(path);
        var config = store.Load();

        // 降级为默认配置，不得抛异常
        Assert.Equal(AppConfig.CurrentVersion, config.Version);

        var backups = Directory.GetFiles(temp.Path, "config.json.backup.*");
        Assert.Single(backups);

        var error = store.TakeLoadError();
        Assert.NotNull(error);
        Assert.Contains("格式错误", error);

        // 读取即清除，避免下次启动重复提示
        Assert.False(File.Exists(store.ErrorFilePath));
        Assert.Null(store.TakeLoadError());
    }

    /// <summary>
    /// v1 的 <c>EnableGithubProxy</c> / <c>GithubProxyAddr</c> 要能正确读入，
    /// 并在落盘时改写为 v2+ 的键名。
    /// </summary>
    [Fact]
    public void LegacyProxyKeys_AreMigratedAndRenamed()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(
            path,
            """
            {
              "Version": 1,
              "General": {
                "EnableGithubProxy": true,
                "GithubProxyAddr": "127.0.0.1:8888",
                "LogLevel": 0
              }
            }
            """);

        var store = NewStore(path);
        var config = store.Load();

        Assert.Equal(AppConfig.CurrentVersion, config.Version);
        Assert.True(config.General.EnableHttpProxy);
        Assert.Equal("127.0.0.1:8888", config.General.HttpProxyAddr);
        Assert.Equal(1, config.General.LogLevel);

        // 迁移结果必须已落盘，且不再出现旧键
        var saved = File.ReadAllText(path);
        Assert.Contains("EnableHttpProxy", saved);
        Assert.DoesNotContain("EnableGithubProxy", saved);
        Assert.DoesNotContain("GithubProxyAddr", saved);
    }

    /// <summary>
    /// serde 语义：字段级裸 <c>#[serde(default)]</c> 优先于容器级 default。
    /// <c>Functions</c> 存在但缺少 <c>EnableReserveGameinfo</c> 时，Rust 得到 false。
    /// </summary>
    [Fact]
    public void MissingEnableReserveGameinfo_DefaultsToFalse()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(path, """{ "Version": 3, "Functions": { "HideTft": false } }""");

        var config = NewStore(path).Load();

        Assert.False(config.Functions.EnableReserveGameinfo);
        Assert.False(config.Functions.HideTft);
    }

    /// <summary>
    /// 整个 <c>Functions</c> 节点缺失时走容器默认值，此时该字段应为 true。
    /// 与上一条用例合起来覆盖 serde 的两种缺省路径。
    /// </summary>
    [Fact]
    public void MissingFunctionsSection_UsesContainerDefaults()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(path, """{ "Version": 3 }""");

        var config = NewStore(path).Load();

        Assert.True(config.Functions.EnableReserveGameinfo);
        Assert.True(config.Functions.HideTft);
        Assert.Equal(20, config.Functions.CareerGamesNumber);
        Assert.Equal(2400, config.Functions.DefaultGameMode);
    }

    /// <summary>显式 <c>null</c> 的列表/字符串要容错为默认值，而不是让整份配置判为损坏。</summary>
    [Fact]
    public void ExplicitNulls_AreNormalized()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(
            path,
            """
            {
              "Version": 3,
              "General": { "LolPath": null, "SignalrUserId": null },
              "Functions": { "AutoBanChampion": null, "ScreenshotOnMultikillLevels": null }
            }
            """);

        var config = NewStore(path).Load();

        Assert.Empty(config.General.LolPath);
        Assert.Equal(string.Empty, config.General.SignalrUserId);
        Assert.Empty(config.Functions.AutoBanChampion);
        Assert.Equal(new List<int> { 3, 4, 5, 8 }, config.Functions.ScreenshotOnMultikillLevels);
    }

    /// <summary>写入只含 UTF-8 内容、无 BOM；再次读回可正确解析中文。</summary>
    [Fact]
    public void Save_WritesUtf8WithoutBom()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");

        var store = NewStore(path);
        var config = store.Load();
        config.Personalization.Language = "简体中文";
        store.Save(config);

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "不应写入 BOM");
        Assert.Equal("简体中文", NewStore(path).Load().Personalization.Language);
    }

    private static ConfigStore NewStore(string path) => new(path, NullLogger<ConfigStore>.Instance);
}
