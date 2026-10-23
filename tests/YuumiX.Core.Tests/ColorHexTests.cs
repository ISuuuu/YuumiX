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

    /// <summary>取色控件需要数值形态：8 位、6 位、3 位都要能解析出正确的 alpha 与通道。</summary>
    [Theory]
    [InlineData("#3339b01b", 0x33, 0x39, 0xB0, 0x1B)]
    [InlineData("#009faa", 0xFF, 0x00, 0x9F, 0xAA)]
    [InlineData("#f00", 0xFF, 0xFF, 0x00, 0x00)]
    [InlineData("FFFFFFFF", 0xFF, 0xFF, 0xFF, 0xFF)]
    public void TryParseArgb_ReturnsChannels(string input, byte a, byte r, byte g, byte b)
    {
        Assert.True(ColorHex.TryParseArgb(input, out var actualA, out var actualR, out var actualG, out var actualB));
        Assert.Equal((a, r, g, b), (actualA, actualR, actualG, actualB));
    }

    [Fact]
    public void TryParseArgb_InvalidInputYieldsDefaults()
    {
        Assert.False(ColorHex.TryParseArgb("不是颜色", out var a, out var r, out var g, out var b));
        Assert.Equal((0xFF, (byte)0, (byte)0, (byte)0), (a, r, g, b));
    }

    /// <summary>取色控件的回调是数值形态，写回配置前必须拼回与 Rust 一致的字符串写法。</summary>
    [Fact]
    public void ToArgb_ProducesUppercaseEightDigitString()
    {
        Assert.Equal("#3339B01B", ColorHex.ToArgb(0x33, 0x39, 0xB0, 0x1B));
        Assert.Equal("#FF009FAA", ColorHex.ToArgb(0xFF, 0x00, 0x9F, 0xAA));
    }

    [Fact]
    public void ToRgb_FromChannels_ProducesSixDigitString()
    {
        Assert.Equal("#009FAA", ColorHex.ToRgb(0x00, 0x9F, 0xAA));
    }

    /// <summary>主题色在配置里存 8 位、在取色器里显示 6 位，互转不能丢通道。</summary>
    [Theory]
    [InlineData("#009faa", "#009FAA")]
    [InlineData("#ff009faa", "#009FAA")]
    [InlineData(null, "#009FAA")]
    public void ToRgb_DropsAlpha(string? input, string expected)
    {
        Assert.Equal(expected, ColorHex.ToRgb(input, "#009faa"));
    }
}
