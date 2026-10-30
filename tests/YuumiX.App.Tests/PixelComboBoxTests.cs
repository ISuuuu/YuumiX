using YuumiX.App.Controls;
using Xunit;

namespace YuumiX.App.Tests;

public sealed class PixelComboBoxTests
{
    [Fact]
    public void EmptyOptionsDoNotOpen() =>
        Assert.Null(PixelComboBox.CalculatePlacement(0, 0, 160, 32, 800, 600, 0));

    [Fact]
    public void LongListFitsNarrowViewport()
    {
        var result = PixelComboBox.CalculatePlacement(90, 70, 160, 32, 100, 180, 20);
        Assert.Equal((0, 0, 100, 180), result);
    }

    [Fact]
    public void ListOpensAboveAnchorWhenBelowDoesNotFit()
    {
        var result = PixelComboBox.CalculatePlacement(100, 450, 160, 32, 800, 600, 3);
        Assert.Equal((100, 318, 160, 128), result);
    }

    [Theory]
    [InlineData(0, 0, 3, false)]
    [InlineData(1, 0, 3, true)]
    [InlineData(-1, 0, 3, false)]
    [InlineData(3, 0, 3, false)]
    public void CommitRequiresDifferentValidSelection(int index, int selected, int count, bool expected) =>
        Assert.Equal(expected, PixelComboBox.ShouldCommit(index, selected, count));
}
