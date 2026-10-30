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
    [InlineData(1199, 1100)]
    [InlineData(1000, 1000)]
    [InlineData(1100.75, 1100)]
    [InlineData(1341, 1100)]
    public void ContentColumnFit_FloorsClampedWidth(double viewportWidth, double expectedWidth)
    {
        var width = ContentColumn.Fit(viewportWidth);
        Assert.Equal(expectedWidth, width);
        Assert.Equal(Math.Floor(width), width);
    }

    [Theory]
    [InlineData(1199, 1100, 1.0, 49)]
    [InlineData(1184, 1100, 1.0, 42)]
    [InlineData(1000, 1000, 1.0, 0)]
    [InlineData(1100.75, 1100, 1.0, 0)]
    [InlineData(1199, 1100, 1.25, 48)]
    [InlineData(1199, 1100, 1.5, 48)]
    [InlineData(1341, 1100, 1.0, 120)]
    [InlineData(1341, 1100, 1.25, 120)]
    public void ContentColumnCenterOffset_AlwaysLandsOnPhysicalPixelGrid(
        double viewportWidth,
        double columnWidth,
        double pixelScale,
        double expectedOffset)
    {
        var width = ContentColumn.Fit(viewportWidth);
        Assert.Equal(columnWidth, width);

        var offset = ContentColumn.CenterOffset(viewportWidth, width, pixelScale);

        Assert.Equal(expectedOffset, offset);

        // 弹层锚点必须落在整数物理像素上：offset × 缩放系数 ∈ ℤ。
        // 系统缩放 125% 下整数 DIP 仍是半像素（49×1.25=61.25），所以断言乘积而不是 offset 本身。
        Assert.Equal(Math.Round(offset * pixelScale), offset * pixelScale, 6);

        // 负数与非法输入回退为 0，不产生负边距。
        Assert.Equal(0, ContentColumn.CenterOffset(-1, width, pixelScale));
        Assert.Equal(0, ContentColumn.CenterOffset(double.NaN, width, pixelScale));
        Assert.Equal(0, ContentColumn.CenterOffset(viewportWidth, width, double.NaN));
    }
}
