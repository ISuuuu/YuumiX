using Xunit;
using YuumiX.Core.Common;

namespace YuumiX.Core.Tests;

/// <summary>
/// 颜色字符串归一化（ADR-12）：配置文件里 6 位与 8 位形态混用，透传前必须统一成 #AARRGGBB。
/// </summary>
public sealed class ColorHexTests
{
    [Theory]
    [InlineData("#009faa", "#FF009FAA")]
    [InlineData("009faa", "#FF009FAA")]
    [InlineData("#009FAA", "#FF009FAA")]
    [InlineData("#3339b01b", "#3339B01B")]
    [InlineData("#FFF", "#FFFFFFFF")]
    [InlineData("  #009faa  ", "#FF009FAA")]
    public void NormalizeArgb_ProducesEightDigitUppercase(string input, string expected)
    {
        Assert.Equal(expected, ColorHex.NormalizeArgb(input, "#FF000000"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("不合法")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void InvalidValue_FallsBackToProvidedDefault(string? input)
    {
        Assert.Equal("#FF009FAA", ColorHex.NormalizeArgb(input, "#009faa"));
    }

    /// <summary>兜底值本身也非法时不得抛异常，返回不透明黑，让错误以「看得见」的方式暴露。</summary>
    [Fact]
    public void InvalidFallback_ReturnsOpaqueBlack()
    {
        Assert.Equal("#FF000000", ColorHex.NormalizeArgb("nope", "also-nope"));
    }

    [Fact]
    public void TryNormalize_ReportsFailureWithoutThrowing()
    {
        Assert.False(ColorHex.TryNormalize(string.Empty, out var value));
        Assert.Equal(string.Empty, value);

        Assert.True(ColorHex.TryNormalize("#00000000", out value));
        Assert.Equal("#00000000", value);
    }
}
