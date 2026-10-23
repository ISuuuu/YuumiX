using Xunit;
using YuumiX.Core.Assets;

namespace YuumiX.Core.Tests;

/// <summary>
/// 缓存版本哨兵：版本变化才清空，同版本重复调用必须无副作用
/// （探活每轮都会汇报版本，重复清空会让图标在每次重连后重新下载）。
/// </summary>
public sealed class AssetCacheStampTests
{
    [Fact]
    public void Apply_FirstVersion_ClearsStaleEntries()
    {
        using var directory = new TempDirectory();
        var first = WriteFile(directory, "first.png");
        var second = WriteFile(directory, "second.png");

        var stamp = new AssetCacheStamp(directory.Path);

        Assert.True(stamp.Apply("16.1.1"));
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(Path.Combine(directory.Path, AssetCacheStamp.StampFileName)));
    }

    [Fact]
    public void Apply_SameVersion_DoesNothing()
    {
        using var directory = new TempDirectory();
        var stamp = new AssetCacheStamp(directory.Path);

        Assert.True(stamp.Apply("16.1.1"));

        var kept = WriteFile(directory, "kept.png");

        Assert.False(stamp.Apply("16.1.1"));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void Apply_NewVersion_ClearsAgain()
    {
        using var directory = new TempDirectory();
        var stamp = new AssetCacheStamp(directory.Path);
        stamp.Apply("16.1.1");

        var kept = WriteFile(directory, "kept.png");

        Assert.True(stamp.Apply("16.2.0"));
        Assert.False(File.Exists(kept));
    }

    /// <summary>版本取不到时不能清缓存：宁可继续用旧图，也不要让一次探活解析失败清空全站缓存。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_WithoutVersion_DoesNothing(string? version)
    {
        using var directory = new TempDirectory();
        var kept = WriteFile(directory, "kept.png");
        var stamp = new AssetCacheStamp(directory.Path);

        Assert.False(stamp.Apply(version));
        Assert.True(File.Exists(kept));
    }

    private static string WriteFile(TempDirectory directory, string name)
    {
        var path = directory.File(name);
        File.WriteAllText(path, "x");
        return path;
    }
}
