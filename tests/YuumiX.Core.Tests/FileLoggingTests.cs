using Microsoft.Extensions.Logging;
using Xunit;
using YuumiX.Core.Diagnostics;

namespace YuumiX.Core.Tests;

/// <summary>
/// 文件日志：行格式、级别过滤、大小轮转。
/// </summary>
public sealed class FileLoggingTests
{
    [Fact]
    public void WritesDailyFile_WithExpectedLineFormat()
    {
        using var dir = new TempDirectory();
        using var provider = new RotatingFileLoggerProvider(dir.Path);
        using var factory = LoggingSetup.CreateFactory(provider);

        factory.CreateLogger("YuumiX.Core.Tests").LogInformation("你好 {Name}", "YuumiX");

        var files = Directory.GetFiles(dir.Path, "yuumi_*.log");
        var file = Assert.Single(files);
        Assert.EndsWith(DateTime.Now.ToString("yyyy-MM-dd") + ".log", file);

        var lines = SharedRead.Lines(file);
        var line = Assert.Single(lines);
        Assert.Contains("[INFO ] YuumiX.Core.Tests - 你好 YuumiX", line);
    }

    /// <summary>
    /// 配置里的 LogLevel 决定过滤阈值：2=Error 时 Information 必须被丢弃。
    /// </summary>
    [Fact]
    public void ConfiguredMinLevel_FiltersOutLowerLevels()
    {
        using var dir = new TempDirectory();
        using var provider = new RotatingFileLoggerProvider(dir.Path);
        using var factory = LoggingSetup.CreateFactory(provider);
        var logger = factory.CreateLogger("Filter");

        provider.MinLevel = LoggingSetup.MapLogLevel(2);
        logger.LogInformation("这条应被丢弃");
        logger.LogError("这条应保留");

        var text = SharedRead.Text(Directory.GetFiles(dir.Path, "yuumi_*.log").Single());
        Assert.DoesNotContain("这条应被丢弃", text);
        Assert.Contains("这条应保留", text);
    }

    [Fact]
    public void ExceedingMaxBytes_RotatesToIndexedFile()
    {
        using var dir = new TempDirectory();
        using var provider = new RotatingFileLoggerProvider(dir.Path, maxBytes: 120);
        using var factory = LoggingSetup.CreateFactory(provider);
        var logger = factory.CreateLogger("Rotate");

        for (var i = 0; i < 6; i++)
        {
            logger.LogInformation("第 {Index} 行，故意写长一些以触发轮转", i);
        }

        var files = Directory.GetFiles(dir.Path, "yuumi_*.log");
        Assert.True(files.Length >= 2, $"应产生分片文件，实际只有 {files.Length} 个");
        Assert.Contains(files, f => f.Contains("-1.log"));
    }

    /// <summary>日志级别映射与原项目一致：0=Debug，1=Info（含未知值回退），2=Error。</summary>
    [Theory]
    [InlineData(0, LogLevel.Debug)]
    [InlineData(1, LogLevel.Information)]
    [InlineData(2, LogLevel.Error)]
    [InlineData(3, LogLevel.Information)]
    [InlineData(40, LogLevel.Information)]
    public void MapLogLevel_MatchesOriginalSemantics(int configured, LogLevel expected)
    {
        Assert.Equal(expected, LoggingSetup.MapLogLevel(configured));
    }
}
