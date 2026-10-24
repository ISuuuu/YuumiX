using Xunit;
using YuumiX.Core.Features.GameInfo;

namespace YuumiX.Core.Tests;

public sealed class LiveClientChampionMatcherTests
{
    private static readonly Dictionary<int, string> Catalog = new()
    {
        [62] = "孙悟空",
        [103] = "九尾妖狐 阿狸",
        [92] = "放逐之刃 锐雯",
        [157] = "疾风剑豪 亚索",
        [223] = "河流之王 塔姆",
    };

    private static readonly Dictionary<string, int> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ahri"] = 103,
        ["yasuo"] = 157,
        ["riven"] = 92,
    };

    [Theory]
    [InlineData("MonkeyKing", null, 62)]
    [InlineData("wukong", null, 62)]
    [InlineData("DrMundo", null, 36)]
    [InlineData("TwistedFate", null, 4)]
    [InlineData("TahmKench", null, 223)]
    [InlineData(null, "game_character_displayname_Ahri", 103)]
    [InlineData(null, "game_character_displayname_MonkeyKing", 62)]
    [InlineData("孙悟空", null, 62)]
    [InlineData("放逐之刃", null, 92)]
    [InlineData("亚索", null, 157)]
    public void FindChampionId_ResolvesExpectedChampion(string? champName, string? rawName, int expectedId)
    {
        int actual = LiveClientChampionMatcher.FindChampionId(Catalog, champName, rawName, Aliases);
        Assert.Equal(expectedId, actual);
    }

    [Fact]
    public void FindChampionId_WhenEmptyOrUnknown_ReturnsZero()
    {
        Assert.Equal(0, LiveClientChampionMatcher.FindChampionId(Catalog, null, null, Aliases));
        Assert.Equal(0, LiveClientChampionMatcher.FindChampionId(Catalog, "", "", Aliases));
        Assert.Equal(0, LiveClientChampionMatcher.FindChampionId(Catalog, "UnknownHero12345", "", Aliases));
    }
}
