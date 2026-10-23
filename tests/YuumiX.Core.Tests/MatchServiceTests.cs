using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Lcu;
using YuumiX.Testing.Lcu;

namespace YuumiX.Core.Tests;

/// <summary>
/// 战绩服务的离线验收：真套接字（<see cref="FakeLcuServer"/>）+ 真传输层
/// （<see cref="LcuHttpTransport"/>），只把 LCU 换掉。
///
/// 为什么不给 <see cref="MatchService"/> 塞一个假的 <see cref="ILcuTransport"/>：
/// 那样验证不到真实链路里的三件事——路径是否命中白名单、查询串是否拼对、
/// 并发限流是否真的作用在同一个传输层实例上。这三处恰好是最容易写错又最难在界面上发现的地方。
/// </summary>
public sealed class MatchServiceTests
{
    private const string Puuid = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task GetMatchHistoryAsync_ParsesFixtureAndPublishesStatus()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        string body = MatchFixtures.ByName("top-solo").ReadText();
        server.Handler = _ => FakeResponse.Ok(body);

        using LcuHttpTransport transport = NewTransport(server);
        var service = new MatchService(() => transport);
        var statuses = new List<MatchServiceStatus>();
        using IDisposable subscription = service.Status.Subscribe(status =>
        {
            lock (statuses)
            {
                statuses.Add(status);
            }
        });

        MatchHistoryPage page = await service.GetMatchHistoryAsync(Puuid, 0, 19);

        MatchSummary summary = Assert.Single(page.Games);
        Assert.Equal(430, summary.QueueId);
        Assert.Equal("匹配模式", summary.QueueName);
        Assert.Equal(0, page.BeginIndex);
        Assert.Equal(19, page.EndIndex);

        // 夹具的 gameCount = 1 → 没有下一页
        Assert.Equal(1, page.TotalCount);
        Assert.False(page.HasMore);

        lock (statuses)
        {
            Assert.Contains(statuses, status => status.State == MatchLoadState.Loading);
            Assert.Contains(statuses, status => status.State == MatchLoadState.Loaded);
        }

        Assert.Equal(0, service.InFlightPageCount);
    }

    [Fact]
    public async Task GetMatchHistoryAsync_BuildsPathWithPagingQuery()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok("""{"games":{"gameCount":0,"games":[]}}""");

        using LcuHttpTransport transport = NewTransport(server);
        var service = new MatchService(() => transport);

        await service.GetMatchHistoryAsync(Puuid, 20, 39);

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal("GET", request.Method);
        Assert.StartsWith($"/lol-match-history/v1/products/lol/{Puuid}/matches", request.Path, StringComparison.Ordinal);
        Assert.Contains("begIndex=20", request.Path, StringComparison.Ordinal);
        Assert.Contains("endIndex=39", request.Path, StringComparison.Ordinal);
    }

    /// <summary>同一 gameId 在翻页边界重复下发时只保留一条，否则列表会出现重复卡片。</summary>
    [Fact]
    public async Task GetMatchHistoryAsync_DeduplicatesDuplicateGameIds()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        string game = MatchFixtures.ByName("minimal-420").ReadText();
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(game);
        string single = document.RootElement.GetProperty("games").GetProperty("games")[0].GetRawText();
        server.Handler = _ => FakeResponse.Ok("{\"games\":{\"games\":[" + single + "," + single + "]}}");

        using LcuHttpTransport transport = NewTransport(server);
        var service = new MatchService(() => transport);

        MatchHistoryPage page = await service.GetMatchHistoryAsync(Puuid, 0, 1);

        Assert.Single(page.Games);
    }

    [Fact]
    public async Task GetMatchHistoryAsync_ReturnsEmptyOnNotFound()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.NotFound();

        using LcuHttpTransport transport = NewTransport(server);
        var service = new MatchService(() => transport);
        var statuses = new List<MatchServiceStatus>();
        using IDisposable subscription = service.Status.Subscribe(status =>
        {
            lock (statuses)
            {
                statuses.Add(status);
            }
        });

        MatchHistoryPage page = await service.GetMatchHistoryAsync(Puuid, 0, 19);

        Assert.Empty(page.Games);
        Assert.False(page.HasMore);
        lock (statuses)
        {
            Assert.Contains(statuses, status => status.State == MatchLoadState.Failed);
        }
    }

    [Fact]
    public async Task GetMatchHistoryAsync_ReturnsEmptyOnServerError()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.ServerError();

        using LcuHttpTransport transport = NewTransport(server);
        var service = new MatchService(() => transport);

        MatchHistoryPage page = await service.GetMatchHistoryAsync(Puuid, 0, 19);

        Assert.Empty(page.Games);
    }

    /// <summary>客户端未连接时给空页而不是抛异常，界面只需看状态流。</summary>
    [Fact]
    public async Task GetMatchHistoryAsync_ReturnsEmptyWithoutConnection()
    {
        var service = new MatchService(() => null);
        var statuses = new List<MatchServiceStatus>();
        using IDisposable subscription = service.Status.Subscribe(status =>
        {
            lock (statuses)
            {
                statuses.Add(status);
            }
        });

        MatchHistoryPage page = await service.GetMatchHistoryAsync(Puuid, 0, 19);

        Assert.Empty(page.Games);
        lock (statuses)
        {
            MatchServiceStatus failed = Assert.Single(statuses, status => status.State == MatchLoadState.Failed);
            Assert.Contains("未连接", failed.Message ?? string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>同参数并发只打一次 HTTP：两个组件同时要第一页是常态（列表 + 预取）。</summary>
    [Fact]
    public async Task GetMatchHistoryAsync_ConcurrentIdenticalRequestsShareOneCall()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok(MatchFixtures.ByName("top-solo").ReadText());
        using LcuHttpTransport transport = NewTransport(server);
        var service = new MatchService(() => transport);

        Task<MatchHistoryPage> first = service.GetMatchHistoryAsync(Puuid, 0, 19);
        Task<MatchHistoryPage> second = service.GetMatchHistoryAsync(Puuid, 0, 19);

        MatchHistoryPage[] pages = await Task.WhenAll(first, second);

        Assert.Single(server.Requests);
        Assert.Equal(pages[0].Games.Count, pages[1].Games.Count);
        Assert.Equal(0, service.InFlightPageCount);
    }

    [Fact]
    public async Task GetMatchDetailAsync_CachesByGameIdUntilInvalidated()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        string detailBody = GameBodyOf("top-solo");
        server.Handler = _ => FakeResponse.Ok(detailBody);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new MatchService(() => transport);

        MatchDetail? first = await service.GetMatchDetailAsync(11395344574);
        MatchDetail? second = await service.GetMatchDetailAsync(11395344574);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Single(server.Requests);
        Assert.Equal(1, service.CachedDetailCount);

        service.InvalidateCache();
        Assert.Equal(0, service.CachedDetailCount);

        await service.GetMatchDetailAsync(11395344574);

        // 清空后必须重新打一次 HTTP，并把结果重新缓存
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(1, service.CachedDetailCount);
    }

    /// <summary>
    /// 详情按 gameId 缓存，但"查询者胜负"必须按调用方的 puuid 现场补算——
    /// 否则 A 查过之后，B 查询同一局会看到 A 的胜负结论。
    /// </summary>
    [Fact]
    public async Task GetMatchDetailAsync_ComputesQueriedWinPerCaller()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        string detailBody = GameBodyOf("top-solo");
        server.Handler = _ => FakeResponse.Ok(detailBody);
        using LcuHttpTransport transport = NewTransport(server);
        var service = new MatchService(() => transport);

        MatchDetail? asOwner = await service.GetMatchDetailAsync(11395344574, "<redacted>");
        MatchDetail? asStranger = await service.GetMatchDetailAsync(11395344574, "someone-else");
        MatchDetail? anonymous = await service.GetMatchDetailAsync(11395344574);

        Assert.True(asOwner?.QueriedPlayerWin);
        Assert.Null(asStranger?.QueriedPlayerWin);
        Assert.Null(anonymous?.QueriedPlayerWin);

        // 三次查询共用一次 HTTP：缓存的是与查询者无关的版本
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task GetMatchDetailAsync_ReturnsNullWhenGameMissing()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.NotFound();
        using LcuHttpTransport transport = NewTransport(server);
        var service = new MatchService(() => transport);

        Assert.Null(await service.GetMatchDetailAsync(42));
    }

    [Fact]
    public async Task GetMatchDetailAsync_ReturnsNullWithoutConnection()
    {
        var service = new MatchService(() => null);

        Assert.Null(await service.GetMatchDetailAsync(42));
    }

    [Theory]
    [InlineData(20, 0, 19, 20, false)]  // 总数 20，本页 0–19 即末页
    [InlineData(40, 0, 19, 20, true)]   // 还有下一页
    [InlineData(10, 0, 19, 10, false)]  // 本页未填满
    [InlineData(null, 0, 19, 20, true)] // 无 gameCount：填满即认为还有
    [InlineData(null, 0, 19, 3, false)] // 无 gameCount 且未填满
    public void HasMore_FollowsGameCountThenPageFill(
        int? totalCount,
        int beginIndex,
        int endIndex,
        int returned,
        bool expected)
    {
        Assert.Equal(expected, MatchService.HasMore(totalCount, beginIndex, endIndex, returned));
    }

    [Fact]
    public async Task GetMatchHistoryAsync_RejectsInvalidRange()
    {
        var service = new MatchService(() => null);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetMatchHistoryAsync(Puuid, 20, 10));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetMatchHistoryAsync(Puuid, -1, 10));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetMatchHistoryAsync("  ", 0, 10));
    }

    /// <summary>
    /// 取出夹具里的单局对象。列表接口返回的是 <c>{ games: { games: [...] } }</c> 外层，
    /// 而详情接口返回的是单局对象本体——两者结构不同，不能直接拿列表载荷去调详情解析。
    /// </summary>
    private static string GameBodyOf(string fixtureName)
    {
        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(MatchFixtures.ByName(fixtureName).ReadText());

        return document.RootElement.GetProperty("games").GetProperty("games")[0].GetRawText();
    }

    private static LcuHttpTransport NewTransport(FakeLcuServer server) => new(
        server.BaseAddress,
        server.AuthorizationHeader,
        maxConcurrency: 4,
        requestTimeout: TimeSpan.FromSeconds(10),
        NullLogger<LcuHttpTransport>.Instance);
}
