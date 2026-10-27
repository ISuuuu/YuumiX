using Xunit;
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
}
