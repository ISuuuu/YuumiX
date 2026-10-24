using System.Text.Json;
using YuumiX.Core.Updates;
using Xunit;

namespace YuumiX.Core.Tests;

/// <summary>
/// 在线更新的纯函数验收（requirements-settings.md §5：GitHub 域名白名单、版本归一化、
/// 缓存 TTL 与缺当前版本强制刷新、便携版无 target 视为无更新）。
/// </summary>
public sealed class UpdateServiceTests
{
    // ── GitHub 域名白名单 ───────────────────────────────────────

    [Theory]
    [InlineData("https://api.github.com/repos/ISuuuu/YuumiX/releases", true)]
    [InlineData("https://github.com/ISuuuu/YuumiX/releases/latest", true)]
    [InlineData("https://raw.githubusercontent.com/ISuuuu/YuumiX/main/README.md", true)]
    [InlineData("https://objects.githubusercontent.com/asset.zip", true)] // *.githubusercontent.com
    [InlineData("https://evil.example.com/releases", false)]
    [InlineData("https://github.com.evil.com/releases", false)]           // 前缀伪装
    [InlineData("ftp://api.github.com/x", false)]
    [InlineData("not a url", false)]
    public void IsAllowedGitHubUrl_EnforcesWhitelist(string url, bool expected)
    {
        Assert.Equal(expected, GitHubClient.IsAllowedGitHubUrl(url));
    }

    // ── 版本归一化与比较 ────────────────────────────────────────

    [Theory]
    [InlineData(" v1.2.3 ", "1.2.3")]
    [InlineData("v10.0.1", "10.0.1")]
    [InlineData("1.2.3", "1.2.3")]
    public void NormalizeVersion_StripsVAndWhitespace(string input, string expected)
    {
        Assert.Equal(expected, GitHubClient.NormalizeVersion(input));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.2", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData("0.2.0", "0.1.9", true)]
    [InlineData("abc", "1.0.0", false)]   // 解析失败不误报
    public void IsNewerVersion_ComparesSemver(string latest, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewerVersion(latest, current));
    }

    // ── 更新日志缓存新鲜度 ──────────────────────────────────────

    private static UpdateService.ReleaseEntry Entry(string tag) => new(tag, "2026-10-24T00:00:00Z", "notes");

    [Fact]
    public void IsCacheFresh_EmptyCache_IsStale()
    {
        Assert.False(UpdateService.IsCacheFresh([], "1.0.0", TimeSpan.Zero));
    }

    [Fact]
    public void IsCacheFresh_WithinTtl_WithCurrentVersion_IsFresh()
    {
        var cache = new[] { Entry("v1.0.0"), Entry("v1.1.0") };
        Assert.True(UpdateService.IsCacheFresh(cache, "1.0.0", TimeSpan.FromHours(23)));
        Assert.True(UpdateService.IsCacheFresh(cache, "v1.1.0", TimeSpan.FromHours(23))); // 归一化后比对
    }

    [Fact]
    public void IsCacheFresh_PastTtl_IsStale()
    {
        var cache = new[] { Entry("v1.0.0") };
        Assert.False(UpdateService.IsCacheFresh(cache, "1.0.0", UpdateService.CacheTtl + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void IsCacheFresh_MissingCurrentVersion_IsStale()
    {
        // 刚升级到新版本而缓存里没有它 → 强制刷新（旧 get_release_changelog 语义）
        var cache = new[] { Entry("v1.0.0") };
        Assert.False(UpdateService.IsCacheFresh(cache, "1.1.0", TimeSpan.FromMinutes(1)));
    }

    // ── Releases 载荷解析 ───────────────────────────────────────

    [Fact]
    public void ParseReleases_ReadsTagDateBody()
    {
        const string json = """
            [
              { "tag_name": "v1.1.0", "published_at": "2026-10-20T12:00:00Z", "body": "修复若干问题" },
              { "tag_name": "v1.0.0", "published_at": "2026-10-01T08:00:00Z", "body": "首个版本" }
            ]
            """;

        var releases = UpdateService.ParseReleases(json);

        Assert.Equal(2, releases.Count);
        Assert.Equal("v1.1.0", releases[0].Tag);
        Assert.Equal("修复若干问题", releases[0].Body);
    }

    // ── 便携资产判定 ────────────────────────────────────────────

    [Fact]
    public void FindPortableAssetUrl_RequiresPortableZip()
    {
        var release = JsonDocument.Parse("""
            {
              "tag_name": "v1.1.0",
              "assets": [
                { "name": "YuumiX-setup.exe", "browser_download_url": "https://github.com/x/YuumiX-setup.exe" },
                { "name": "YuumiX-windows-x86_64-portable.zip", "browser_download_url": "https://github.com/x/portable.zip" }
              ]
            }
            """).RootElement;

        Assert.Equal("https://github.com/x/portable.zip", UpdateService.FindPortableAssetUrl(release));
    }

    [Fact]
    public void FindPortableAssetUrl_MissingPortable_ReturnsNull()
    {
        var release = JsonDocument.Parse("""
            {
              "tag_name": "v1.1.0",
              "assets": [
                { "name": "YuumiX-setup.exe", "browser_download_url": "https://github.com/x/YuumiX-setup.exe" }
              ]
            }
            """).RootElement;

        // 便携版没有 portable 资产 → 视为无更新（旧 portable target 语义）
        Assert.Null(UpdateService.FindPortableAssetUrl(release));
    }
}
