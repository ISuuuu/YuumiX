using System.Net;
using System.Text;
using Xunit;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Features.Tools.Models;
using YuumiX.Core.Runtime;

namespace YuumiX.Core.Tests;

/// <summary>
/// 英雄别名索引（<see cref="ChampionAliasProvider"/>）的刷新策略与缓存行为验证：
/// 首次拉取、版本变化、TTL 过期、新英雄补拉、失败回退、缓存损坏回退、社区补丁不被覆盖。
/// </summary>
public sealed class ChampionAliasProviderTests
{
    private static readonly int[] KnownIds = [64];

    [Fact]
    public async Task FirstRun_DownloadsAndWritesCache()
    {
        using var appData = new TempDirectory();
        var handler = new StubHandler(_ => Ok(HeroJson((64, "瞎子,李青,xiazi,lq,LeeSin"))));
        var provider = NewProvider(appData, handler, out _, out _);

        Assert.True(await provider.RefreshIfNeededAsync(KnownIds));

        Assert.Equal(1, handler.Calls);
        Assert.Contains("xiazi", provider.GetKeywords(64), StringComparison.Ordinal);
        Assert.True(File.Exists(provider.CacheFilePath));
    }

    [Fact]
    public async Task SameVersionAndFreshCache_SkipsNetwork()
    {
        using var appData = new TempDirectory();
        var first = new StubHandler(_ => Ok(HeroJson((64, "瞎子,李青,xiazi"))));
        var provider1 = NewProvider(appData, first, out _, out _);
        Assert.True(await provider1.RefreshIfNeededAsync(KnownIds));

        // 同一数据目录 + 同一版本号：第二次启动不应再联网
        var second = new StubHandler(_ => Ok(HeroJson((64, "瞎子,李青,xiazi"))));
        var provider2 = NewProvider(appData, second, out _, out _);

        Assert.False(await provider2.RefreshIfNeededAsync(KnownIds));
        Assert.Equal(0, second.Calls);
        Assert.Contains("xiazi", provider2.GetKeywords(64), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionChanged_RefreshesAgain()
    {
        using var appData = new TempDirectory();
        var first = new StubHandler(_ => Ok(HeroJson((64, "瞎子,李青,xiazi"))));
        var provider1 = NewProvider(appData, first, out _, out _);
        await provider1.RefreshIfNeededAsync(KnownIds);

        // 模拟升级：缓存里记的是旧版本号
        var second = new StubHandler(_ => Ok(HeroJson((64, "瞎子,李青,xiazi2"))));
        var provider2 = NewProvider(appData, second, out _, out var version);
        version.Value = "9.9.9";

        Assert.True(await provider2.RefreshIfNeededAsync(KnownIds));
        Assert.Equal(1, second.Calls);
        Assert.Contains("xiazi2", provider2.GetKeywords(64), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CacheExpiredBeyondTtl_RefreshesAgain()
    {
        using var appData = new TempDirectory();
        var first = new StubHandler(_ => Ok(HeroJson((64, "瞎子,李青,xiazi"))));
        var provider1 = NewProvider(appData, first, out var clock, out _);
        await provider1.RefreshIfNeededAsync(KnownIds);

        clock.Value.Now = clock.Value.Now.Add(ChampionAliasProvider.RefreshTtl + TimeSpan.FromHours(1));

        var second = new StubHandler(_ => Ok(HeroJson((64, "瞎子,李青,xiazi"))));
        var provider2 = NewProvider(appData, second, out var clock2, out _);
        clock2.Value.Now = clock.Value.Now;

        Assert.True(await provider2.RefreshIfNeededAsync(KnownIds));
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task UnknownChampionId_TriggersRefreshWithoutVersionChange()
    {
        using var appData = new TempDirectory();
        var first = new StubHandler(_ => Ok(HeroJson((64, "瞎子,李青,xiazi"))));
        var provider1 = NewProvider(appData, first, out _, out _);
        await provider1.RefreshIfNeededAsync(KnownIds);

        // 出现一个内置表与缓存都没有的新英雄 id（版本号未变）
        var second = new StubHandler(_ => Ok(HeroJson((64, "瞎子,李青,xiazi"), (12345, "新英雄,xinyingxiong"))));
        var provider2 = NewProvider(appData, second, out _, out _);

        Assert.False(provider2.Covers([64, 12345]));
        Assert.True(await provider2.RefreshIfNeededAsync([64, 12345]));
        Assert.Equal(1, second.Calls);
        Assert.Contains("xinyingxiong", provider2.GetKeywords(12345), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadFailure_FallsBackToBuiltinBaseline()
    {
        using var appData = new TempDirectory();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var provider = NewProvider(appData, handler, out _, out _);

        Assert.False(await provider.RefreshIfNeededAsync(KnownIds));
        Assert.False(File.Exists(provider.CacheFilePath));

        // 内置基线仍可搜：瞎子 → 李青
        var champ = new ChampionSummaryItem(64, "李青", "LeeSin", "盲僧", string.Empty);
        Assert.True(ChampionSearch.Matches(champ, "瞎子", provider));
        Assert.True(ChampionSearch.Matches(champ, "xiazi", provider));
    }

    [Fact]
    public async Task CorruptCacheFile_FallsBackToBuiltinBaseline()
    {
        using var appData = new TempDirectory();
        var probe = NewProvider(appData, new StubHandler(_ => Ok(HeroJson((64, "x")))), out _, out _);
        Directory.CreateDirectory(Path.GetDirectoryName(probe.CacheFilePath)!);
        await File.WriteAllTextAsync(probe.CacheFilePath, "{ this is not json");

        var provider = NewProvider(appData, new StubHandler(_ => Ok(HeroJson((64, "x")))), out _, out _);

        Assert.Contains("xiazi", provider.GetKeywords(64), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommunityPatchSurvivesRemoteOverwrite()
    {
        using var appData = new TempDirectory();
        // 腾讯原表不含「小火龙」，补丁必须始终生效
        var handler = new StubHandler(_ => Ok(HeroJson((901, "炽炎雏龙,斯莫德,chiyanchulong,simode"))));
        var provider = NewProvider(appData, handler, out _, out _);

        await provider.RefreshIfNeededAsync([901]);

        string keywords = provider.GetKeywords(901)!;
        Assert.Contains("chiyanchulong", keywords, StringComparison.Ordinal);
        Assert.Contains("小火龙", keywords, StringComparison.Ordinal);
        // 补丁只叠加一次：基线已含补丁时不重复追加
        Assert.Equal(keywords.IndexOf("小火龙", StringComparison.Ordinal), keywords.LastIndexOf("小火龙", StringComparison.Ordinal));

        var champ = new ChampionSummaryItem(901, "斯莫德", "Smolder", "炽炎雏龙", string.Empty);
        Assert.True(ChampionSearch.Matches(champ, "小火龙", provider));
    }

    [Fact]
    public void ParseSource_ReadsHeroIdAndKeywords()
    {
        var parsed = ChampionAliasProvider.ParseSource(HeroJson((64, "瞎子"), (67, "薇恩")));

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Count);
        Assert.Equal("瞎子", parsed[64].Keywords);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"hero\":[]}")]
    [InlineData("{\"other\":1}")]
    public void ParseSource_RejectsUnusablePayload(string body)
    {
        Assert.Null(ChampionAliasProvider.ParseSource(body));
    }

    // ── 测试支撑 ─────────────────────────────────────────────

    private static ChampionAliasProvider NewProvider(
        TempDirectory appData,
        StubHandler handler,
        out Ref<ManualTimeProvider> clock,
        out Ref<string> version)
    {
        var timeBox = new Ref<ManualTimeProvider>(new ManualTimeProvider());
        var versionBox = new Ref<string>("0.2.2");
        clock = timeBox;
        version = versionBox;

        var paths = RuntimePaths.Detect(
            executableDirectory: appData.File("exe"),
            roamingAppData: appData.Path);

        return new ChampionAliasProvider(
            paths,
            versionProvider: () => versionBox.Value,
            logger: null,
            mockHandler: handler,
            timeProvider: timeBox.Value);
    }

    private static string HeroJson(params (int Id, string Keywords)[] heroes) =>
        "{\"hero\":[" + string.Join(",", heroes.Select(h => $"{{\"heroId\":\"{h.Id}\",\"keywords\":\"{h.Keywords}\"}}")) + "]}";

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>可写引用盒：让测试在构造后继续改时钟/版本号（out 参数不能直接给 ref 变量赋值）。</summary>
    private sealed class Ref<T>(T value)
    {
        public T Value { get; set; } = value;
    }
}
