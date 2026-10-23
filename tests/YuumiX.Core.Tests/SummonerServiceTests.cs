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

    private static LcuHttpTransport NewTransport(FakeLcuServer server) => new(
        server.BaseAddress,
        server.AuthorizationHeader,
        maxConcurrency: 4,
        requestTimeout: TimeSpan.FromSeconds(10),
        NullLogger<LcuHttpTransport>.Instance);
}
