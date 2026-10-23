using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Tests;

/// <summary>
/// 配置往返（load → save）的无损性验证。
///
/// 这是阶段 2 的核心验收标准：「能读写原 config.json 且不丢失字段」。
/// 夹具 <c>Fixtures/config.v3.json</c> 取自原项目真实配置（仅替换个人标识字符串），
/// 字段结构与值保持原样。
/// </summary>
public sealed class ConfigRoundTripTests
{
    [Fact]
    public void LoadThenSave_PreservesEveryField()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.Copy(Fixture.Path, path);

        var before = JsonNode.Parse(File.ReadAllText(path))!;

        var store = NewStore(path);
        var config = store.Load();
        store.Save(config);

        var after = JsonNode.Parse(File.ReadAllText(path))!;
        var diffs = JsonDiff.Compare(before, after);

        Assert.True(
            diffs.Count == 0,
            "配置写回后字段发生变化：" + Environment.NewLine + JsonDiff.Format(diffs));
    }

    /// <summary>
    /// 模型必须覆盖原文件的全部字段。用反射锁定各节属性数量：
    /// 少一个说明漏建模（往返测试会丢字段），多一个说明凭空造了 Rust 侧没有的字段。
    /// </summary>
    [Theory]
    [InlineData(typeof(GeneralConfig), 13)]
    [InlineData(typeof(PersonalizationConfig), 10)]
    [InlineData(typeof(FunctionsConfig), 58)]
    [InlineData(typeof(OtherConfig), 2)]
    public void Model_CoversAllFieldsOfOriginalSchema(Type sectionType, int expectedCount)
    {
        var actual = sectionType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Count(p => !p.IsDefined(typeof(JsonExtensionDataAttribute), inherit: false));

        Assert.Equal(expectedCount, actual);
    }

    [Fact]
    public void LoadThenSave_PreservesValuesInModel()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.Copy(Fixture.Path, path);

        var config = NewStore(path).Load();

        // 抽查各节与各类型：int / bool / string / List<int> / List<string> / double / 可空 bool
        Assert.Equal(3, config.Version);
        Assert.Equal(new List<string> { "WeGame", @"C:\Riot Games\LeagueClient" }, config.General.LolPath);
        Assert.Equal(@"C:\Program Files (x86)\WeGame", config.General.WegamePath);
        Assert.True(config.General.EnableCloseToTray);
        Assert.Equal(1, config.General.LogLevel);
        Assert.Equal("https://lol.369900.xyz", config.General.UploadApiUrl);
        Assert.True(config.General.EnableHttpProxy);

        Assert.Equal("#009faa", config.Personalization.ThemeColor);
        Assert.Equal("#3339b01b", config.Personalization.WinCardColor);

        Assert.Equal(0d, config.Functions.AutoBanDelay);
        Assert.Equal(new List<int> { 17 }, config.Functions.AutoSelectChampion);
        Assert.Equal(new List<int> { 3, 4, 5, 8 }, config.Functions.ScreenshotOnMultikillLevels);
        Assert.False(config.Functions.UploadEnabled);
        Assert.True(config.Functions.EnableReserveGameinfo);
        Assert.Empty(config.Functions.WatchedFriendPuuids);

        Assert.Equal(string.Empty, config.Other.LastNoticeSha);
    }

    /// <summary>
    /// 未建模的字段（例如将来 Yuumi 新增、或用户手工加的键）必须原样写回。
    ///
    /// 迁移期两个版本共用同一份 config.json，若 YuumiX 写回时丢弃未知键，
    /// 会静默清空原版本新增的配置项。
    /// </summary>
    [Fact]
    public void UnknownFields_SurviveRoundTrip()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");

        var root = Fixture.ReadObject();
        root["FutureSection"] = new JsonObject { ["Flags"] = new JsonArray(1, 2, 3) };
        root["Functions"]!.AsObject()["FutureToggle"] = true;
        root["General"]!.AsObject()["FuturePath"] = @"D:\缓存";
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), NoBom);

        var store = NewStore(path);
        store.Save(store.Load());

        var after = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        Assert.True(after.ContainsKey("FutureSection"), "顶层未知节点被丢弃");
        Assert.Equal(3, after["FutureSection"]!["Flags"]!.AsArray().Count);
        Assert.True(after["Functions"]!.AsObject()["FutureToggle"]!.GetValue<bool>());
        Assert.Equal(@"D:\缓存", after["General"]!.AsObject()["FuturePath"]!.GetValue<string>());
    }

    /// <summary>
    /// 非 ASCII 字符（中文安装路径）必须原样输出，不能被转义成 \uXXXX。
    /// 语义等价，但转义会让配置文件不可读，并在与原版本交替写入时产生无意义的文本 diff。
    /// </summary>
    [Fact]
    public void NonAsciiText_IsWrittenRaw()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");

        var root = Fixture.ReadObject();
        root["General"]!.AsObject()["LolPath"] = new JsonArray("WeGame", @"D:\WeGameApps\英雄联盟（含经典模式）\LeagueClient");
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), NoBom);

        var store = NewStore(path);
        store.Save(store.Load());

        var written = File.ReadAllText(path);
        Assert.Contains("英雄联盟（含经典模式）", written);
        Assert.DoesNotContain("\\u", written);
    }

    /// <summary>整数形态的 double 必须保持 <c>0.0</c> 写法（serde_json 的习惯），不能退化成 <c>0</c>。</summary>
    [Fact]
    public void WholeDouble_KeepsDecimalPoint()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.Copy(Fixture.Path, path);

        var store = NewStore(path);
        store.Save(store.Load());

        Assert.Contains("\"AutoBanDelay\": 0.0", File.ReadAllText(path));
    }

    [Fact]
    public void Save_LeavesNoTemporaryFile()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.Copy(Fixture.Path, path);

        var store = NewStore(path);
        store.Save(store.Load());

        Assert.False(File.Exists(path + ".tmp"), "原子写入后不应残留 .tmp 文件");
    }

    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static ConfigStore NewStore(string path) => new(path, NullLogger<ConfigStore>.Instance);
}
