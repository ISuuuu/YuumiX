using System.Net;
using System.Text;
using Xunit;
using YuumiX.Core.Assets;
using YuumiX.Core.Lcu;
using YuumiX.Testing.Lcu;

namespace YuumiX.Core.Tests;

/// <summary>
/// 资源聚合：三级链路、缓存落盘、在途去重、双前缀降级、白名单拦截。
/// 两个来源都走真实回环套接字（<see cref="FakeLcuServer"/>），
/// 因为这一层的故障几乎都出在「请求实际发成了什么样」，mock 掉就测不到。
/// </summary>
public sealed class AssetServiceTests
{
    private const string IconPath = "/lol-game-data/assets/v1/champion-icons/54.png";

    [Fact]
    public async Task GetAsync_LcuHit_StoresToCacheAndSecondCallUsesCache()
    {
        await using var lcu = FakeLcuServer.Start();
        lcu.Handler = _ => new FakeResponse(200, "IMG-54", "image/png");

        using var directory = new TempDirectory();
        var cache = new AssetCache(directory.Path);
        using var source = NewLcuSource(lcu);
        using var service = new AssetService(cache, [source]);

        var payload = await service.GetAsync(IconPath);

        Assert.NotNull(payload);
        Assert.Equal("LCU", payload.Source);
        Assert.Equal("IMG-54", Encoding.UTF8.GetString(payload.Bytes));
        Assert.NotNull(cache.TryGetPath(IconPath));

        var second = await service.GetAsync(IconPath);

        Assert.NotNull(second);
        Assert.Equal("Cache", second.Source);
        Assert.Single(lcu.Requests); // 第二次没有再打 LCU
    }

    [Fact]
    public async Task GetAsync_LcuMiss_FallsBackToCdn()
    {
        await using var lcu = FakeLcuServer.Start();
        lcu.Handler = _ => FakeResponse.NotFound();

        await using var cdn = FakeLcuServer.Start();
        cdn.Handler = _ => new FakeResponse(200, "CDN-54", "image/png");

        using var directory = new TempDirectory();
        using var lcuSource = NewLcuSource(lcu);
        using var cdnSource = NewCdnSource(cdn);
        using var service = new AssetService(new AssetCache(directory.Path), [lcuSource, cdnSource]);

        var payload = await service.GetAsync(IconPath);

        Assert.NotNull(payload);
        Assert.Equal("CDragon", payload.Source);
        Assert.Equal("CDN-54", Encoding.UTF8.GetString(payload.Bytes));

        var requested = Assert.Single(cdn.Requests);
        Assert.Equal(
            "/plugins/rcp-be-lol-game-data/global/default/v1/champion-icons/54.png",
            requested.Path);
    }

    [Fact]
    public async Task GetAsync_BothSourcesMiss_ReturnsNull()
    {
        await using var lcu = FakeLcuServer.Start();
        lcu.Handler = _ => FakeResponse.NotFound();

        await using var cdn = FakeLcuServer.Start();
        cdn.Handler = _ => FakeResponse.NotFound();

        using var directory = new TempDirectory();
        using var lcuSource = NewLcuSource(lcu);
        using var cdnSource = NewCdnSource(cdn);
        using var service = new AssetService(new AssetCache(directory.Path), [lcuSource, cdnSource]);

        Assert.Null(await service.GetAsync(IconPath));
    }

    /// <summary>
    /// 同一路径的并发请求只能打出去一次：列表滚动时十几项同时请求同一个头像，
    /// 没有去重就会成倍放大网络请求并互相覆盖缓存。
    /// </summary>
    [Fact]
    public async Task GetAsync_ConcurrentSamePath_IssuesSingleNetworkRequest()
    {
        await using var lcu = FakeLcuServer.Start();
        lcu.Handler = _ =>
        {
            Thread.Sleep(60); // 拉长窗口，确保请求真的重叠
            return new FakeResponse(200, "IMG", "image/png");
        };

        using var directory = new TempDirectory();
        using var source = NewLcuSource(lcu);
        using var service = new AssetService(new AssetCache(directory.Path), [source]);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.GetAsync(IconPath)));

        Assert.All(results, payload => Assert.NotNull(payload));
        Assert.Single(lcu.Requests);
    }

    [Fact]
    public async Task GetAsync_DisallowedPath_ReturnsNullWithoutAnyRequest()
    {
        await using var lcu = FakeLcuServer.Start();
        using var directory = new TempDirectory();
        using var source = NewLcuSource(lcu);
        using var service = new AssetService(new AssetCache(directory.Path), [source]);

        Assert.Null(await service.GetAsync("C:\\Windows\\win.ini"));
        Assert.Empty(lcu.Requests);
    }

    /// <summary>
    /// LCU 的部分 <c>iconPath</c> 带双重 <c>/assets/assets/</c>，这是历史数据形态。
    /// 404 时必须自动降级为单前缀再试一次。
    /// </summary>
    [Fact]
    public async Task GetAsync_DoubleAssetsPrefix_FallsBackToSinglePrefix()
    {
        await using var lcu = FakeLcuServer.Start();
        lcu.Handler = request => request.Path.Contains("/assets/assets/", StringComparison.Ordinal)
            ? FakeResponse.NotFound()
            : new FakeResponse(200, "IMG", "image/png");

        using var directory = new TempDirectory();
        using var source = NewLcuSource(lcu);
        using var service = new AssetService(new AssetCache(directory.Path), [source]);

        var payload = await service.GetAsync("/lol-game-data/assets/assets/characters/aatrox/hud/aatrox_square.png");

        Assert.NotNull(payload);
        Assert.Equal(2, lcu.Requests.Count);
        Assert.Equal(
            "/lol-game-data/assets/characters/aatrox/hud/aatrox_square.png",
            lcu.Requests[1].Path);
    }

    /// <summary>UI 主入口：返回可直接拼成 <c>file://</c> 的本地绝对路径。</summary>
    [Fact]
    public async Task GetCachedPathAsync_ReturnsLocalFilePath()
    {
        await using var lcu = FakeLcuServer.Start();
        lcu.Handler = _ => new FakeResponse(200, "IMG", "image/png");

        using var directory = new TempDirectory();
        using var source = NewLcuSource(lcu);
        using var service = new AssetService(new AssetCache(directory.Path), [source]);

        var path = await service.GetCachedPathAsync(IconPath);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(Path.Combine(directory.Path, AssetCache.Hash(IconPath) + ".png"), path);

        // 第二次直接命中磁盘，不再请求
        Assert.Equal(path, await service.GetCachedPathAsync(IconPath));
        Assert.Single(lcu.Requests);
    }

    [Fact]
    public void SourceNames_ReflectConfiguredChain()
    {
        using var directory = new TempDirectory();
        using var service = new AssetService(
            new AssetCache(directory.Path),
            [new CdnAssetSource("http://127.0.0.1:1", new SocketsHttpHandler())]);

        Assert.Equal(["CDragon"], service.SourceNames);
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 测试版 LCU 源：把基地址指向假服务端（明文 http），凭据仍需携带 token 以验证鉴权头。
    /// </summary>
    private static LcuAssetSource NewLcuSource(FakeLcuServer server) =>
        new(
            () => new LcuCredentials((uint)server.BaseAddress.Port, "asset-token"),
            baseAddressFactory: _ => server.BaseAddress);

    /// <summary>
    /// 测试版 CDN 源：基地址指向假服务端，并显式禁用代理——
    /// 生产实现走系统代理（本机是 127.0.0.1:7897），测试若沿用会在回环请求上被代理拦住。
    /// </summary>
    private static CdnAssetSource NewCdnSource(FakeLcuServer server) =>
        new(
            server.BaseAddress.ToString(),
            new SocketsHttpHandler { UseProxy = false });
}
