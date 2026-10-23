using Xunit;
using YuumiX.Core.Runtime;

namespace YuumiX.Core.Tests;

/// <summary>
/// 运行时路径解析。核心约束：安装版必须指向 <c>%APPDATA%/Yuumi</c>（与原项目共用配置），
/// 便携版必须完全隔离到 exe 旁的 <c>data</c>。
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
        Assert.Equal(Path.Combine(appData.Path, "Yuumi"), paths.DataDirectory);
        Assert.Equal(Path.Combine(appData.Path, "Yuumi", "config.json"), paths.ConfigFilePath);
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
    /// 数据目录必须叫 <c>Yuumi</c> 而不是 <c>YuumiX</c>：
    /// 迁移期两个版本共用同一份配置是验收前提。
    /// </summary>
    [Fact]
    public void DataFolderName_MatchesOriginalApp()
    {
        Assert.Equal("Yuumi", RuntimePaths.DataFolderName);
        Assert.Equal("portable.flag", RuntimePaths.PortableMarkerFile);
    }
}
