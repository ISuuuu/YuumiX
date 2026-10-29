using Xunit;
using YuumiX.App.Controls;
using YuumiX.App.Windowing;

namespace YuumiX.App.Tests;

public sealed class WindowMetricsTests
{
    [Theory]
    [InlineData("Auto", 1.0)]
    [InlineData("100", 1.0)]
    [InlineData("125", 1.25)]
    [InlineData("150", 1.5)]
    [InlineData(null, 1.0)]
    [InlineData("unknown", 1.0)]
    public void ParseScaleFactor_ReturnsExpectedFactor(string? dpiScale, double expectedFactor)
    {
        var factor = WindowMetrics.ParseScaleFactor(dpiScale);
        Assert.Equal(expectedFactor, factor);
    }

    [Theory]
    [InlineData("Auto", 1200, 910)]
    [InlineData("100", 1200, 910)]
    [InlineData("125", 1500, 1138)]
    [InlineData("150", 1800, 1365)]
    [InlineData(null, 1200, 910)]
    [InlineData("", 1200, 910)]
    public void CalculateWindowSize_FromString_ReturnsExpectedDimensions(string? dpiScale, int expectedWidth, int expectedHeight)
    {
        var (width, height) = WindowMetrics.CalculateWindowSize(dpiScale);
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Fact]
    public void CalculateWindowSize_FromDoubleFactor_ReturnsRoundedDimensions()
    {
        var (width, height) = WindowMetrics.CalculateWindowSize(1.25);
        Assert.Equal(1500, width);
        Assert.Equal(1138, height);
    }

    [Fact]
    public void ApplyScaleToWindow_WhenWindowIsNull_ReturnsFalse()
    {
        var result = WindowMetrics.ApplyScaleToWindow(null, "125");
        Assert.False(result);
    }

    [Theory]
    [InlineData(1199, 1100, 49)]
    [InlineData(1184, 1100, 42)]
    [InlineData(1000, 1000, 0)]
    [InlineData(1100.75, 1100, 0)]
    public void ContentColumnCenterOffset_AlwaysReturnsIntegerPixelOffset(
        double viewportWidth,
        double columnWidth,
        double expectedOffset)
    {
        var width = ContentColumn.Fit(viewportWidth);
        var offset = ContentColumn.CenterOffset(viewportWidth, columnWidth);

        // 列宽必须与用例给定值一致（Fit 的夹取结果），否则用例本身就失去意义
        Assert.Equal(columnWidth, width);

        // 左边距取整：主树控件会被布局取整掩盖，Popup 树里的 ComboBox 弹层则会被重采样
        Assert.Equal(expectedOffset, offset);
        Assert.Equal(Math.Floor(offset), offset);
        Assert.Equal(Math.Floor(width), width);
    }
}
