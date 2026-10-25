using Xunit;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Lcu.Sgp;

namespace YuumiX.Core.Tests;

public sealed class MatchServicePagingSgpTests
{
    private const string Puuid = "22222222-3333-4444-5555-666666666666";

    [Fact]
    public async Task GetMatchHistoryAsync_Page1ThenPage2_TriggersSgpStartingFromIndex20()
    {
        // 构造 20 局 LCU 数据（id 从 101 到 120）
        var lcuGamesJson = string.Join(",", Enumerable.Range(101, 20).Select(id =>
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

        var lcuTransport = new DelegateLcuTransport(req =>
        {
            if (req.Path.StartsWith($"/lol-match-history/v1/products/lol/{Puuid}/matches", StringComparison.Ordinal))
            {
                return new LcuResponse(200, $"{{\"games\":{{\"gameCount\":100,\"games\":[{lcuGamesJson}]}}}}");
            }
            return new LcuResponse(404, "");
        });

        // 构造 Mock SGP 服务，记录调用参数并返回第 21 到 40 局（id: 201 到 220）
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
                return Enumerable.Range(201, count).Select(id => CreateSummary(id)).ToList();
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
        Assert.Equal(0, sgpCallCount); // 第 1 页未触发 SGP（仅使用 LCU 预拉取的 20 条）

        // ── 2. 用户点到第 2 页（10~19，共 10 条）──
        MatchHistoryPage page2 = await service.GetMatchHistoryAsync(Puuid, 10, 19);

        Assert.Equal(10, page2.Games.Count);
        Assert.Equal(111, page2.Games[0].GameId);
        Assert.Equal(120, page2.Games[9].GameId);
        Assert.True(page2.HasMore);

        // 核心断言：点到第 2 页时，自动调用 SGP 接口从第 21 条（startIndex = 20）拉取！
        Assert.Equal(1, sgpCallCount);
        Assert.Equal(20, lastSgpStartIndex);
        Assert.Equal(30, lastSgpCount);

        // ── 3. 用户继续点到第 3 页（20~29，共 10 条）──
        MatchHistoryPage page3 = await service.GetMatchHistoryAsync(Puuid, 20, 29);

        Assert.Equal(10, page3.Games.Count);
        Assert.Equal(201, page3.Games[0].GameId); // 来自 SGP 拉取的数据
        Assert.Equal(210, page3.Games[9].GameId);
        Assert.True(page3.HasMore);

        // ── 4. 测试缓存清空 ──
        service.InvalidateCache();
        // 再次查第 1 页重新拉取
        MatchHistoryPage refreshed = await service.GetMatchHistoryAsync(Puuid, 0, 9);
        Assert.Equal(10, refreshed.Games.Count);
    }

    [Fact]
    public async Task GetMatchHistoryAsync_CareerLargeEndIndex_TriggersSgpAndReturnsRequestedCount()
    {
        // 构造 20 局 LCU 数据（id 从 101 到 120）
        var lcuGamesJson = string.Join(",", Enumerable.Range(101, 20).Select(id =>
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

        var lcuTransport = new DelegateLcuTransport(req =>
        {
            if (req.Path.StartsWith($"/lol-match-history/v1/products/lol/{Puuid}/matches", StringComparison.Ordinal))
            {
                return new LcuResponse(200, $"{{\"games\":{{\"gameCount\":100,\"games\":[{lcuGamesJson}]}}}}");
            }
            return new LcuResponse(404, "");
        });

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
                return Enumerable.Range(201, count).Select(id => CreateSummary(id)).ToList();
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
        Assert.Equal(201, page.Games[20].GameId);
        Assert.Equal(210, page.Games[29].GameId);
        Assert.Equal(1, sgpCallCount);
        Assert.Equal(20, lastSgpStartIndex);
        Assert.Equal(30, lastSgpCount);
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
