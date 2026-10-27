using Xunit;
using YuumiX.Core.Runtime;

namespace YuumiX.Core.Tests;

/// <summary>
/// 运行时路径解析。核心约束：安装版数据目录是 YuumiX 自己的 <c>%APPDATA%/YuumiX</c>，
/// 旧版 Yuumi 目录仅作为一次性迁移的只读来源暴露；便携版完全隔离到 exe 旁的 <c>data</c>。
/// </summary>
public sealed class RuntimePathsTests
{
    [Fact]
    public void Installed_UsesRoamingAppData()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();

        var paths = RuntimePaths.Detect(exeDir.Path, appData.Path);

        Assert.False(paths.IsPortable);
        Assert.Equal(Path.Combine(appData.Path, "YuumiX"), paths.DataDirectory);
        Assert.Equal(Path.Combine(appData.Path, "YuumiX", "config.json"), paths.ConfigFilePath);
        // 旧版目录必须可探测（一次性迁移的读取来源），但与自己的数据目录互不重叠
        Assert.Equal(Path.Combine(appData.Path, "Yuumi"), paths.LegacyDataDirectory);
    }

    [Fact]
    public void Portable_WhenMarkerFileExists()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();
        File.WriteAllBytes(Path.Combine(exeDir.Path, RuntimePaths.PortableMarkerFile), []);

        var paths = RuntimePaths.Detect(exeDir.Path, appData.Path);

        Assert.True(paths.IsPortable);
        Assert.Equal(Path.Combine(exeDir.Path, "data"), paths.DataDirectory);
        // 便携版数据目录本就随各自 exe 隔离，没有可探测的旧版目录
        Assert.Null(paths.LegacyDataDirectory);
    }

    /// <summary>
    /// 日志目录恒在 exe 旁，不随便携/安装模式变化（与原 logging.rs 一致）。
    /// 两个版本的可执行文件目录不同，天然不会互相覆盖。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LogDirectory_IsAlwaysNextToExecutable(bool portable)
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();
        if (portable)
        {
            File.WriteAllBytes(Path.Combine(exeDir.Path, RuntimePaths.PortableMarkerFile), []);
        }

        var paths = RuntimePaths.Detect(exeDir.Path, appData.Path);

        Assert.Equal(Path.Combine(exeDir.Path, "log"), paths.LogDirectory);
    }

    /// <summary>
    /// 数据目录必须叫 <c>YuumiX</c>：两个版本已各自独立，写旧版 Yuumi 的目录会互相覆盖配置。
    /// 旧目录名仅保留在 <see cref="RuntimePaths.LegacyDataFolderName"/> 供一次性迁移读取。
    /// </summary>
    [Fact]
    public void DataFolderName_IsOwnApp()
    {
        Assert.Equal("YuumiX", RuntimePaths.DataFolderName);
        Assert.Equal("Yuumi", RuntimePaths.LegacyDataFolderName);
        Assert.Equal("portable.flag", RuntimePaths.PortableMarkerFile);
    }
}
