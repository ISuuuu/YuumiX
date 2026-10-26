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

    // ── 预发布版本（SemVer 2.0）──────────────────────────────────

    [Theory]
    [InlineData("1.0.0", "1.0.0-rc.1", true)]    // 正式版高于同号预发布
    [InlineData("1.0.0-rc.1", "1.0.0", false)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1", true)]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.9", true)] // 数值比较而非字典序
    [InlineData("1.0.0-rc.1", "1.0.0-beta.1", true)] // 字母标识按序数：beta < rc
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha", true)] // 前缀相同时更长者更高
    [InlineData("0.2.0-rc.1", "0.1.0", true)]
    [InlineData("0.2.0-rc.1", "0.2.0-rc.1", false)]
    [InlineData("1.0.0+build.5", "1.0.0", false)]  // 构建元数据不参与比较
    public void IsNewerVersion_HandlesPrerelease(string latest, string current, bool expected)
    {
        // 旧实现用 Version.TryParse，预发布后缀会解析失败并一律返回 false，
        // 导致预发布版本永远收不到更新提示——这组用例锁住该回归。
        Assert.Equal(expected, UpdateService.IsNewerVersion(latest, current));
    }

    // ── 更新通道选择 ────────────────────────────────────────────

    private static UpdateService.ReleaseEntry AssetEntry(
        string tag,
        string? url = "https://github.com/x/portable.zip",
        bool prerelease = false,
        string? setupUrl = null)
        => new(tag, "2026-10-25T00:00:00Z", "notes", url, prerelease, setupUrl);

    [Fact]
    public void SelectCandidate_StableUserNeverReceivesPrerelease()
    {
        var releases = new[] { AssetEntry("v0.2.0", prerelease: true), AssetEntry("v0.1.1") };

        var picked = UpdateService.SelectCandidate(releases, "0.1.0", isPortable: true);

        Assert.NotNull(picked);
        Assert.Equal("v0.1.1", picked.Tag);
    }

    [Fact]
    public void SelectCandidate_PrereleaseUserContinuesOnPrereleaseChannel()
    {
        var releases = new[] { AssetEntry("v0.2.0", prerelease: true), AssetEntry("v0.1.1") };

        var picked = UpdateService.SelectCandidate(releases, "0.2.0-rc.1", isPortable: true);

        Assert.NotNull(picked);
        Assert.Equal("v0.2.0", picked.Tag);
    }

    /// <summary>
    /// 预发布用户必须能收到同号正式版（0.1.0-rc.1 → 0.1.0 的「转正」路径）。
    /// 通道过滤写成 <c>(候选预发布 &amp;&amp; 当前非预发布) 才跳过</c> 正是为了这条：
    /// 若误写成「当前是预发布就只收预发布」，rc 用户会被永久钉死在预发布通道。
    /// </summary>
    [Theory]
    [InlineData(true)]  // 便携版：认 portable 资产
    [InlineData(false)] // 安装版：认 setup 资产
    public void SelectCandidate_PrereleaseUserReceivesSameVersionStableRelease(bool isPortable)
    {
        var releases = new[]
        {
            AssetEntry("v0.1.0", setupUrl: "https://github.com/x/setup-0.1.0.exe"),
            AssetEntry("v0.1.0-rc.1", setupUrl: "https://github.com/x/setup-0.1.0-rc1.exe"),
        };

        var picked = UpdateService.SelectCandidate(releases, "0.1.0-rc.1", isPortable);

        Assert.NotNull(picked);
        Assert.Equal("v0.1.0", picked.Tag);
        Assert.False(picked.IsPrerelease);
    }

    /// <summary>
    /// 转正之后再发预发布，已转正的用户不应收到——否则「升级到正式版」反而把人带回尝鲜通道。
    /// </summary>
    [Fact]
    public void SelectCandidate_UserOnStableDoesNotGetPrereleaseAfterGraduating()
    {
        var releases = new[] { AssetEntry("v0.1.1-rc.1", prerelease: true), AssetEntry("v0.1.0") };

        Assert.Null(UpdateService.SelectCandidate(releases, "0.1.0", isPortable: true));
    }

    [Fact]
    public void SelectCandidate_PicksHighestNewerVersion()
    {
        var releases = new[] { AssetEntry("v0.1.2"), AssetEntry("v0.1.10"), AssetEntry("v0.1.3") };

        var picked = UpdateService.SelectCandidate(releases, "0.1.0", isPortable: true);

        Assert.NotNull(picked);
        Assert.Equal("v0.1.10", picked.Tag);
    }

    [Fact]
    public void SelectCandidate_EachChannelRequiresItsOwnAsset()
    {
        // v0.2.0 只发了 setup.exe，没发 portable zip
        var releases = new[] { AssetEntry("v0.2.0", url: null, setupUrl: "https://github.com/x/s.exe"), AssetEntry("v0.1.1") };

        // 便携版拿不到 0.2.0 的 zip，退到 0.1.1
        Assert.Equal("v0.1.1", UpdateService.SelectCandidate(releases, "0.1.0", isPortable: true)?.Tag);
        // 安装版有对应资产，应当收到 0.2.0
        Assert.Equal("v0.2.0", UpdateService.SelectCandidate(releases, "0.1.0", isPortable: false)?.Tag);
    }

    [Fact]
    public void SelectCandidate_InstallerSkipsReleaseWithoutSetupAsset()
    {
        // v0.2.0 只发了 zip（AssetEntry 默认给 portable url），安装版应跳过它
        var releases = new[]
        {
            AssetEntry("v0.2.0"),
            AssetEntry("v0.1.1", url: null, setupUrl: "https://github.com/x/setup-0.1.1.exe"),
        };

        Assert.Equal("v0.1.1", UpdateService.SelectCandidate(releases, "0.1.0", isPortable: false)?.Tag);
        Assert.Equal("v0.2.0", UpdateService.SelectCandidate(releases, "0.1.0", isPortable: true)?.Tag);
    }

    [Fact]
    public void SelectCandidate_ReturnsNullWhenAssetMissingForEveryNewerVersion()
    {
        // 所有更高版本都缺安装版资产 → 安装版应判定「无更新」，而不是给一个下不动的按钮
        var releases = new[] { AssetEntry("v0.2.0"), AssetEntry("v0.1.1") };

        Assert.Null(UpdateService.SelectCandidate(releases, "0.1.0", isPortable: false));
    }

    [Fact]
    public void SelectCandidate_ReturnsNullWhenNothingNewer()
    {
        var releases = new[] { AssetEntry("v0.1.0"), AssetEntry("v0.0.9") };

        Assert.Null(UpdateService.SelectCandidate(releases, "0.1.0", isPortable: true));
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

    [Fact]
    public void ParseReleases_ReadsPrereleaseAndPortableAsset_SkipsDraft()
    {
        const string json = """
            [
              { "tag_name": "v0.2.0", "prerelease": true, "body": "RC",
                "assets": [ { "name": "YuumiX_v0.2.0_portable.zip", "browser_download_url": "https://github.com/x/p.zip" } ] },
              { "tag_name": "v0.1.1", "prerelease": false, "body": "fix",
                "assets": [ { "name": "YuumiX-setup.exe", "browser_download_url": "https://github.com/x/s.exe" } ] },
              { "tag_name": "v0.3.0", "draft": true, "body": "草稿" }
            ]
            """;

        var releases = UpdateService.ParseReleases(json);

        Assert.Equal(2, releases.Count); // draft 不出现在结果里
        Assert.True(releases[0].IsPrerelease);
        Assert.Equal("https://github.com/x/p.zip", releases[0].PortableAssetUrl);
        Assert.Null(releases[0].SetupAssetUrl); // 只有 zip 的版本不提供安装版资产
        Assert.False(releases[1].IsPrerelease);
        Assert.Null(releases[1].PortableAssetUrl);
        Assert.Equal("https://github.com/x/s.exe", releases[1].SetupAssetUrl);
    }

    // ── 便携版替换脚本 ──────────────────────────────────────────

    [Fact]
    public void BuildApplyScript_CopiesWithoutMirroringUserData()
    {
        string script = UpdateService.BuildApplyScript(@"C:\tmp\src", @"C:\app", @"C:\app\YuumiX.exe", 4321);

        Assert.Contains(@"chcp 65001", script);           // 中文用户名路径
        Assert.Contains(@"set ""PID=4321""", script);      // 等父进程退出
        Assert.Contains(@"robocopy ""%SRC%"" ""%DST%"" /E", script);
        Assert.Contains(@"start """" /D ""%DST%""", script);

        // /MIR 会删除目标目录里多出来的文件，等于清掉用户的 data 与 log
        Assert.DoesNotContain("/MIR", script);
        Assert.DoesNotContain("/PURGE", script);
        // timeout 在无控制台窗口时会因 stdin 重定向报错
        Assert.DoesNotContain("timeout ", script);
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

    // ── 安装版资产判定与静默安装脚本 ────────────────────────────

    [Fact]
    public void FindSetupAssetUrl_RequiresSetupExeAndRejectsPortableName()
    {
        var release = JsonDocument.Parse("""
            {
              "tag_name": "v1.1.0",
              "assets": [
                { "name": "YuumiX_v1.1.0_portable.zip", "browser_download_url": "https://github.com/x/portable.zip" },
                { "name": "YuumiX_portable_setup.exe", "browser_download_url": "https://github.com/x/mixed.exe" },
                { "name": "YuumiX_v1.1.0_x64-setup.exe", "browser_download_url": "https://github.com/x/setup.exe" }
              ]
            }
            """).RootElement;

        // 命中含 setup 的 exe；同时含 portable 的混合命名两边都不认
        Assert.Equal("https://github.com/x/setup.exe", UpdateService.FindSetupAssetUrl(release));
        Assert.Equal("https://github.com/x/portable.zip", UpdateService.FindPortableAssetUrl(release));
    }

    [Fact]
    public void FindSetupAssetUrl_MissingSetup_ReturnsNull()
    {
        var release = JsonDocument.Parse("""
            {
              "tag_name": "v1.1.0",
              "assets": [
                { "name": "YuumiX_v1.1.0_portable.zip", "browser_download_url": "https://github.com/x/portable.zip" }
              ]
            }
            """).RootElement;

        Assert.Null(UpdateService.FindSetupAssetUrl(release));
    }

    [Fact]
    public void BuildInstallerScript_RunsSilentSetupInPlaceAfterExit()
    {
        string script = UpdateService.BuildInstallerScript(
            @"C:\tmp\downloads\YuumiX-0.2.0.exe",
            @"C:\Users\me\AppData\Local\Programs\YuumiX",
            @"C:\Users\me\AppData\Local\Programs\YuumiX\YuumiX.exe",
            4321);

        Assert.Contains(@"chcp 65001", script);              // 中文用户名路径
        Assert.Contains(@"set ""PID=4321""", script);         // 等父进程退出
        Assert.Contains("/SILENT", script);
        Assert.Contains("/NORESTART", script);
        // /DIR 必须显式传：不传会沿用注册表里的上次安装路径，用户改过目录就会装到别处
        Assert.Contains(@"/DIR=""%DST%""", script);
        Assert.Contains(@"start """" /D ""%DST%""", script);  // 装完拉起新版本

        // 安装版不走目录覆盖分支
        Assert.DoesNotContain("robocopy", script);
        // timeout 在无控制台窗口时会因 stdin 重定向报错
        Assert.DoesNotContain("timeout ", script);
    }
}
