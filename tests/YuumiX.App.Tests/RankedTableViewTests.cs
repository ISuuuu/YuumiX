using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Xunit;
using YuumiX.App.Pages.Career;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Career.Models;

namespace YuumiX.App.Tests;

public sealed class RankedTableViewTests
{
    private static readonly YuumiPalette SamplePalette = YuumiTheme.Default;

    private static RankedQueueEntry CreateQueue(string queueType, string tier, int wins, int losses) =>
        new()
        {
            QueueType = queueType,
            Tier = tier,
            Division = tier == "NONE" ? "NA" : "I",
            LeaguePoints = 50,
            Wins = wins,
            Losses = losses,
        };

    [Fact]
    public void Render_BothQueuesEmpty_ReturnsEmptyState()
    {
        var stats = new RankedStats
        {
            Queues =
            [
                CreateQueue(RankedQueueTypes.SoloDuo, "NONE", 0, 0),
                CreateQueue(RankedQueueTypes.Flex, "NONE", 0, 0),
            ],
        };

        var element = (BorderElement)RankedTableView.Render(stats, SamplePalette);
        var stack = Assert.IsAssignableFrom<StackElement>(element.Child);

        // header + divider + emptyRow = 3 items
        Assert.Equal(3, stack.Children.Length);
        var emptyBorder = Assert.IsAssignableFrom<BorderElement>(stack.Children[2]);
        var text = Assert.IsAssignableFrom<TextBlockElement>(emptyBorder.Child);
        Assert.Equal("本赛季未定级", text.Content);
    }

    [Fact]
    public void Render_EmptyStatsRecord_ReturnsEmptyState()
    {
        var stats = RankedStats.Empty;

        var element = (BorderElement)RankedTableView.Render(stats, SamplePalette);
        var stack = Assert.IsAssignableFrom<StackElement>(element.Child);

        Assert.Equal(3, stack.Children.Length);
        var emptyBorder = Assert.IsAssignableFrom<BorderElement>(stack.Children[2]);
        var text = Assert.IsAssignableFrom<TextBlockElement>(emptyBorder.Child);
        Assert.Equal("本赛季未定级", text.Content);
    }

    [Fact]
    public void Render_OnlySoloHasData_ReturnsFullTwoRowTable()
    {
        var stats = new RankedStats
        {
            Queues =
            [
                CreateQueue(RankedQueueTypes.SoloDuo, "GOLD", 10, 5),
                CreateQueue(RankedQueueTypes.Flex, "NONE", 0, 0),
            ],
        };

        var element = (BorderElement)RankedTableView.Render(stats, SamplePalette);
        var stack = Assert.IsAssignableFrom<StackElement>(element.Child);

        // header + divider + soloRow + divider + flexRow = 5 items
        Assert.Equal(5, stack.Children.Length);
    }

    [Fact]
    public void Render_OnlyFlexHasData_ReturnsFullTwoRowTable()
    {
        var stats = new RankedStats
        {
            Queues =
            [
                CreateQueue(RankedQueueTypes.SoloDuo, "NONE", 0, 0),
                CreateQueue(RankedQueueTypes.Flex, "PLATINUM", 3, 2),
            ],
        };

        var element = (BorderElement)RankedTableView.Render(stats, SamplePalette);
        var stack = Assert.IsAssignableFrom<StackElement>(element.Child);

        Assert.Equal(5, stack.Children.Length);
    }

    [Fact]
    public void Render_BothQueuesHaveData_ReturnsFullTwoRowTable()
    {
        var stats = new RankedStats
        {
            Queues =
            [
                CreateQueue(RankedQueueTypes.SoloDuo, "DIAMOND", 20, 10),
                CreateQueue(RankedQueueTypes.Flex, "GOLD", 15, 8),
            ],
        };

        var element = (BorderElement)RankedTableView.Render(stats, SamplePalette);
        var stack = Assert.IsAssignableFrom<StackElement>(element.Child);

        Assert.Equal(5, stack.Children.Length);
    }

    [Fact]
    public void Render_SoloHasHighWinRate_FlexHasLowWinRate_RenderCorrectCells()
    {
        var stats = new RankedStats
        {
            Queues =
            [
                CreateQueue(RankedQueueTypes.SoloDuo, "GOLD", 6, 4), // 60%
                CreateQueue(RankedQueueTypes.Flex, "SILVER", 4, 6), // 40%
            ],
        };

        var element = (BorderElement)RankedTableView.Render(stats, SamplePalette);
        var stack = Assert.IsAssignableFrom<StackElement>(element.Child);

        Assert.Equal(5, stack.Children.Length);

        var soloGrid = Assert.IsAssignableFrom<GridElement>(stack.Children[2]);
        var soloWinRate = Assert.IsAssignableFrom<TextBlockElement>(soloGrid.Children[2]);
        Assert.Equal("60%", soloWinRate.Content);

        var flexGrid = Assert.IsAssignableFrom<GridElement>(stack.Children[4]);
        var flexWinRate = Assert.IsAssignableFrom<TextBlockElement>(flexGrid.Children[2]);
        Assert.Equal("40%", flexWinRate.Content);
    }

    [Fact]
    public void Render_OneQueueZeroGames_WinRateIsDashes()
    {
        var stats = new RankedStats
        {
            Queues =
            [
                CreateQueue(RankedQueueTypes.SoloDuo, "GOLD", 10, 5),
                CreateQueue(RankedQueueTypes.Flex, "NONE", 0, 0),
            ],
        };

        var element = (BorderElement)RankedTableView.Render(stats, SamplePalette);
        var stack = Assert.IsAssignableFrom<StackElement>(element.Child);

        var flexGrid = Assert.IsAssignableFrom<GridElement>(stack.Children[4]);
        var flexWinRate = Assert.IsAssignableFrom<TextBlockElement>(flexGrid.Children[2]);
        Assert.Equal("--", flexWinRate.Content);
    }
}
