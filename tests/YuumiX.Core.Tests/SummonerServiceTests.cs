using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;
using YuumiX.Testing.Lcu;

namespace YuumiX.Core.Tests;

/// <summary>
/// 召唤师服务的离线验收（真套接字 + 真传输层）。
///
/// 重点在缓存边界：一次登录期间召唤师不会变，但**换号之后必须能立刻拿到新账号**，
/// 否则会出现"切了小号，搜索页还在查大号战绩"。所以缓存与 <see cref="ISummonerService.InvalidateCache"/>
/// 的配合必须锁死。
/// </summary>
public sealed class SummonerServiceTests
{
    private const string Payload =
        """
        {
          "accountId": 2000000001,
          "displayName": "测试玩家",
          "gameName": "测试玩家",
          "tagLine": "HN1",
          "percentCompleteForNextLevel": 42,
          "privacy": "PUBLIC",
          "profileIconId": 5213,
          "puuid": "11111111-2222-3333-4444-555555555555",
          "summonerId": 1234567890123,
          "summonerLevel": 512,
          "xpSinceLastLevel": 1000,
          "xpUntilNextLevel": 5000
        }
        """;

    [Fact]
    public async Task GetCurrentSummonerAsync_ParsesPayloadAndHitsExpectedPath()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok(Payload);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new SummonerService(() => transport);

        SummonerProfile? profile = await service.GetCurrentSummonerAsync();

        Assert.NotNull(profile);
        Assert.Equal("测试玩家#HN1", profile.RiotId);
        Assert.Equal(512, profile.SummonerLevel);

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal("/lol-summoner/v1/current-summoner", request.Path);
    }

    [Fact]
    public async Task GetCurrentSummonerAsync_CachesUntilInvalidated()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok(Payload);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new SummonerService(() => transport);

        await service.GetCurrentSummonerAsync();
        await service.GetCurrentSummonerAsync();
        Assert.Single(server.Requests);

        service.InvalidateCache();
        await service.GetCurrentSummonerAsync();

        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task GetCurrentSummonerAsync_ZeroTtlDisablesCache()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok(Payload);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new SummonerService(() => transport, cacheTtl: TimeSpan.Zero);

        await service.GetCurrentSummonerAsync();
        await service.GetCurrentSummonerAsync();

        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task GetCurrentSummonerAsync_ReturnsNullOnNotFound()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.NotFound();
        using LcuHttpTransport transport = NewTransport(server);
        var service = new SummonerService(() => transport);

        Assert.Null(await service.GetCurrentSummonerAsync());
    }

    [Fact]
    public async Task GetCurrentSummonerAsync_ReturnsNullWithoutConnection()
    {
        var service = new SummonerService(() => null);

        Assert.Null(await service.GetCurrentSummonerAsync());
    }

    /// <summary>载荷被截断时不得抛异常，给 <c>null</c> 让界面显示未登录态。</summary>
    [Fact]
    public async Task GetCurrentSummonerAsync_ReturnsNullOnBrokenJson()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok("{ \"accountId\": 1,");
        using LcuHttpTransport transport = NewTransport(server);
        var service = new SummonerService(() => transport);

        Assert.Null(await service.GetCurrentSummonerAsync());
    }

    /// <summary>
    /// 按名字搜索：<c>#</c> 必须转义进查询串，否则服务端只看到 <c>#</c> 之前的部分，
    /// 带 tagLine 的玩家永远搜不到（这是本接口最容易写错的一处）。
    /// </summary>
    [Fact]
    public async Task FindByNameAsync_EscapesRiotIdIntoQuery()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok(Payload);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new SummonerService(() => transport);

        SummonerProfile? profile = await service.FindByNameAsync("测试玩家#HN1");

        Assert.NotNull(profile);
        Assert.Equal("11111111-2222-3333-4444-555555555555", profile.Puuid);

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal("/lol-summoner/v1/summoners", request.Path.Split('?')[0]);
        Assert.Contains("%23", request.Path, StringComparison.Ordinal);
    }

    /// <summary>搜索不做缓存：连续查两个名字必须打两次请求，否则第二次会拿到上一个人的资料。</summary>
    [Fact]
    public async Task FindByNameAsync_DoesNotCache()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok(Payload);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new SummonerService(() => transport);

        await service.FindByNameAsync("甲#A");
        await service.FindByNameAsync("乙#B");

        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task FindByNameAsync_ReturnsNullOnNotFound()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.NotFound();
        using LcuHttpTransport transport = NewTransport(server);
        var service = new SummonerService(() => transport);

        Assert.Null(await service.FindByNameAsync("不存在的玩家#000"));
    }

    [Fact]
    public async Task FindByNameAsync_RejectsBlankName()
    {
        var service = new SummonerService(() => null);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.FindByNameAsync("   "));
    }

    /// <summary>
    /// 按 puuid 查询：命中 <c>/lol-summoner/v2/summoners/puuid/{puuid}</c>（与私密检测同接口），
    /// 载荷复用 SummonerParser 解析。
    /// </summary>
    [Fact]
    public async Task FindByPuuidAsync_ParsesPayloadAndHitsExpectedPath()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok(Payload);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new SummonerService(() => transport);

        SummonerProfile? profile = await service.FindByPuuidAsync("11111111-2222-3333-4444-555555555555");

        Assert.NotNull(profile);
        Assert.Equal("测试玩家#HN1", profile.RiotId);
        Assert.Equal(512, profile.SummonerLevel);

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal("/lol-summoner/v2/summoners/puuid/11111111-2222-3333-4444-555555555555", request.Path);
    }

    /// <summary>查不到（404）返回 <c>null</c> 而不抛异常，调用方用占位对象兜底。</summary>
    [Fact]
    public async Task FindByPuuidAsync_ReturnsNullOnNotFound()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.NotFound();
        using LcuHttpTransport transport = NewTransport(server);
        var service = new SummonerService(() => transport);

        Assert.Null(await service.FindByPuuidAsync("no-such-puuid"));
    }

    [Fact]
    public async Task FindByPuuidAsync_ReturnsNullWithoutConnection()
    {
        var service = new SummonerService(() => null);

        Assert.Null(await service.FindByPuuidAsync("any-puuid"));
    }

    /// <summary>载荷损坏时不得抛异常。</summary>
    [Fact]
    public async Task FindByPuuidAsync_ReturnsNullOnBrokenJson()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok("{ \"puuid\": \"");
        using LcuHttpTransport transport = NewTransport(server);
        var service = new SummonerService(() => transport);

        Assert.Null(await service.FindByPuuidAsync("any-puuid"));
    }

    [Fact]
    public async Task FindByPuuidAsync_RejectsBlankPuuid()
    {
        var service = new SummonerService(() => null);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.FindByPuuidAsync("  "));
    }

    /// <summary>
    /// 按 summonerId 查询：命中 <c>/lol-summoner/v1/summoners/{id}</c>，载荷复用 SummonerParser 解析。
    /// </summary>
    [Fact]
    public async Task FindBySummonerIdAsync_ParsesPayloadAndHitsExpectedPath()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok(Payload);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new SummonerService(() => transport);

        SummonerProfile? profile = await service.FindBySummonerIdAsync(123456789L);

        Assert.NotNull(profile);
        Assert.Equal("测试玩家#HN1", profile.RiotId);
        Assert.Equal(512, profile.SummonerLevel);

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal("/lol-summoner/v1/summoners/123456789", request.Path);
    }

    [Fact]
    public async Task FindBySummonerIdAsync_ReturnsNullOnNotFound()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.NotFound();
        using LcuHttpTransport transport = NewTransport(server);
        var service = new SummonerService(() => transport);

        Assert.Null(await service.FindBySummonerIdAsync(999999999L));
    }

    [Fact]
    public async Task FindBySummonerIdAsync_ReturnsNullWithoutConnection()
    {
        var service = new SummonerService(() => null);

        Assert.Null(await service.FindBySummonerIdAsync(123L));
    }

    [Fact]
    public async Task FindBySummonerIdAsync_ReturnsNullOnInvalidId()
    {
        var service = new SummonerService(() => null);

        Assert.Null(await service.FindBySummonerIdAsync(0));
        Assert.Null(await service.FindBySummonerIdAsync(-1));
    }

    [Fact]
    public async Task FindBySummonerIdAsync_ReturnsNullOnBrokenJson()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok("{ \"summonerId\": ");
        using LcuHttpTransport transport = NewTransport(server);
        var service = new SummonerService(() => transport);

        Assert.Null(await service.FindBySummonerIdAsync(123L));
    }

    [Fact]
    public async Task CurrentChanged_PublishesWhenProfileLoadedAndInvalidated()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok(Payload);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new SummonerService(() => transport);

        var events = new List<SummonerProfile?>();
        using var subscription = service.CurrentChanged.Subscribe(events.Add);

        // 首次读取成功，发布 profile
        SummonerProfile? profile = await service.GetCurrentSummonerAsync();
        Assert.NotNull(profile);
        Assert.Single(events);
        Assert.Equal("测试玩家#HN1", events[0]?.RiotId);

        // 命中缓存未变更，不重复发布
        await service.GetCurrentSummonerAsync();
        Assert.Single(events);

        // 清空缓存，发布 null
        service.InvalidateCache();
        Assert.Equal(2, events.Count);
        Assert.Null(events[1]);
    }

    private static LcuHttpTransport NewTransport(FakeLcuServer server) => new(
        server.BaseAddress,
        server.AuthorizationHeader,
        maxConcurrency: 4,
        requestTimeout: TimeSpan.FromSeconds(10),
        NullLogger<LcuHttpTransport>.Instance);
}
