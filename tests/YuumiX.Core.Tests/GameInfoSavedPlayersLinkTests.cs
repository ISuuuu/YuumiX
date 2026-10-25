using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.SavedPlayers;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;
using YuumiX.Testing.Lcu;
using Xunit;

namespace YuumiX.Core.Tests;

/// <summary>
/// 路人集联动批次验收（需求文档 §4.8 / §11.0-3）：
/// 映射查询 in-flight 去重、宿命候选并入相遇记录、状态发布含映射、SavedPlayersStore 映射聚合。
/// </summary>
public sealed class GameInfoSavedPlayersLinkTests
{
    private sealed record GameDef(long GameId, int QueueId, bool win, long creation);

    /// <summary>测试环境：FakeLcuServer + 临时 SQLite 路人集存储 + GameInfoService。</summary>
    private sealed class SavedPlayersLinkEnv : IAsyncDisposable
    {
        public FakeLcuServer Server { get; } = FakeLcuServer.Start();

        public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"yuumix_test_{Guid.NewGuid():N}.db");

        public SavedPlayersStore Store { get; }

        /// <summary>puuid → 召唤师档案 JSON。</summary>
        public Dictionary<string, long> Profiles { get; } = new();

        /// <summary>puuid → 全量战绩列表（索引 0 最新）。</summary>
        public Dictionary<string, List<GameDef>> MatchGames { get; } = new();

        /// <summary>gameId → 对局详情 JSON（宿命检测端点）。</summary>
        public Dictionary<long, string> GameDetails { get; } = new();

        /// <summary>已请求过的对局详情 gameId（验证候选裁剪）。</summary>
        public List<long> RequestedGameDetails { get; } = [];

        public GameInfoService Service { get; }

        public SavedPlayersLinkEnv()
        {
            Store = new SavedPlayersStore(DbPath);

            var transport = new LcuHttpTransport(
                Server.BaseAddress,
                Server.AuthorizationHeader,
                maxConcurrency: 4,
                requestTimeout: TimeSpan.FromSeconds(10),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LcuHttpTransport>.Instance);

            Service = new GameInfoService(
                () => transport,
                new SummonerService(() => transport),
                new MatchService(() => transport),
                new CareerService(() => transport),
                new MasteryService(() => transport),
                new FateService(() => transport),
                new AppConfig(),
                savedPlayersStore: Store);

            Server.Handler = Handle;
        }

        private FakeResponse Handle(RecordedRequest req)
        {
            string path = req.Path;

            if (path.EndsWith("current-summoner", StringComparison.Ordinal))
            {
                return FakeResponse.Ok(ProfileJson("p-me", 100, 300));
            }

            if (path.EndsWith("gameflow-phase", StringComparison.Ordinal))
            {
                return FakeResponse.Ok("\"None\"");
            }

            if (path.EndsWith("/lol-gameflow/v1/session", StringComparison.Ordinal))
            {
                return FakeResponse.NotFound();
            }

            Match detailMatch = Regex.Match(path, @"/lol-match-history/v1/games/(\d+)$");
            if (detailMatch.Success)
            {
                long gid = long.Parse(detailMatch.Groups[1].Value);
                lock (RequestedGameDetails)
                {
                    RequestedGameDetails.Add(gid);
                }

                return GameDetails.TryGetValue(gid, out var json) ? FakeResponse.Ok(json) : FakeResponse.NotFound();
            }

            Match historyMatch = Regex.Match(path, @"/lol-match-history/v1/products/lol/([^/]+)/matches\?begIndex=(\d+)&endIndex=(\d+)");
            if (historyMatch.Success)
            {
                string puuid = historyMatch.Groups[1].Value;
                int beg = int.Parse(historyMatch.Groups[2].Value);
                int end = int.Parse(historyMatch.Groups[3].Value);
                if (MatchGames.TryGetValue(puuid, out var games))
                {
                    var slice = games.Skip(beg).Take(end - beg + 1).ToList();
                    return FakeResponse.Ok(HistoryJson(slice, games.Count));
                }

                return FakeResponse.NotFound();
            }

            Match profileMatch = Regex.Match(path, @"/lol-summoner/v2/summoners/puuid/([^/?]+)");
            if (profileMatch.Success)
            {
                string puuid = profileMatch.Groups[1].Value;
                if (Profiles.TryGetValue(puuid, out long sid))
                {
                    return FakeResponse.Ok(ProfileJson(puuid, sid, 300));
                }

                return FakeResponse.NotFound();
            }

            return FakeResponse.NotFound();
        }

        /// <summary>合成路人集数据：保存玩家 + 相遇对局记录。</summary>
        public async Task SeedSavedPlayerAsync(
            string puuid,
            string? tag,
            long lastMetAt,
            params (long GameId, long UpdateAt)[] encounteredGames)
        {
            // 触发懒加载建表（EnsureInitialized），raw SQL 才能插入
            await Store.QueryPlayersAsync(pageSize: 1);

            await using var conn = new SqliteConnection($"Data Source={DbPath}");
            await conn.OpenAsync();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT INTO saved_players (puuid, self_puuid, region, rso_platform_id, tag, summoner_name, profile_icon_id, tag_line, champion_id, update_at, last_met_at)
                    VALUES (@puuid, 'p-me', '', '', @tag, 'Saved-' || @puuid, 0, '1', 0, @now, @lastMetAt);";
                cmd.Parameters.AddWithValue("@puuid", puuid);
                cmd.Parameters.AddWithValue("@tag", (object?)tag ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@now", lastMetAt);
                cmd.Parameters.AddWithValue("@lastMetAt", lastMetAt);
                await cmd.ExecuteNonQueryAsync();
            }

            foreach (var (gameId, updateAt) in encounteredGames)
            {
                await using var cmd = conn.CreateCommand();
                // OR IGNORE：同一 (game_id, puuid, self_puuid) 由唯一索引拦截，
                // 造数据时重复写入应被丢弃（对应断线重连导致的二次记录）
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO encountered_games (game_id, puuid, self_puuid, region, rso_platform_id, queue_type, update_at)
                    VALUES (@gameId, @puuid, 'p-me', '', '', 'RANKED_SOLO_5x5', @updateAt);";
                cmd.Parameters.AddWithValue("@gameId", gameId);
                cmd.Parameters.AddWithValue("@puuid", puuid);
                cmd.Parameters.AddWithValue("@updateAt", updateAt);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            Store.Dispose();
            await Server.DisposeAsync();
            try
            {
                if (File.Exists(DbPath))
                {
                    File.Delete(DbPath);
                }
            }
            catch
            {
                // 忽略文件占用
            }
        }
    }

    private static string ProfileJson(string puuid, long sid, int level) =>
        $$"""
        {"accountId":1,"summonerId":{{sid}},"puuid":"{{puuid}}","displayName":"N-{{sid}}","gameName":"N-{{sid}}","tagLine":"1","summonerLevel":{{level}},"profileIconId":1,"percentCompleteForNextLevel":0,"xpSinceLastLevel":0,"xpUntilNextLevel":0}
        """;

    private static string GameJson(GameDef g) =>
        $$$"""
        {"gameId":{{{g.GameId}}},"gameCreation":{{{g.creation}}},"gameDuration":1500,"queueId":{{{g.QueueId}}},"participants":[{"championId":1,"spell1Id":4,"spell2Id":14,"stats":{"win":{{{(g.win ? "true" : "false")}}},"kills":3,"deaths":2,"assists":5,"champLevel":15,"item0":0,"item1":0,"item2":0,"item3":0,"item4":0,"item5":0,"item6":0,"perk0":0}}]}
        """;

    private static string HistoryJson(List<GameDef> games, int totalCount) =>
        "{\"games\":{\"gameCount\":" + totalCount + ",\"games\":[" +
        string.Join(",", games.Select(GameJson)) + "]}}";

    /// <summary>对局详情 JSON：p-me 与目标玩家同队/敌队（宿命检测端点）。</summary>
    private static string FateGameJson(long gameId, long gameCreation, bool sameTeam, long targetSid = 200) =>
        $$$"""
        {"gameId":{{{gameId}}},"queueId":420,"gameCreation":{{{(gameCreation > 0 ? gameCreation.ToString() : "0")}}},"participantIdentities":[{"participantId":1,"player":{"puuid":"p-me","summonerId":100}},{"participantId":2,"player":{"puuid":"p-x","summonerId":{{{targetSid}}}}}],"participants":[{"participantId":1,"teamId":100,"championId":1},{"participantId":2,"teamId":{{{(sameTeam ? 100 : 200)}}},"championId":64}]}
        """;

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("等待条件超时");
    }

    // ── 任务1：映射查询 in-flight 去重（同 puuid 并发一次） ─────────────────

    [Fact]
    public async Task QuerySavedPlayersMap_ConcurrentSamePuuid_SharedSingleQuery()
    {
        var service = new GameInfoService(
            () => null,
            new SummonerService(() => null),
            new MatchService(() => null),
            new CareerService(() => null),
            new MasteryService(() => null),
            new FateService(() => null),
            new AppConfig());

        int queryCount = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SavedPlayersMapQueryOverride = async (puuid, _) =>
        {
            Interlocked.Increment(ref queryCount);
            await gate.Task;
            return new Dictionary<string, SavedPlayerMarker>
            {
                ["k-" + puuid] = new() { EncounterCount = 1 },
            };
        };

        Task<IReadOnlyDictionary<string, SavedPlayerMarker>> first = service.QuerySavedPlayersMapAsync("p-a");
        Task<IReadOnlyDictionary<string, SavedPlayerMarker>> second = service.QuerySavedPlayersMapAsync("p-a");

        // 查询被 gate 挂起期间，第二次同 puuid 调用共享同一次 in-flight 查询
        Assert.False(first.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref queryCount));

        gate.SetResult(true);
        var map1 = await first;
        var map2 = await second;

        Assert.Equal(1, Volatile.Read(ref queryCount));
        Assert.Contains("k-p-a", map1.Keys);
        Assert.Equal(map1["k-p-a"], map2["k-p-a"]);

        // in-flight 完成后清除：同 puuid 再查会发起新查询；不同 puuid 也各自发起
        await service.QuerySavedPlayersMapAsync("p-a");
        Assert.Equal(2, Volatile.Read(ref queryCount));
        await service.QuerySavedPlayersMapAsync("p-b");
        Assert.Equal(3, Volatile.Read(ref queryCount));

        service.Dispose();
    }

    [Fact]
    public async Task QuerySavedPlayersMap_DifferentPuuidWhileInFlight_ReplacesInFlight()
    {
        var service = new GameInfoService(
            () => null,
            new SummonerService(() => null),
            new MatchService(() => null),
            new CareerService(() => null),
            new MasteryService(() => null),
            new FateService(() => null),
            new AppConfig());

        var gates = new Dictionary<string, TaskCompletionSource<bool>>(StringComparer.Ordinal);
        service.SavedPlayersMapQueryOverride = async (puuid, _) =>
        {
            TaskCompletionSource<bool> gate;
            lock (gates)
            {
                if (!gates.TryGetValue(puuid, out var existing))
                {
                    existing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    gates[puuid] = existing;
                }

                gate = existing;
            }

            await gate.Task;
            return new Dictionary<string, SavedPlayerMarker>();
        };

        Task<IReadOnlyDictionary<string, SavedPlayerMarker>> first = service.QuerySavedPlayersMapAsync("p-a");
        Task<IReadOnlyDictionary<string, SavedPlayerMarker>> second = service.QuerySavedPlayersMapAsync("p-b");

        lock (gates)
        {
            gates["p-a"].TrySetResult(true);
            gates["p-b"].TrySetResult(true);
        }

        await Task.WhenAll(first, second);

        service.Dispose();
    }

    // ── 任务2：SavedPlayersStore 映射聚合 ─────────────────────────────────

    [Fact]
    public async Task SavedPlayersStore_GetSavedPlayersMap_AggregatesMarkers()
    {
        await using var env = new SavedPlayersLinkEnv();
        await env.SeedSavedPlayerAsync(
            "p-x",
            tag: "演员",
            lastMetAt: 1_700_000_700_000,
            (777, 2000),
            (778, 1000),
            (777, 3000)); // 重复 game_id 应去重
        await env.SeedSavedPlayerAsync("p-other", tag: null, lastMetAt: 1_600_000_000_000, (888, 900));
        // 其他 self_puuid 的数据不进映射
        await using (var conn = new SqliteConnection($"Data Source={env.DbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO saved_players (puuid, self_puuid, region, rso_platform_id, tag, summoner_name, profile_icon_id, tag_line, champion_id, update_at, last_met_at)
                VALUES ('p-stranger', 'p-not-me', '', '', NULL, 'S', 0, '1', 0, 1, 1);";
            await cmd.ExecuteNonQueryAsync();
        }

        var map = await env.Store.GetSavedPlayersMapAsync("p-me");

        Assert.Equal(2, map.Count);
        Assert.False(map.ContainsKey("p-stranger"));

        var marker = map["p-x"];
        Assert.Equal("演员", marker.Tag);
        Assert.Equal(2, marker.EncounterCount); // 重复的 777 未落库
        Assert.Equal(777, marker.LastEncounteredGameId); // update_at 最大的一局
        Assert.Equal(1_700_000_700_000, marker.LastMetAt);
        Assert.Equal(2, marker.EncounteredGameIds.Count);
        Assert.Contains(777L, marker.EncounteredGameIds);
        Assert.Contains(778L, marker.EncounteredGameIds);

        var other = map["p-other"];
        Assert.Null(other.Tag);
        Assert.Equal(1, other.EncounterCount);
    }

    // ── 任务3：宿命候选并入相遇记录 + 状态发布含映射 ───────────────────────

    [Fact]
    public async Task FateDetection_MergesEncounteredGameIntoCandidates()
    {
        await using var env = new SavedPlayersLinkEnv();
        await env.SeedSavedPlayerAsync("p-x", tag: null, lastMetAt: 1_700_000_700_000, (777, 2000));

        env.Profiles["p-x"] = 200;
        // 7 局战绩：前 5 局（200_001..200_005）无详情；999 在第 7 位（超出近 5 局候选，虽配了详情也不应拉取）
        env.MatchGames["p-x"] =
        [
            new GameDef(200_001, 420, win: true, creation: 1_700_000_100_000),
            new GameDef(200_002, 420, win: true, creation: 1_700_000_090_000),
            new GameDef(200_003, 420, win: false, creation: 1_700_000_080_000),
            new GameDef(200_004, 420, win: true, creation: 1_700_000_070_000),
            new GameDef(200_005, 420, win: true, creation: 1_700_000_060_000),
            new GameDef(200_006, 420, win: true, creation: 1_700_000_050_000),
            new GameDef(999, 420, win: true, creation: 1_700_000_040_000),
        ];

        env.GameDetails[777] = FateGameJson(777, gameCreation: 1_700_000_777_000, sameTeam: true);
        env.GameDetails[999] = FateGameJson(999, gameCreation: 1_700_000_999_000, sameTeam: true);

        // 当前召唤师就绪 + 路人集映射发布到状态流
        await env.Service.RefreshStateAsync();
        await WaitUntilAsync(() => env.Service.CurrentState.SavedPlayerMap.ContainsKey("p-x"));
        var publishedMarker = env.Service.CurrentState.SavedPlayerMap["p-x"];
        Assert.Equal(777, publishedMarker.LastEncounteredGameId);
        Assert.Equal(1_700_000_700_000, publishedMarker.LastMetAt);

        var player = await env.Service.LoadPlayerDataAsync(3, 200, "p-x", new PremadePlayerLike { CellId = 3, Puuid = "p-x", SummonerId = 200 });
        Assert.NotNull(player);

        // 宿命检测异步补填：等待写入状态
        await WaitUntilAsync(() =>
            env.Service.CurrentState.PlayerData.TryGetValue("p-x", out var entry) &&
            !string.IsNullOrEmpty(entry.FateFlag));

        var fateEntry = env.Service.CurrentState.PlayerData["p-x"];
        Assert.Equal("ally", fateEntry.FateFlag);
        Assert.Equal(1, fateEntry.FateAllyCount);
        Assert.Equal(0, fateEntry.FateEnemyCount);
        Assert.Equal(1_700_000_777_000, fateEntry.FateGameCreation);
        // 命中局（777）不是该玩家战绩第 1 局 → 「曾」而非「友/敌」
        Assert.False(fateEntry.FateIsLastGame);

        // 相遇记录并入候选（777 被拉取）；近 5 局之外的对局（999）不进候选
        lock (env.RequestedGameDetails)
        {
            Assert.Contains(777L, env.RequestedGameDetails);
            Assert.DoesNotContain(999L, env.RequestedGameDetails);
        }
    }

    [Fact]
    public async Task FateDetection_MissingGameCreation_FallsBackToLastMetAt()
    {
        await using var env = new SavedPlayersLinkEnv();
        const long lastMetAt = 1_700_000_123_456;
        await env.SeedSavedPlayerAsync("p-x", tag: "代练", lastMetAt: lastMetAt, (777, 2000));

        env.Profiles["p-x"] = 200;
        env.MatchGames["p-x"] =
        [
            new GameDef(200_001, 420, win: true, creation: 1_700_000_100_000),
        ];

        // 详情无 gameCreation（0）且 777 不在战绩列表中 → 时间戳回退到路人集 lastMetAt
        env.GameDetails[777] = FateGameJson(777, gameCreation: 0, sameTeam: false);

        await env.Service.RefreshStateAsync();
        await WaitUntilAsync(() => env.Service.CurrentState.SavedPlayerMap.ContainsKey("p-x"));

        await env.Service.LoadPlayerDataAsync(3, 200, "p-x", new PremadePlayerLike { CellId = 3, Puuid = "p-x", SummonerId = 200 });

        await WaitUntilAsync(() =>
            env.Service.CurrentState.PlayerData.TryGetValue("p-x", out var entry) &&
            !string.IsNullOrEmpty(entry.FateFlag));

        var fateEntry = env.Service.CurrentState.PlayerData["p-x"];
        Assert.Equal("enemy", fateEntry.FateFlag);
        Assert.Equal(lastMetAt, fateEntry.FateGameCreation);
    }
}
