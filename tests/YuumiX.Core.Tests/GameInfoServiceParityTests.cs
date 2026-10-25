using System.Text.Json;
using System.Text.RegularExpressions;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Lcu;
using YuumiX.Core.Lcu.Sgp;
using YuumiX.Core.Persistence;
using YuumiX.Testing.Lcu;
using Xunit;

namespace YuumiX.Core.Tests;

/// <summary>
/// 对局信息正确性对齐批次验收（需求文档 §11.0-8 / §11.0-9 / §11.0-2 / §11.0-6）：
/// 战绩隐藏口径、保留快照完整规则、当前玩家战绩同步与链式轮询、
/// 11~50 局后台扩充 + SGP 并集、GameInfoFilter 三处生效、IsHumanoid 人机与自定义会话。
/// </summary>
public sealed class GameInfoServiceParityTests
{
    private const long GameId = 555;

    // ── 测试环境 ─────────────────────────────────────────────────────────

    private sealed record GameDef(long GameId, int QueueId, bool win, long creation, int ChampionId = 1);

    private sealed class FakeSgpService : ISgpService
    {
        public readonly List<(string Puuid, int Start, int Count)> Calls = [];

        public Dictionary<string, List<MatchSummary>> Results { get; } = new(StringComparer.Ordinal);

        public bool Supported { get; set; } = true;

        public bool IsSupported(string? server) => Supported;

        public Task<string?> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("fake-token");

        public Task<YuumiX.Core.Features.Tools.Models.SpectateCredentials?> GetSpectateCredentialsAsync(
            string? server, string puuid, CancellationToken cancellationToken = default)
            => Task.FromResult<YuumiX.Core.Features.Tools.Models.SpectateCredentials?>(null);

        public Task<IReadOnlyList<MatchSummary>> GetMatchHistoryAsync(
            string? server, string puuid, int startIndex, int count, CancellationToken cancellationToken = default)
        {
            List<MatchSummary> result;
            lock (Calls)
            {
                Calls.Add((puuid, startIndex, count));
                result = Results.TryGetValue(puuid, out var list) ? [.. list] : [];
            }

            return Task.FromResult<IReadOnlyList<MatchSummary>>(result);
        }
    }

    private sealed class GameInfoTestEnv : IAsyncDisposable
    {
        public FakeLcuServer Server { get; } = FakeLcuServer.Start();

        public FakeSgpService Sgp { get; } = new();

        public string Phase { get; set; } = "GameStart";

        public string? GameflowSession { get; set; }

        public string? ChampSelectSession { get; set; }

        /// <summary>puuid → 召唤师档案 JSON（v2/summoners/puuid 命中即返回）。</summary>
        public Dictionary<string, (long Sid, int Level)> Profiles { get; } = new();

        /// <summary>puuid → 全量战绩列表（索引 0 最新；按窗口切片下发，模拟真实 LCU）。</summary>
        public Dictionary<string, List<GameDef>> MatchGames { get; } = new();

        public GameInfoService Service { get; }

        public GameInfoTestEnv(AppConfig config)
        {
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
                config,
                sgpService: Sgp);

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
                return FakeResponse.Ok($"\"{Phase}\"");
            }

            if (path.EndsWith("/lol-gameflow/v1/session", StringComparison.Ordinal))
            {
                return GameflowSession is null ? FakeResponse.NotFound() : FakeResponse.Ok(GameflowSession);
            }

            if (path.EndsWith("/lol-champ-select/v1/session", StringComparison.Ordinal))
            {
                return ChampSelectSession is null ? FakeResponse.NotFound() : FakeResponse.Ok(ChampSelectSession);
            }

            // 战绩列表（含分页窗口）：按窗口切片返回该 puuid 的全量列表
            Match match = Regex.Match(path, @"/lol-match-history/v1/products/lol/([^/]+)/matches\?begIndex=(\d+)&endIndex=(\d+)");
            if (match.Success)
            {
                string puuid = match.Groups[1].Value;
                int beg = int.Parse(match.Groups[2].Value);
                int end = int.Parse(match.Groups[3].Value);
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
                if (Profiles.TryGetValue(puuid, out var profile))
                {
                    return FakeResponse.Ok(ProfileJson(puuid, profile.Sid, profile.Level));
                }

                return FakeResponse.NotFound();
            }

            return FakeResponse.NotFound();
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await Server.DisposeAsync();
        }
    }

    private static string ProfileJson(string puuid, long sid, int level) =>
        $$"""
        {"accountId":1,"summonerId":{{sid}},"puuid":"{{puuid}}","displayName":"N-{{sid}}","gameName":"N-{{sid}}","tagLine":"1","summonerLevel":{{level}},"profileIconId":1,"percentCompleteForNextLevel":0,"xpSinceLastLevel":0,"xpUntilNextLevel":0}
        """;

    private static string GameJson(GameDef g) =>
        $$$"""
        {"gameId":{{{g.GameId}}},"gameCreation":{{{g.creation}}},"gameDuration":1500,"queueId":{{{g.QueueId}}},"participants":[{"championId":{{{g.ChampionId}}},"spell1Id":4,"spell2Id":14,"stats":{"win":{{{(g.win ? "true" : "false")}}},"kills":3,"deaths":2,"assists":5,"champLevel":15,"item0":0,"item1":0,"item2":0,"item3":0,"item4":0,"item5":0,"item6":0,"perk0":0}}]}
        """;

    private static string HistoryJson(List<GameDef> games, int totalCount) =>
        "{\"games\":{\"gameCount\":" + totalCount + ",\"games\":[" +
        string.Join(",", games.Select(GameJson)) + "]}}";

    private static string ParticipantJson(string puuid, long sid, int cellId, int championId = 0) =>
        $$$"""{"summonerId":{{{sid}}},"puuid":"{{{puuid}}}","gameName":"N-{{{sid}}}","tagLine":"1","profileIconId":1,"championId":{{{championId}}},"cellId":{{{cellId}}}}""";

    /// <summary>标准 5v5 对局会话：我方 p-me + p-a..p-d（蓝方），敌方 p-e..p-i。</summary>
    private static string FullSessionJson(long gameId)
    {
        string me = ParticipantJson("p-me", 100, 0, 64);
        string[] allies = ["p-a", "p-b", "p-c", "p-d"];
        string[] enemies = ["p-e", "p-f", "p-g", "p-h", "p-i"];
        string teamOne = "[" + string.Join(",", new[] { me }.Concat(allies.Select((p, i) => ParticipantJson(p, 101 + i, i + 1)))) + "]";
        string teamTwo = "[" + string.Join(",", enemies.Select((p, i) => ParticipantJson(p, 201 + i, i))) + "]";
        return $$$"""{"gameData":{"gameId":{{{gameId}}},"queue":{"id":420,"gameMode":"CLASSIC","isCustom":false},"teamOne":{{{teamOne}}},"teamTwo":{{{teamTwo}}}}}""";
    }

    /// <summary>配置全部 10 名玩家的档案与战绩（各 count 局，胜负交替可选）。</summary>
    private static void SetupFullGame(GameInfoTestEnv env, int gamesPerPlayer, Func<int, bool>? winPolicy = null)
    {
        winPolicy ??= (i => i % 10 < 6);
        long[] sids = [100, 101, 102, 103, 104, 201, 202, 203, 204, 205];
        string[] puuids = ["p-me", "p-a", "p-b", "p-c", "p-d", "p-e", "p-f", "p-g", "p-h", "p-i"];
        for (int i = 0; i < puuids.Length; i++)
        {
            env.Profiles[puuids[i]] = (sids[i], 300);
            env.MatchGames[puuids[i]] = Enumerable.Range(0, gamesPerPlayer)
                .Select(i => new GameDef(100_000 + i, 420, winPolicy(i), 1_700_000_000_000 - i * 60_000))
                .ToList();
        }
    }

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

    // ── 任务1：战绩隐藏判定（战绩返回空即隐藏，与等级无关） ────────────────

    [Theory]
    [InlineData(10)]   // 新号等级档
    [InlineData(300)]  // 高等级档
    public async Task LoadPlayerDataAsync_EmptyMatchHistory_MarksHiddenRegardlessOfLevel(int level)
    {
        var config = new AppConfig();
        await using var env = new GameInfoTestEnv(config);
        env.Profiles["p-x"] = (900, level);
        env.MatchGames["p-x"] = [];

        var player = await env.Service.LoadPlayerDataAsync(3, 900, "p-x", new PremadePlayerLike { CellId = 3, Puuid = "p-x", SummonerId = 900 });

        Assert.NotNull(player);
        Assert.True(player.MatchHistoryHidden);
        Assert.Empty(player.Matches);
    }

    [Fact]
    public async Task LoadPlayerDataAsync_NonEmptyMatchHistory_NotHidden()
    {
        var config = new AppConfig();
        await using var env = new GameInfoTestEnv(config);
        env.Profiles["p-x"] = (900, 10);
        env.MatchGames["p-x"] = [new GameDef(9001, 420, win: true, creation: 1_700_000_000_000)];

        var player = await env.Service.LoadPlayerDataAsync(3, 900, "p-x", new PremadePlayerLike { CellId = 3, Puuid = "p-x", SummonerId = 900 });

        Assert.NotNull(player);
        Assert.False(player.MatchHistoryHidden);
        Assert.Single(player.Matches);
        Assert.Equal(1, player.WinCount);
    }

    // ── 任务2：保留快照完整规则 ──────────────────────────────────────────

    [Fact]
    public async Task ReservedSnapshot_ExcludesLoadingPlaceholders()
    {
        var config = new AppConfig();
        config.Functions.EnableReserveGameinfo = true;
        await using var env = new GameInfoTestEnv(config);
        env.GameflowSession = FullSessionJson(GameId);

        // 只有我方 5 人可加载，敌方 5 人档案 404 → 保持 loading 占位
        foreach (string puuid in new[] { "p-me", "p-a", "p-b", "p-c", "p-d" })
        {
            long sid = puuid == "p-me" ? 100 : 101 + puuid[2] - 'a';
            env.Profiles[puuid] = (sid, 300);
            env.MatchGames[puuid] = [new GameDef(9001, 420, win: true, creation: 1_700_000_000_000)];
        }

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();

        Assert.True(env.Service.HasReservedSnapshot);
        Assert.Equal(10, env.Service.ReservedSnapshotTeamCount);
        Assert.Equal(5, env.Service.ReservedSnapshotLoadedCount);
        var snapshotData = env.Service.ReservedSnapshotPlayerData;
        Assert.NotNull(snapshotData);
        Assert.All(snapshotData!.Values, entry => Assert.False(entry.Loading));
    }

    [Fact]
    public async Task ReservedSnapshot_InProgressReducedSession_RestoresFullTeamsAndKeepsSnapshot()
    {
        var config = new AppConfig();
        await using var env = new GameInfoTestEnv(config);
        env.GameflowSession = FullSessionJson(GameId);
        SetupFullGame(env, gamesPerPlayer: 1);

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();
        Assert.Equal(5, env.Service.CurrentState.MyTeam.Count);
        Assert.Equal(5, env.Service.CurrentState.TheirTeam.Count);

        // InProgress 精简 session：同 gameId 但只剩自己
        env.Phase = "InProgress";
        string reduced = $$$"""{"gameData":{"gameId":{{{GameId}}},"queue":{"id":420,"gameMode":"CLASSIC"},"teamOne":[{{{ParticipantJson("p-me", 100, 0, 64)}}}],"teamTwo":[]}}""";
        env.GameflowSession = reduced;

        await env.Service.RefreshStateAsync();

        // 快照未被精简会话毒化；完整队伍从快照恢复
        Assert.True(env.Service.HasReservedSnapshot);
        Assert.Equal(10, env.Service.ReservedSnapshotTeamCount);
        Assert.Equal(5, env.Service.CurrentState.MyTeam.Count);
        Assert.Equal(5, env.Service.CurrentState.TheirTeam.Count);
    }

    [Fact]
    public async Task ReservedSnapshot_NewGameIdWithoutChampSelect_ClearsEverything()
    {
        var config = new AppConfig();
        config.Functions.EnableReserveGameinfo = true;
        await using var env = new GameInfoTestEnv(config);
        env.GameflowSession = FullSessionJson(GameId);
        SetupFullGame(env, gamesPerPlayer: 1);

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();
        Assert.True(env.Service.HasReservedSnapshot);

        // 全新 gameId 且未经历本局选人：彻底清空（含快照与上一局玩家数据）
        string teamOne = "[" + string.Join(",", new[] { ParticipantJson("p-me", 100, 0, 64), ParticipantJson("p-z", 190, 1, 22) }) + "]";
        env.GameflowSession = $$$"""{"gameData":{"gameId":999,"queue":{"id":420,"gameMode":"CLASSIC"},"teamOne":{{{teamOne}}},"teamTwo":[]}}""";
        env.Profiles["p-z"] = (190, 300);
        env.MatchGames["p-z"] = [new GameDef(9002, 420, win: true, creation: 1_700_000_000_000)];
        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();

        // 旧快照（gameId 555）已彻底清空；新对局数据加载后写入的是属于新局（999）的快照
        Assert.Equal(999, env.Service.ReservedSnapshotGameId);
        Assert.False(env.Service.CurrentState.PlayerData.ContainsKey("p-a"));
        Assert.True(env.Service.CurrentState.PlayerData.ContainsKey("p-z"));
    }

    [Fact]
    public async Task ReservedSnapshot_ChampSelectPartialLoad_DoesNotShrinkPreviousSnapshot()
    {
        var config = new AppConfig();
        await using var env = new GameInfoTestEnv(config);
        env.GameflowSession = FullSessionJson(GameId);
        SetupFullGame(env, gamesPerPlayer: 1);

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();
        Assert.Equal(10, env.Service.ReservedSnapshotLoadedCount);

        // 进入选人：内存清空后仅 2 人可加载（其余 404 → loading 占位），
        // 非真实对局阶段的新快照已加载数缩水，不得覆盖上一局完整快照
        env.Phase = "ChampSelect";
        env.GameflowSession = null;
        env.ChampSelectSession = """
        {
            "actions": [],
            "myTeam": [
                { "cellId": 0, "championId": 0, "championPickIntent": 0, "puuid": "p-me", "summonerId": 100 },
                { "cellId": 1, "championId": 0, "championPickIntent": 0, "puuid": "p-c1", "summonerId": 111 },
                { "cellId": 2, "championId": 0, "championPickIntent": 0, "puuid": "p-c2", "summonerId": 222 },
                { "cellId": 3, "championId": 0, "championPickIntent": 0, "puuid": "p-c3", "summonerId": 333 },
                { "cellId": 4, "championId": 0, "championPickIntent": 0, "puuid": "p-c4", "summonerId": 444 }
            ],
            "theirTeam": []
        }
        """;
        env.Profiles["p-c1"] = (111, 300);
        env.MatchGames["p-c1"] = [new GameDef(8001, 420, win: true, creation: 1_700_000_000_000)];

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();

        Assert.Equal(GameId, env.Service.ReservedSnapshotGameId);
        Assert.Equal(10, env.Service.ReservedSnapshotLoadedCount);
    }

    [Fact]
    public async Task ReservedSnapshot_NonActivePhase_RestoresOnlyWhenEnabled()
    {
        var config = new AppConfig();
        config.Functions.EnableReserveGameinfo = true;
        await using var env = new GameInfoTestEnv(config);
        env.GameflowSession = FullSessionJson(GameId);
        SetupFullGame(env, gamesPerPlayer: 1);

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();

        // 选人秒退：进选人清内存（保留快照）→ 再退回 Lobby，配置开启 → 从快照恢复
        env.Phase = "ChampSelect";
        env.GameflowSession = null;
        env.ChampSelectSession = null;
        await env.Service.RefreshStateAsync();
        Assert.Empty(env.Service.CurrentState.MyTeam);
        Assert.True(env.Service.HasReservedSnapshot);

        env.Phase = "Lobby";
        await env.Service.RefreshStateAsync();

        Assert.NotEmpty(env.Service.CurrentState.MyTeam);
        Assert.Equal(5, env.Service.CurrentState.MyTeam.Count);
        Assert.True(env.Service.CurrentState.ShouldShowContent);
    }

    // ── 任务3：当前玩家战绩同步 + 对局结束链式轮询 ────────────────────────

    [Fact]
    public async Task SyncCurrentPlayerMatches_UpdatesAllMultiKeyEntriesAndClearsHidden()
    {
        var config = new AppConfig();
        await using var env = new GameInfoTestEnv(config);
        env.GameflowSession = FullSessionJson(GameId);
        SetupFullGame(env, gamesPerPlayer: 1);

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();

        // 我战绩被隐藏（空列表），同步后应清隐藏并更新
        env.Sgp.Results["p-me"] =
        [
            NewMatch(900_001, 420, win: true, 1_700_100_000_000),
            NewMatch(900_002, 420, win: true, 1_700_090_000_000),
            NewMatch(900_003, 420, win: false, 1_700_080_000_000),
        ];

        await env.Service.SyncCurrentPlayerMatchesAsync(forceNetwork: true);

        var state = env.Service.CurrentState;
        foreach (string key in new[] { "p-me", "100", "0" })
        {
            var entry = state.PlayerData[key];
            Assert.False(entry.MatchHistoryHidden);
            Assert.Equal(3, entry.Matches.Count);
            Assert.Equal(2, entry.WinCount);
            Assert.Equal(1, entry.LossesCount);
        }

        Assert.Contains(env.Sgp.Calls, c => c.Puuid == "p-me" && c.Start == 0 && c.Count == 50);
    }

    [Fact]
    public async Task SyncCurrentPlayerMatches_ThrottledWithinTenSeconds()
    {
        var config = new AppConfig();
        await using var env = new GameInfoTestEnv(config);
        env.GameflowSession = FullSessionJson(GameId);
        SetupFullGame(env, gamesPerPlayer: 1);

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();
        env.Sgp.Results["p-me"] = [NewMatch(900_001, 420, win: true, 1_700_100_000_000)];

        await env.Service.SyncCurrentPlayerMatchesAsync(forceNetwork: true);
        int callsAfterFirst = env.Sgp.Calls.Count(c => c.Puuid == "p-me" && c.Start == 0);

        // 非强制同步在 10s 节流窗口内：不再发起网络请求
        await env.Service.SyncCurrentPlayerMatchesAsync(forceNetwork: false);
        Assert.Equal(callsAfterFirst, env.Sgp.Calls.Count(c => c.Puuid == "p-me" && c.Start == 0));
    }

    [Fact]
    public async Task GameEndedChain_PollsThreeTimesThenStops()
    {
        var config = new AppConfig();
        await using var env = new GameInfoTestEnv(config);
        env.GameflowSession = FullSessionJson(GameId);
        SetupFullGame(env, gamesPerPlayer: 1);

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();
        env.Sgp.Results["p-me"] = [NewMatch(900_001, 420, win: true, 1_700_100_000_000)];

        env.Service.GameEndedChainDelaysMs = [40, 40, 40];
        env.Service.NotifyGameEnded();

        await WaitUntilAsync(() => env.Sgp.Calls.Count(c => c.Puuid == "p-me" && c.Start == 0 && c.Count == 50) >= 3);
        await WaitUntilAsync(() => !env.Service.IsGameEndedChainActive);

        // 链结束后不再追加轮询
        int calls = env.Sgp.Calls.Count(c => c.Puuid == "p-me" && c.Start == 0 && c.Count == 50);
        await Task.Delay(150);
        Assert.Equal(calls, env.Sgp.Calls.Count(c => c.Puuid == "p-me" && c.Start == 0 && c.Count == 50));
    }

    [Fact]
    public async Task GameEndedChain_CancelledWhenPhaseReEntersActive()
    {
        var config = new AppConfig();
        await using var env = new GameInfoTestEnv(config);
        env.GameflowSession = FullSessionJson(GameId);
        SetupFullGame(env, gamesPerPlayer: 1);

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();
        env.Sgp.Results["p-me"] = [NewMatch(900_001, 420, win: true, 1_700_100_000_000)];

        env.Service.GameEndedChainDelaysMs = [400, 400, 400];
        env.Service.NotifyGameEnded();
        Assert.True(env.Service.IsGameEndedChainActive);

        // 重新进入活跃阶段（选人）：链式轮询立即取消
        env.Phase = "ChampSelect";
        env.GameflowSession = null;
        env.ChampSelectSession = null;
        await env.Service.RefreshStateAsync();

        Assert.False(env.Service.IsGameEndedChainActive);
        // 链式轮询被取消：不出现同步专用的 (0,50) 拉取（0,10 区间的调用属后台样本扩充）
        Assert.DoesNotContain(env.Sgp.Calls, c => c.Start == 0 && c.Count == 50);
    }

    // ── 任务7：11~50 局后台扩充 + SGP 并集 ───────────────────────────────

    [Fact]
    public async Task ExpandMatches_MergesByGameIdAndRefreshesStatsWithoutChangingList()
    {
        var config = new AppConfig();
        await using var env = new GameInfoTestEnv(config);
        env.GameflowSession = FullSessionJson(GameId);

        // 我：10 局（6 胜 4 负）+ 第 11~15 局（2 局与前 10 重复、3 局新增且全胜）
        var games = Enumerable.Range(0, 10)
            .Select(i => new GameDef(100_000 + i, 420, win: i < 6, creation: 1_700_000_000_000 - i * 60_000))
            .ToList();
        games.Add(games[2]);                          // 与前 10 局重复
        games.Add(games[5]);                          // 与前 10 局重复
        games.Add(new GameDef(200_001, 420, win: true, creation: 1_700_000_000_000 - 20 * 60_000));
        games.Add(new GameDef(200_002, 420, win: true, creation: 1_700_000_000_000 - 21 * 60_000));
        games.Add(new GameDef(200_003, 420, win: true, creation: 1_700_000_000_000 - 22 * 60_000));
        env.MatchGames["p-me"] = games;
        env.Profiles["p-me"] = (100, 300);
        foreach (var (puuid, sid) in new[] { ("p-a", 101L), ("p-b", 102L), ("p-c", 103L), ("p-d", 104L), ("p-e", 201L), ("p-f", 202L), ("p-g", 203L), ("p-h", 204L), ("p-i", 205L) })
        {
            env.Profiles[puuid] = (sid, 300);
            env.MatchGames[puuid] = [];
        }

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();

        // 等待后台扩充处理完成
        await WaitUntilAsync(() => env.Service.ExtraMatchesProcessedCount >= 1);

        var entry = env.Service.CurrentState.PlayerData["p-me"];
        // Matches 列表不变（仍为前 10 局，首局 gameId 不变）
        Assert.Equal(10, entry.Matches.Count);
        Assert.Equal(100_000, entry.Matches[0].GameId);
        // 统计基于 13 局去重样本（10 + 3 新增胜局）：9 胜 4 负
        Assert.Equal(9, entry.WinCount);
        Assert.Equal(4, entry.LossesCount);
    }

    [Fact]
    public async Task ExpandMatches_SgpFallbackUnionsAndOverridesOverlappingGames()
    {
        var config = new AppConfig();
        await using var env = new GameInfoTestEnv(config);
        env.GameflowSession = FullSessionJson(GameId);

        // 我：前 10 局 6 胜 4 负；LCU 第 11~14 局仅 4 屺（<20 触发 SGP 补拉）
        var games = Enumerable.Range(0, 10)
            .Select(i => new GameDef(100_000 + i, 420, win: i < 6, creation: 1_700_000_000_000 - i * 60_000))
            .ToList();
        games.Add(new GameDef(300_001, 420, win: true, creation: 1_700_000_000_000 - 20 * 60_000));   // LCU: 胜
        games.Add(new GameDef(300_002, 420, win: true, creation: 1_700_000_000_000 - 21 * 60_000));
        games.Add(new GameDef(300_003, 420, win: false, creation: 1_700_000_000_000 - 22 * 60_000));
        games.Add(new GameDef(300_004, 420, win: false, creation: 1_700_000_000_000 - 23 * 60_000));
        env.MatchGames["p-me"] = games;
        env.Profiles["p-me"] = (100, 300);

        // SGP 补拉：300_001 重合但结果为负（SGP 覆盖重合局），另 2 局新负局
        env.Sgp.Results["p-me"] =
        [
            NewMatch(300_001, 420, win: false, 1_700_000_000_000 - 20 * 60_000),
            NewMatch(300_005, 420, win: false, 1_700_000_000_000 - 24 * 60_000),
            NewMatch(300_006, 420, win: false, 1_700_000_000_000 - 25 * 60_000),
        ];

        foreach (var (puuid, sid) in new[] { ("p-a", 101L), ("p-b", 102L), ("p-c", 103L), ("p-d", 104L), ("p-e", 201L), ("p-f", 202L), ("p-g", 203L), ("p-h", 204L), ("p-i", 205L) })
        {
            env.Profiles[puuid] = (sid, 300);
            env.MatchGames[puuid] = [];
        }

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();
        await WaitUntilAsync(() => env.Service.ExtraMatchesProcessedCount >= 1);
        await WaitUntilAsync(() => env.Sgp.Calls.Any(c => c.Puuid == "p-me" && c.Start == 10 && c.Count == 40));

        var entry = env.Service.CurrentState.PlayerData["p-me"];
        // 样本：前 10 局（6 胜 4 负）+ 300_001(负·SGP 覆盖) + 300_002(胜) + 300_003(负) + 300_004(负) + 300_005(负) + 300_006(负)
        // = 16 局，7 胜 9 负
        Assert.Equal(7, entry.WinCount);
        Assert.Equal(9, entry.LossesCount);
        Assert.Equal(10, entry.Matches.Count);
    }

    // ── 任务8：GameInfoFilter 三处生效 ───────────────────────────────────

    [Fact]
    public async Task GameInfoFilter_FirstScreenFiltersMatchesByCurrentQueue()
    {
        var config = new AppConfig();
        config.Functions.GameInfoFilter = true;
        await using var env = new GameInfoTestEnv(config);
        env.GameflowSession = FullSessionJson(GameId);  // 队列 420

        env.Profiles["p-me"] = (100, 300);
        env.MatchGames["p-me"] =
        [
            new GameDef(100_001, 420, win: true, creation: 1_700_000_000_000),
            new GameDef(100_002, 450, win: true, creation: 1_699_900_000_000),  // 大乱斗，应被过滤
            new GameDef(100_003, 420, win: false, creation: 1_699_800_000_000),
        ];

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();

        var entry = env.Service.CurrentState.PlayerData["p-me"];
        Assert.False(entry.MatchHistoryHidden);
        Assert.Equal(2, entry.Matches.Count);
        Assert.All(entry.Matches, m => Assert.Equal(420, m.QueueId));
        Assert.Equal(1, entry.WinCount);
        Assert.Equal(1, entry.LossesCount);
    }

    [Fact]
    public async Task GameInfoFilter_ExpansionSamplesFilteredByCurrentQueue()
    {
        var config = new AppConfig();
        config.Functions.GameInfoFilter = true;
        await using var env = new GameInfoTestEnv(config);
        env.GameflowSession = FullSessionJson(GameId);  // 队列 420

        // 前 2 局匹配队列（全胜）；第 3~7 局为大乱斗（全胜）——若未过滤会刷成 7 胜 0 负
        env.Profiles["p-me"] = (100, 300);
        env.MatchGames["p-me"] =
        [
            new GameDef(100_001, 420, win: true, creation: 1_700_000_000_000),
            new GameDef(100_002, 420, win: true, creation: 1_699_900_000_000),
            new GameDef(100_003, 450, win: true, creation: 1_699_800_000_000),
            new GameDef(100_004, 450, win: true, creation: 1_699_700_000_000),
            new GameDef(100_005, 450, win: true, creation: 1_699_600_000_000),
            new GameDef(100_006, 450, win: true, creation: 1_699_500_000_000),
            new GameDef(100_007, 450, win: true, creation: 1_699_400_000_000),
        ];

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();
        Assert.Equal(2, env.Service.CurrentState.PlayerData["p-me"].WinCount);

        await WaitUntilAsync(() => env.Service.ExtraMatchesProcessedCount >= 1);
        await Task.Delay(100);  // 等待统计写回

        var entry = env.Service.CurrentState.PlayerData["p-me"];
        Assert.Equal(2, entry.WinCount);
        Assert.Equal(2, entry.Matches.Count);
    }

    [Fact]
    public async Task GameInfoFilter_CurrentPlayerSyncFiltersByCurrentQueue()
    {
        var config = new AppConfig();
        config.Functions.GameInfoFilter = true;
        await using var env = new GameInfoTestEnv(config);
        env.GameflowSession = FullSessionJson(GameId);  // 队列 420
        SetupFullGame(env, gamesPerPlayer: 1);

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();

        env.Sgp.Results["p-me"] =
        [
            NewMatch(900_001, 420, win: true, 1_700_100_000_000),
            NewMatch(900_002, 450, win: true, 1_700_090_000_000),   // 大乱斗，应被过滤
            NewMatch(900_003, 420, win: false, 1_700_080_000_000),
        ];

        await env.Service.SyncCurrentPlayerMatchesAsync(forceNetwork: true);

        var entry = env.Service.CurrentState.PlayerData["p-me"];
        Assert.Equal(2, entry.Matches.Count);
        Assert.All(entry.Matches, m => Assert.Equal(420, m.QueueId));
        Assert.Equal(1, entry.WinCount);
    }

    // ── 任务5：IsHumanoid 人机判定与自定义会话敌方 cellId 重映射 ──────────

    [Fact]
    public async Task ChampSelect_CustomGame_FlagsHumanoidBotsAndRemapsEnemyCells()
    {
        var config = new AppConfig();
        await using var env = new GameInfoTestEnv(config);
        env.Phase = "ChampSelect";
        env.GameflowSession = null;
        env.ChampSelectSession = """
        {
            "isCustomGame": true,
            "actions": [],
            "myTeam": [
                { "cellId": 0, "championId": 64, "championPickIntent": 0, "puuid": "p-me", "summonerId": 100 },
                { "cellId": 1, "championId": 0, "championPickIntent": 0, "isHumanoid": true },
                { "cellId": 2, "championId": 81, "championPickIntent": 0, "isHumanoid": true }
            ],
            "theirTeam": [
                { "cellId": 0 },
                { "cellId": 1, "championId": 0, "championPickIntent": 0, "puuid": "p-e1", "summonerId": 201 },
                { "cellId": 2, "championId": 55, "championPickIntent": 0 }
            ]
        }
        """;

        await env.Service.RefreshStateAsync();

        var state = env.Service.CurrentState;
        Assert.Equal(3, state.MyTeam.Count);

        // isHumanoid 成员补标 bot，下游按本地占位处理
        Assert.True(state.MyTeam[1].IsHumanoid);
        Assert.True(state.MyTeam[1].Bot);
        Assert.True(state.MyTeam[1].IsBot);
        Assert.True(state.MyTeam[2].IsHumanoid);
        Assert.True(state.MyTeam[2].IsBot);
        Assert.Equal(81, state.MyTeam[2].ChampionId);

        // 敌方空占位（无身份无英雄）过滤；自定义会话敌方 cellId 重映射 5+idx
        Assert.Equal(2, state.TheirTeam.Count);
        Assert.Equal(5, state.TheirTeam[0].CellId);
        Assert.Equal("p-e1", state.TheirTeam[0].Puuid);
        Assert.Equal(6, state.TheirTeam[1].CellId);
        Assert.Equal(55, state.TheirTeam[1].ChampionId);
    }

    [Fact]
    public async Task ChampSelect_CustomGame_HumanoidEnemySkipsNetworkRequests()
    {
        var config = new AppConfig();
        await using var env = new GameInfoTestEnv(config);
        env.Phase = "ChampSelect";
        env.GameflowSession = null;
        env.ChampSelectSession = """
        {
            "isCustomGame": true,
            "actions": [],
            "myTeam": [
                { "cellId": 0, "championId": 64, "championPickIntent": 0, "puuid": "p-me", "summonerId": 100 },
                { "cellId": 1, "championId": 22, "championPickIntent": 0, "isHumanoid": true }
            ],
            "theirTeam": []
        }
        """;
        env.Profiles["p-me"] = (100, 300);
        env.MatchGames["p-me"] = [new GameDef(100_001, 420, win: true, creation: 1_700_000_000_000)];

        await env.Service.RefreshStateAsync();
        await env.Service.LoadAllPlayersAsync();

        // 人机走本地占位：不发起召唤师/战绩网络请求
        Assert.DoesNotContain(env.Server.Requests, r => r.Path.Contains("summoners/puuid") && !r.Path.Contains("current-summoner") && !r.Path.Contains("p-me"));
        var botEntry = env.Service.CurrentState.PlayerData.TryGetValue("1", out var bot) ? bot : null;
        Assert.NotNull(botEntry);
        Assert.True(botEntry!.MatchHistoryHidden);
        Assert.False(botEntry.Loading);
    }

    [Theory]
    [InlineData(0, 4, "blue")]
    [InlineData(5, 9, "red")]
    public async Task ChampSelect_ResolvesMapSideFromMyTeamCellIds(int firstCell, int lastCell, string expectedSide)
    {
        var config = new AppConfig();
        await using var env = new GameInfoTestEnv(config);
        env.Phase = "ChampSelect";
        env.GameflowSession = null;
        env.ChampSelectSession = $$"""
        {
            "isCustomGame": false,
            "actions": [],
            "myTeam": [
                { "cellId": {{firstCell}}, "championId": 64, "championPickIntent": 0, "puuid": "p-me", "summonerId": 100 },
                { "cellId": {{lastCell}}, "championId": 0, "championPickIntent": 0, "puuid": "p-a", "summonerId": 101 }
            ],
            "theirTeam": []
        }
        """;

        // pin-drop 未就绪（NotFound）→ 回退按 myTeam 格子位判定（0..4 蓝 / 5..9 红）
        await env.Service.RefreshStateAsync();

        Assert.Equal(expectedSide, env.Service.CurrentState.MapSide);
    }

    [Fact]
    public async Task ChampSelect_MapSideKnownOnReentry_DoesNotReprobe()
    {
        var config = new AppConfig();
        await using var env = new GameInfoTestEnv(config);
        env.Phase = "ChampSelect";
        env.GameflowSession = null;
        env.ChampSelectSession = """
        {
            "isCustomGame": false,
            "actions": [],
            "myTeam": [
                { "cellId": 0, "championId": 64, "championPickIntent": 0, "puuid": "p-me", "summonerId": 100 }
            ],
            "theirTeam": []
        }
        """;

        await env.Service.RefreshStateAsync();
        Assert.Equal("blue", env.Service.CurrentState.MapSide);

        int pinDropRequests = env.Server.Requests.Count(r => r.Path.EndsWith("pin-drop-notification", StringComparison.Ordinal));

        // 同阶段重入（选人事件高频触发）：阵营已知时不再探测
        await env.Service.RefreshStateAsync();

        Assert.Equal("blue", env.Service.CurrentState.MapSide);
        Assert.Equal(pinDropRequests, env.Server.Requests.Count(r => r.Path.EndsWith("pin-drop-notification", StringComparison.Ordinal)));
    }

    // ── 共用构造 ─────────────────────────────────────────────────────────

    private static MatchSummary NewMatch(long gameId, int queueId, bool win, long creation) => new()
    {
        GameId = gameId,
        QueueId = queueId,
        QueueName = "排位赛",
        MapName = "召唤师峡谷",
        GameCreation = creation,
        DurationSeconds = 1500,
        Win = win,
        Remake = false,
        ChampionId = 1,
        Spell1Id = 4,
        Spell2Id = 14,
        ChampionLevel = 15,
        Kills = 3,
        Deaths = 2,
        Assists = 5,
        ItemIds = [0, 0, 0, 0, 0, 0, 0],
        RuneId = 8000,
        CreepScore = 150,
        Gold = 10000,
        DamageToChampions = 20000,
        TotalHeal = 2000,
        AugmentIds = [],
    };
}
