using Xunit;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Features.Tools.Models;

namespace YuumiX.Core.Tests;

/// <summary>
/// 英雄搜索匹配（名称 / 称号 / 英文别名 / id / 别名表）的行为验证。
/// 别名表数据源自腾讯英雄列表快照，见 <see cref="ChampionAliasTable"/>。
/// </summary>
public sealed class ChampionSearchTests
{
    private static readonly ChampionSummaryItem[] Catalog =
    [
        new(1, "安妮", "Annie", "黑暗之女", ""),
        new(64, "李青", "LeeSin", "盲僧", ""),
        new(67, "薇恩", "Vayne", "暗夜猎手", ""),
        new(122, "德莱厄斯", "Darius", "诺克萨斯之手", ""),
        new(157, "亚索", "Yasuo", "疾风剑豪", ""),
        new(888, "烈娜塔 · 戈拉斯克", "Renata", "烈娜塔", ""),
        new(897, "奎桑提", "KSante", "纳祖芒荣耀", ""),
        new(901, "斯莫德", "Smolder", "炽炎雏龙", ""),
        new(902, "米利欧", "Milio", "明烛", ""),
    ];

    [Theory]
    [InlineData("李青", 64)]      // 本名
    [InlineData("盲僧", 64)]      // 称号
    [InlineData("瞎子", 64)]      // 社区别称
    [InlineData("xiazi", 64)]     // 拼音全拼
    [InlineData("lq", 64)]        // 拼音首字母
    [InlineData("Lee Sin", 64)]   // 英文别名带空格
    [InlineData("leesin", 64)]    // 英文别名紧凑写法
    [InlineData("vn", 67)]        // 英文缩写
    [InlineData("yasuo", 157)]    // 拼音全拼
    [InlineData("Yasuo", 157)]    // 大小写不敏感
    [InlineData("诺手", 122)]     // 社区别称
    [InlineData("火女", 1)]       // 社区别称
    [InlineData("烈娜塔戈拉斯克", 888)] // 名称含间隔符时按去标点匹配
    [InlineData("901", 901)]      // 英雄 id
    [InlineData("斯莫德", 901)]   // 新英雄本名
    [InlineData("炽炎雏龙", 901)] // 新英雄称号
    [InlineData("小火龙", 901)]   // 腾讯表未收录、由社区补丁提供
    [InlineData("丁真", 902)]     // 社区补丁
    [InlineData("黑龙", 897)]     // 社区补丁
    public void Matches_ResolvesExactlyOneChampion(string query, int expectedId)
    {
        var matched = Catalog.Where(c => ChampionSearch.Matches(c, query)).ToList();

        var champ = Assert.Single(matched);
        Assert.Equal(expectedId, champ.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("·")]
    public void Matches_WithBlankQuery_ReturnsEveryChampion(string? query)
    {
        Assert.All(Catalog, c => Assert.True(ChampionSearch.Matches(c, query)));
    }

    [Fact]
    public void Matches_WithUnknownQuery_ReturnsNothing()
    {
        Assert.DoesNotContain(Catalog, c => ChampionSearch.Matches(c, "zzzz"));
    }

    [Fact]
    public void AliasTable_CoversEveryChampionWithNonEmptyKeywords()
    {
        Assert.True(ChampionAliasTable.Keywords.Count >= 170, $"别名表条目过少: {ChampionAliasTable.Keywords.Count}");

        foreach (var (id, keywords) in ChampionAliasTable.Keywords)
        {
            Assert.False(string.IsNullOrWhiteSpace(keywords), $"英雄 {id} 的别名串为空");
        }
    }
}
