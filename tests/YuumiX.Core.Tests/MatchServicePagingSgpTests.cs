using Xunit;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Lcu.Sgp;

namespace YuumiX.Core.Tests;

public sealed class MatchServicePagingSgpTests
{
    private const string Puuid = "22222222-3333-4444-5555-666666666666";

    /// <summary>
    /// 首页 20 场改由 SGP 提供（分路字段完整），第 2 页起继续按 20 起的偏移向后拉取。
    ///
    /// 断言从「第 1 页不调 SGP」改为「第 1 页必须调 SGP」是有意的契约变更：
    /// LCU 载荷没有 <c>individualPosition</c> / <c>teamPosition</c>，
    /// 只剩盲选局对全员恒为 SUPPORT 的 <c>timeline.role</c>，拿它判分路会把下路 AD 判成辅助。
    /// </summary>
    [Fact]
    public async Task GetMatchHistoryAsync_Page1ComesFromSgp_AndPage3FetchesFromIndex20()
    {
        var lcuTransport = new DelegateLcuTransport(_ => new LcuResponse(404, ""));

        int sgpCallCount = 0;
        int lastSgpStartIndex = -1;
        int lastSgpCount = -1;

        var mockSgp = new MockSgpService
        {
            // 造 id：第 start 场起连续 count 场（101 起），与真实载荷一样"新到旧"
            OnGetMatchHistory = (server, p, start, count) =>
            {
                sgpCallCount++;
                lastSgpStartIndex = start;
                lastSgpCount = count;
                return Enumerable.Range(101 + start, count).Select(id => CreateSummary(id)).ToList();
            }
        };

        var service = new MatchService(
            () => lcuTransport,
            sgpService: mockSgp,
            credentialsProvider: () => new LcuCredentials(50000, "token", 1234, "hn1"));

        // ── 1. 用户请求第 1 页（0~9，共 10 条）──
        MatchHistoryPage page1 = await service.GetMatchHistoryAsync(Puuid, 0, 9);

        Assert.Equal(10, page1.Games.Count);
        Assert.Equal(101, page1.Games[0].GameId);
        Assert.Equal(110, page1.Games[9].GameId);
        Assert.True(page1.HasMore);

        // 首页一次 SGP 预拉 0~19（20 条），第 1 页直接从中切片
        Assert.Equal(1, sgpCallCount);
        Assert.Equal(0, lastSgpStartIndex);
        Assert.Equal(20, lastSgpCount);

        // ── 2. 用户点到第 2 页（10~19）：池子已覆盖，且 SGP 继续从 20 起预拉 ──
        MatchHistoryPage page2 = await service.GetMatchHistoryAsync(Puuid, 10, 19);

        Assert.Equal(10, page2.Games.Count);
        Assert.Equal(111, page2.Games[0].GameId);
        Assert.Equal(120, page2.Games[9].GameId);
        Assert.True(page2.HasMore);

        // 第 2 页时池子已覆盖 10~19，同时触发一次从第 21 条（startIndex = 20）起的预拉
        Assert.Equal(2, sgpCallCount);
        Assert.Equal(20, lastSgpStartIndex);
        Assert.Equal(30, lastSgpCount);

        // ── 3. 用户继续点到第 3 页（20~29）──
        MatchHistoryPage page3 = await service.GetMatchHistoryAsync(Puuid, 20, 29);

        Assert.Equal(10, page3.Games.Count);
        Assert.Equal(121, page3.Games[0].GameId); // 来自第 2 页时 SGP 预拉的 20 起数据
        Assert.Equal(130, page3.Games[9].GameId);
        Assert.True(page3.HasMore);

        // ── 4. 测试缓存清空 ──
        service.InvalidateCache();
        MatchHistoryPage refreshed = await service.GetMatchHistoryAsync(Puuid, 0, 9);
        Assert.Equal(10, refreshed.Games.Count);
        Assert.Equal(101, refreshed.Games[0].GameId);
    }

    /// <summary>生涯页一次性请求 30 场：前 20 由首页 SGP 提供，20 起再补一批。</summary>
    [Fact]
    public async Task GetMatchHistoryAsync_CareerLargeEndIndex_TriggersSgpAndReturnsRequestedCount()
    {
        var lcuTransport = new DelegateLcuTransport(_ => new LcuResponse(404, ""));

        int sgpCallCount = 0;
        int lastSgpStartIndex = -1;
        int lastSgpCount = -1;

        var mockSgp = new MockSgpService
        {
            OnGetMatchHistory = (server, p, start, count) =>
            {
                sgpCallCount++;
                lastSgpStartIndex = start;
                lastSgpCount = count;
                return Enumerable.Range(101 + start, count).Select(id => CreateSummary(id)).ToList();
            }
        };

        var service = new MatchService(
            () => lcuTransport,
            sgpService: mockSgp,
            credentialsProvider: () => new LcuCredentials(50000, "token", 1234, "hn1"));

        // 生涯页面一次性请求 30 场（0~29）
        MatchHistoryPage page = await service.GetMatchHistoryAsync(Puuid, 0, 29);

        Assert.Equal(30, page.Games.Count);
        Assert.Equal(101, page.Games[0].GameId);
        Assert.Equal(120, page.Games[19].GameId);
        Assert.Equal(121, page.Games[20].GameId);
        Assert.Equal(130, page.Games[29].GameId);
        Assert.Equal(2, sgpCallCount);
        Assert.Equal(20, lastSgpStartIndex);
        Assert.Equal(30, lastSgpCount);
    }

    /// <summary>
    /// 非腾讯大区 / SGP 拉不到数据时必须原样退回 LCU（这是首页改用 SGP 后的唯一安全网）。
    /// </summary>
    [Fact]
    public async Task GetMatchHistoryAsync_FallsBackToLcu_WhenSgpReturnsNothing()
    {
        string lcuBody = BuildLcuBody(301, 20);
        var lcuTransport = new DelegateLcuTransport(req =>
            req.Path.StartsWith($"/lol-match-history/v1/products/lol/{Puuid}/matches", StringComparison.Ordinal)
                ? new LcuResponse(200, lcuBody)
                : new LcuResponse(404, ""));

        var mockSgp = new MockSgpService { OnGetMatchHistory = (_, _, _, _) => [] };

        var service = new MatchService(
            () => lcuTransport,
            sgpService: mockSgp,
            credentialsProvider: () => new LcuCredentials(50000, "token", 1234, "hn1"));

        MatchHistoryPage page = await service.GetMatchHistoryAsync(Puuid, 0, 9);

        Assert.Equal(10, page.Games.Count);
        Assert.Equal(301, page.Games[0].GameId);
        Assert.Equal(310, page.Games[9].GameId);
    }

    /// <summary>SGP 超时（等待被本方法的 5 秒预算取消）同样必须退回 LCU，而不是把取消抛给界面。</summary>
    [Fact]
    public async Task GetMatchHistoryAsync_FallsBackToLcu_WhenSgpCancelledByTimeout()
    {
        string lcuBody = BuildLcuBody(401, 20);
        var lcuTransport = new DelegateLcuTransport(req =>
            req.Path.StartsWith($"/lol-match-history/v1/products/lol/{Puuid}/matches", StringComparison.Ordinal)
                ? new LcuResponse(200, lcuBody)
                : new LcuResponse(404, ""));

        var mockSgp = new MockSgpService { SimulateTimeout = true };

        var service = new MatchService(
            () => lcuTransport,
            sgpService: mockSgp,
            credentialsProvider: () => new LcuCredentials(50000, "token", 1234, "hn1"));

        MatchHistoryPage page = await service.GetMatchHistoryAsync(Puuid, 0, 9);

        Assert.Equal(10, page.Games.Count);
        Assert.Equal(401, page.Games[0].GameId);
    }

    private static string BuildLcuBody(int firstGameId, int count)
    {
        string gamesJson = string.Join(",", Enumerable.Range(firstGameId, count).Select(id =>
            $$"""
            {
                "gameId": {{id}},
                "queueId": 420,
                "gameCreation": {{1700000000000 + id}},
                "gameDuration": 1800,
                "participants": [
                    {
                        "participantId": 1,
                        "championId": 1,
                        "stats": { "win": true, "kills": 1, "deaths": 0, "assists": 0, "item0": 0, "item1": 0, "item2": 0, "item3": 0, "item4": 0, "item5": 0, "item6": 0 }
                    }
                ]
            }
            """));
        return $"{{\"games\":{{\"gameCount\":100,\"games\":[{gamesJson}]}}}}";
    }

    private static MatchSummary CreateSummary(long gameId) => new()
    {
        GameId = gameId,
        QueueId = 420,
        QueueName = "单双排位",
        MapName = "召唤师峡谷",
        GameCreation = 1700000000000 + gameId,
        DurationSeconds = 1800,
        Win = true,
        Remake = false,
        ChampionId = 1,
        ChampionLevel = 18,
        Spell1Id = 4,
        Spell2Id = 14,
        Kills = 5,
        Deaths = 1,
        Assists = 10,
        ItemIds = [0, 0, 0, 0, 0, 0, 0],
        RuneId = 8112,
        CreepScore = 200,
        Gold = 12000,
        DamageToChampions = 25000,
        TotalHeal = 3000,
        AugmentIds = [],
    };

    private sealed class MockSgpService : ISgpService
    {
        public Func<string?, string, int, int, IReadOnlyList<MatchSummary>>? OnGetMatchHistory { get; set; }

        /// <summary>模拟超时：直接抛取消异常（真实实现里 HttpClient 超时抛的就是这个）。</summary>
        public bool SimulateTimeout { get; set; }

        public bool IsSupported(string? server) => true;

        public Task<string?> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("mock-token");

        public Task<YuumiX.Core.Features.Tools.Models.SpectateCredentials?> GetSpectateCredentialsAsync(
            string? server,
            string puuid,
            CancellationToken cancellationToken = default) => Task.FromResult<YuumiX.Core.Features.Tools.Models.SpectateCredentials?>(null);

        public Task<IReadOnlyList<MatchSummary>> GetMatchHistoryAsync(
            string? server,
            string puuid,
            int startIndex,
            int count,
            CancellationToken cancellationToken = default)
        {
            if (SimulateTimeout)
            {
                throw new OperationCanceledException("模拟 SGP 超时");
            }

            var result = OnGetMatchHistory?.Invoke(server, puuid, startIndex, count) ?? [];
            return Task.FromResult(result);
        }
    }

    private sealed class DelegateLcuTransport : ILcuTransport
    {
        private readonly Func<LcuRequest, LcuResponse> _handler;

        public DelegateLcuTransport(Func<LcuRequest, LcuResponse> handler) => _handler = handler;

        public Task<LcuResponse> SendAsync(LcuRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_handler(request));

        public void Dispose() { }
    }
}
