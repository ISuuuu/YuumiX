using Xunit;
using YuumiX.Core.Assets;

namespace YuumiX.Core.Tests;

/// <summary>
/// 资源路径规则。前六条直接移植自原 Rust 实现的单元测试
/// （<c>test_map_asset_path_to_cdragon_url</c>），保持断言逐条对应——
/// 这些映射覆盖了「英雄头像 / 装备图标 / 皮肤资源 / 战利品」四类真实路径形态。
/// </summary>
public sealed class AssetPathMapperTests
{
    private const string Base = AssetPathMapper.DefaultCdnBase;
    private const string Plugin = Base + "/plugins/rcp-be-lol-game-data/global/default";

    [Fact]
    public void ToCdnUrl_MapsV1PluginAssets()
    {
        Assert.Equal(
            Plugin + "/v1/champion-icons/54.png",
            AssetPathMapper.ToCdnUrl("/lol-game-data/assets/v1/champion-icons/54.png"));

        Assert.Equal(
            Plugin + "/v1/profile-icons/29.jpg",
            AssetPathMapper.ToCdnUrl("/lol-game-data/assets/v1/profile-icons/29.jpg"));
    }

    /// <summary>大写目录必须归一到小写：CDragon 的路径全小写，原样拼接会 404。</summary>
    [Fact]
    public void ToCdnUrl_LowercasesGameAssets()
    {
        Assert.Equal(
            Base + "/game/assets/items/icons2d/1001_class_t1_bootsofspeed.png",
            AssetPathMapper.ToCdnUrl("/lol-game-data/assets/ASSETS/Items/Icons2D/1001_Class_T1_BootsofSpeed.png"));

        Assert.Equal(
            Base + "/game/assets/characters/malphite/hud/malphite_square.png",
            AssetPathMapper.ToCdnUrl("/lol-game-data/assets/assets/characters/malphite/hud/malphite_square.png"));
    }

    [Fact]
    public void ToCdnUrl_MapsLootAssets()
    {
        Assert.Equal(
            Base + "/plugins/rcp-fe-lol-loot/global/default/assets/loot_item.png",
            AssetPathMapper.ToCdnUrl("/fe/lol-loot/assets/loot_item.png"));
    }

    [Fact]
    public void ToCdnUrl_KeepsAbsoluteHttpUrls()
    {
        Assert.Equal("https://example.com/pic.png", AssetPathMapper.ToCdnUrl("https://example.com/pic.png"));
    }

    /// <summary>不带 <c>assets/</c> 也不带 <c>v1/</c> 的形态退化为 game/assets。</summary>
    [Fact]
    public void ToCdnUrl_FallsBackToGameAssets()
    {
        Assert.Equal(
            Base + "/game/assets/characters/aatrox/hud/aatrox_square.png",
            AssetPathMapper.ToCdnUrl("/lol-game-data/assets/characters/aatrox/hud/aatrox_square.png"));
    }

    [Fact]
    public void ToCdnUrl_SupportsCustomBase()
    {
        Assert.Equal(
            "http://127.0.0.1:8080/game/assets/x.png",
            AssetPathMapper.ToCdnUrl("/lol-game-data/assets/x.png", "http://127.0.0.1:8080/"));
    }

    [Theory]
    [InlineData("/lol-game-data/assets/v1/champion-icons/54.png")]
    [InlineData("/fe/lol-loot/assets/loot_item.png")]
    [InlineData("https://raw.communitydragon.org/latest/game/assets/x.png")]
    [InlineData("http://example.com/a.jpg")]
    public void IsAllowedPath_AcceptsKnownPrefixes(string path) => Assert.True(AssetPathMapper.IsAllowedPath(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\Windows\\System32\\config\\SAM")]
    [InlineData("/etc/passwd")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("/lol-game-data/")]
    public void IsAllowedPath_RejectsEverythingElse(string? path) => Assert.False(AssetPathMapper.IsAllowedPath(path));

    [Fact]
    public void NormalizeLcuPath_LowercasesGameDataOnly()
    {
        Assert.Equal(
            "/lol-game-data/assets/assets/items/icons2d/x.png",
            AssetPathMapper.NormalizeLcuPath("/lol-game-data/assets/ASSETS/Items/Icons2D/x.png"));

        // 战利品路径大小写敏感，必须原样保留
        const string loot = "/fe/lol-loot/assets/Loot_Item.PNG";
        Assert.Equal(loot, AssetPathMapper.NormalizeLcuPath(loot));
    }

    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.PNG", "image/png")]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("a.svg", "image/svg+xml")]
    [InlineData("a.unknown", "image/png")]
    [InlineData("no-extension", "image/png")]
    public void GuessContentType_UsesExtension(string path, string expected) =>
        Assert.Equal(expected, AssetPathMapper.GuessContentType(path));

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/webp", ".webp")]
    [InlineData("image/svg+xml", ".svg")]
    [InlineData("image/png; charset=binary", ".png")]
    [InlineData(null, ".png")]
    public void ExtensionForContentType_MapsKnownTypes(string? contentType, string expected) =>
        Assert.Equal(expected, AssetPathMapper.ExtensionForContentType(contentType));

    /// <summary>扩展名里带查询串或非字母数字时截断，避免生成非法文件名。</summary>
    [Theory]
    [InlineData("a/b.png?x=1", "png")]
    [InlineData("a/b.pngx", "pngx")]
    [InlineData("a/b", "")]
    [InlineData("a/b.", "")]
    public void ExtractExtension_TruncatesAtNonAlphanumeric(string path, string expected) =>
        Assert.Equal(expected, AssetPathMapper.ExtractExtension(path));
}
