using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Opgg.Models;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;
using YuumiX.Core.Realtime;
using YuumiX.Core.Serialization;
using YuumiX.Core.Updates;
using YuumiX.Core.Upload;

namespace YuumiX.AotVerification;

/// <summary>
/// AOT 验收专用进程：在 IsReflectionEnabledByDefault=false（等价 AOT 成品前提）下
/// 走 Core 的真实生产序列化路径 + SignalR 全链路。任何一段抛异常即整体失败。
/// </summary>
internal static class Program
{
    private static int _checkIndex;

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"[验收 #{_checkIndex}] 失败：{message}");
        }

        _checkIndex++;
        Console.WriteLine($"  ok #{_checkIndex}: {message}");
    }

    private static async Task<int> Main()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "yuumix-aot-verify", DateTime.UtcNow.Ticks.ToString("x"));
            Directory.CreateDirectory(root);
            Console.WriteLine($"隔离数据目录: {root}");

            VerifyConfig(Path.Combine(root, "config"));
            await VerifySearchHistoryAsync(Path.Combine(root, "history"));
            VerifySnapshot(Path.Combine(root, "snapshot"));
            VerifyUploadPayload();
            VerifyUpdateMetadata();
            VerifyOpggAndAlias();
            await VerifySignalRAsync();

            Console.WriteLine($"全部通过：{_checkIndex} 项检查（反射序列化已关闭）");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("AOT 验收失败：");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // ---------------------------------------------------------------- R2：配置

    private static void VerifyConfig(string dir)
    {
        Console.WriteLine("== R2 配置持久化 ==");
        Directory.CreateDirectory(dir);
        var store = new ConfigStore(Path.Combine(dir, "config.json"), NullLogger<ConfigStore>.Instance);

        // 1.1 首次启动：缺配置 → 生成默认并立即落盘
        var cfg = store.Load();
        Check(File.Exists(store.FilePath), "首次启动生成并落盘默认配置");

        // 1.2 合法旧配置：v1 遗留键迁移 + serde 字段级缺省 + 未知键往返 + WholeDoubleConverter + 中文原样
        File.WriteAllText(store.FilePath, """
            {
              "Version": 1,
              "General": { "EnableGithubProxy": true, "GithubProxyAddr": "127.0.0.1:7890", "LolPath": ["D:\\游戏\\英雄联盟"], "LegacyUnknown": { "a": 1 } },
              "Functions": { "AutoBanDelay": 2 },
              "UnknownTop": 123
            }
            """);
        File.Delete(store.ErrorFilePath);
        cfg = store.Load();
        Check(cfg.General.EnableHttpProxy, "v1 键 EnableGithubProxy 迁移为 EnableHttpProxy");
        Check(cfg.General.HttpProxyAddr == "127.0.0.1:7890", "v1 键 GithubProxyAddr 迁移为 HttpProxyAddr");
        Check(cfg.General.LolPath is ["D:\\游戏\\英雄联盟"], "中文路径正确解析");
        Check(!cfg.Functions.EnableReserveGameinfo, "serde 字段级缺省：Functions 存在但缺键时 EnableReserveGameinfo=false");
        store.Save(cfg);
        string text = File.ReadAllText(store.FilePath);
        Check(!text.Contains("\"EnableGithubProxy\""), "写回使用 v2 新键名");
        Check(text.Contains("英雄联盟"), "中文原样输出（UnsafeRelaxedJsonEscaping 保持）");
        Check(text.Contains("\"LegacyUnknown\""), "General 层未知键无损保留");
        Check(text.Contains("\"UnknownTop\": 123"), "顶层未知键无损保留");
        Check(text.Contains("\"AutoBanDelay\": 2.0"), "WholeDoubleConverter 整数 double 补写 .0");

        // 1.3 损坏配置：备份 + .error + 降级默认
        File.WriteAllText(store.FilePath, "{ not json");
        File.Delete(store.ErrorFilePath);
        cfg = store.Load();
        Check(cfg.Version == AppConfig.CurrentVersion, "损坏配置降级为默认值");
        Check(File.Exists(store.ErrorFilePath), "损坏配置写入 .error 提示");
        Check(Directory.GetFiles(dir, "config.json.backup.*").Length == 1, "损坏配置先备份原文件");
    }

    // ------------------------------------------------- R2：搜索历史（异步流路径）

    private static async Task VerifySearchHistoryAsync(string dir)
    {
        Console.WriteLine("== R2 搜索历史 ==");
        var store = new SearchHistoryStore(Path.Combine(dir, "search_history.json"));

        await store.AddAsync("Faker");
        await store.AddAsync("验证召唤师");
        var history = await store.GetHistoryAsync();
        Check(history.Count == 2 && history[0] == "验证召唤师", "搜索历史写入后新条目居前（异步序列化落盘）");

        // 新实例重读磁盘：验证 DeserializeAsync 流式反序列化
        var reloaded = new SearchHistoryStore(Path.Combine(dir, "search_history.json"));
        var reread = await reloaded.GetHistoryAsync();
        Check(reread.Count == 2 && reread[1] == "Faker", "搜索历史跨实例往返一致");
    }

    // ------------------------------------------------------- R2：对局快照

    private static void VerifySnapshot(string dir)
    {
        Console.WriteLine("== R2 对局快照 ==");
        var store = new GameInfoSnapshotStore(Path.Combine(dir, "snapshot.json"));

        var snapshot = new ReservedSnapshot
        {
            GameId = 123456789,
            TeamCount = 10,
            LoadedCount = 10,
            MyTeam = [new PremadePlayerLike { Puuid = "puuid-me", SummonerName = "验证召唤师", ChampionId = 4321 }],
            TheirTeam = [],
        };
        store.Write(snapshot);
        var restored = store.Read();
        Check(restored is not null
            && restored.GameId == 123456789
            && restored.TeamCount == 10
            && restored.MyTeam is [{ Puuid: "puuid-me", SummonerName: "验证召唤师", ChampionId: 4321 }],
            "快照写入后读取往返一致（私有嵌套载荷 + 中文）");

        store.Write(null);
        Check(store.Read() is null && !File.Exists(store.FilePath), "写入 null 清空快照文件");

        // 版本不识别 → 按无快照处理
        File.WriteAllText(store.FilePath, """{"Version": 99, "Snapshot": {}}""");
        Check(store.Read() is null, "快照版本不识别时返回 null");
    }

    // ------------------------------------------- R3：上传载荷（Web 默认 + camelCase）

    private static void VerifyUploadPayload()
    {
        Console.WriteLine("== R3 上传载荷 ==");
        // PendingUploadStore / PostAsJsonAsync 共用 AotJson.WebOptions（JsonSerializerDefaults.Web + 源生成解析器）
        const string json = """
            {
              "matchInfo": { "matchId": 987654321, "gameMode": "CLASSIC", "gameType": "MATCHED_GAME",
                             "queueId": 420, "gameCreation": "2026-11-01", "gameDuration": 1800,
                             "gameVersion": "16.1", "participants": [] },
              "participants": []
            }
            """;
        var payload = JsonSerializer.Deserialize(json, AotJson.WebOptions.GetTypeInfo<UploadPayload>());
        Check(payload is not null && payload.MatchInfo.MatchId == 987654321, "上传载荷反序列化（Web 默认 camelCase）");

        string round = JsonSerializer.Serialize(payload!, AotJson.WebOptions.GetTypeInfo<UploadPayload>());
        Check(round.Contains("\"matchId\":987654321") && round.Contains("\"participants\":[]"),
            "上传载荷序列化回写保持 camelCase 与数组形态");

        // ParticipantInfo 的 PascalCase 特例（serde rename 对齐）
        const string participantJson = """
            {
              "matchInfo": { "matchId": 1, "gameMode": "m", "gameType": "t", "queueId": 420,
                             "gameCreation": "c", "gameDuration": 1, "gameVersion": "v",
                             "participants": [] },
              "participants": [ { "summonerName": "a", "puuid": "p", "teamId": 100, "championId": 1,
                                  "championName": "Yuumi", "SummonerSpell1Id": 4, "SummonerSpell2Id": 12,
                                  "HexTech0": 0, "HexTech1": 0, "HexTech2": 0, "HexTech3": 0, "HexTech4": 0,
                                  "win": true, "kills": 1, "deaths": 0 } ]
            }
            """;
        var withParticipant = JsonSerializer.Deserialize(participantJson, AotJson.WebOptions.GetTypeInfo<UploadPayload>());
        Check(withParticipant?.Participants is [{ SummonerSpell1Id: 4, Win: true }],
            "ParticipantInfo PascalCase 特例字段绑定正确");
    }

    // --------------------------------------------- R3：更新元数据（camelCase 命名策略）

    private static void VerifyUpdateMetadata()
    {
        Console.WriteLine("== R3 更新元数据 ==");
        // ReleaseEntry 缓存：与 UpdateService 相同的 camelCase options 形态
        var entriesOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, TypeInfoResolver = CoreJsonContext.Default };
        const string entriesJson = """
            [ { "tag": "v0.3.0-rc.1", "publishedAt": "2026-11-01T10:00:00Z", "body": "更新说明",
                "portableAssetUrl": "https://example.com/p.zip", "isPrerelease": true } ]
            """;
        var entries = JsonSerializer.Deserialize(entriesJson, entriesOptions.GetTypeInfo<List<UpdateService.ReleaseEntry>>());
        Check(entries is [{ Tag: "v0.3.0-rc.1", IsPrerelease: true }], "ReleaseEntry 缓存反序列化");
        string entriesRound = JsonSerializer.Serialize(entries!, entriesOptions.GetTypeInfo<List<UpdateService.ReleaseEntry>>());
        Check(entriesRound.Contains("\"tag\":\"v0.3.0-rc.1\"") && entriesRound.Contains("\"isPrerelease\":true"),
            "ReleaseEntry 缓存写回 camelCase");

        // DownloadCheckpoint：显式小写 JsonPropertyName
        var checkpointOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, TypeInfoResolver = CoreJsonContext.Default };
        const string checkpointJson = """{ "url": "https://example.com/yuumix.zip", "etag": "\"abc\"", "lastModified": "Sat, 01 Nov 2026 00:00:00 GMT", "totalBytes": 123456 }""";
        var checkpoint = JsonSerializer.Deserialize(checkpointJson, checkpointOptions.GetTypeInfo<DownloadCheckpoint>());
        Check(checkpoint is { Url: "https://example.com/yuumix.zip", TotalBytes: 123456 }, "断点续传元数据反序列化");
        string checkpointRound = JsonSerializer.Serialize(checkpoint!, checkpointOptions.GetTypeInfo<DownloadCheckpoint>());
        Check(checkpointRound.Contains("\"totalBytes\":123456"), "断点续传元数据写回小写键名");
    }

    // ------------------------------------------------ R3：OP.GG 载荷与英雄别名缓存

    private static void VerifyOpggAndAlias()
    {
        Console.WriteLine("== R3 OP.GG / 别名缓存 ==");
        // OpggParser 同款 options 形态（大小写不敏感 + snake_case JsonPropertyName）
        var opggOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, TypeInfoResolver = CoreJsonContext.Default };
        const string tierJson = """{ "meta": { "version": "1.0" }, "data": [ { "id": 1, "name": "亚索" } ] }""";
        var tier = JsonSerializer.Deserialize(tierJson, opggOptions.GetTypeInfo<OpggTierPayload>());
        Check(tier is { Data: [{ Id: 1, Name: "亚索" }] }, "OP.GG 梯队载荷解析");

        const string buildJson = """{ "meta": { "version": "1.0" }, "data": { "runes": [], "last_items": [] } }""";
        var build = JsonSerializer.Deserialize(buildJson, opggOptions.GetTypeInfo<OpggBuildPayload>());
        Check(build is { Data: { Runes: [], LastItems: [] } }, "OP.GG 出装载荷解析");

        const string perkJson = """[ { "id": 8000, "name": "精密", "iconPath": "x", "slots": [ { "perks": [1, 2] } ] } ]""";
        var perks = JsonSerializer.Deserialize(perkJson, opggOptions.GetTypeInfo<List<PerkStyle>>());
        Check(perks is [{ Id: 8000, Slots: [{ Perks: [1, 2] }] }], "LCU 符文系解析（顶级数组形态）");

        // ChampionAliasProvider 缓存结构（默认 options：属性名 PascalCase、大小写敏感）
        var cacheOptions = new JsonSerializerOptions { TypeInfoResolver = CoreJsonContext.Default };
        const string cacheJson = """{ "Schema": 1, "AppVersion": "0.3.0", "FetchedAt": "2026-11-01T00:00:00Z", "Keywords": { "4321": "喵,魔法猫咪" } }""";
        var cache = JsonSerializer.Deserialize(cacheJson, cacheOptions.GetTypeInfo<ChampionAliasProvider.CacheDocument>());
        Check(cache?.Keywords?["4321"] == "喵,魔法猫咪", "英雄别名缓存反序列化");
        // 大小写敏感语义保持：小写键不得误命中（旧行为不回退）
        const string cacheJsonLower = """{ "schema": 1, "keywords": { "4321": "喵" } }""";
        var cacheLower = JsonSerializer.Deserialize(cacheJsonLower, cacheOptions.GetTypeInfo<ChampionAliasProvider.CacheDocument>());
        Check(cacheLower is { Schema: 0, Keywords: null }, "别名缓存大小写敏感语义保持");
    }

    // ----------------------------------------------------- R4：SignalR 全链路

    private static async Task VerifySignalRAsync()
    {
        Console.WriteLine("== R4 SignalR 全链路（本地 Kestrel Hub） ==");
        HostState.Reset();
        int port = GetFreePort();
        string hubBase = $"http://127.0.0.1:{port}";

        var hub = StartHost(hubBase);
        var config = new AppConfig();
        config.Functions.LcuRealtimeEnabled = true;
        config.General.UploadApiUrl = $"{hubBase}/api/lol/upload"; // 走 BuildHubBaseUrl 的剥后缀规则
        config.General.SignalrUserId = "verify-user";

        var service = new SignalrService(
            () => new FakeLcuTransport(),
            () => config,
            NullLogger<SignalrService>.Instance);

        await service.ApplyConfigAsync();
        await WaitForAsync(() => hub.IsClientConnected, TimeSpan.FromSeconds(15), "客户端未在 15 秒内连上本地 Hub");

        // 服务端在 OnConnectedAsync 已推送「对象形态」命令，客户端会回 ReportResult
        await WaitForAsync(() => hub.FindReport("q-connect") is not null, TimeSpan.FromSeconds(10), "对象形态命令未回传");
        Check(hub.FindReport("q-connect")!.Contains("验证召唤师"),
            "对象形态命令经白名单 → LCU GET → 回传 queryId 关联结果（载荷为 LCU 响应体）");

        // 字符串形态命令（后端容错路径：wire 上是 JSON 字符串值）
        await hub.PushCommandStringAsync(new JsonObject { ["endpoint"] = "/lol-gameflow/v1/gameflow-phase" }.ToJsonString(), "q-string");
        await WaitForAsync(() => hub.FindReport("q-string") is not null, TimeSpan.FromSeconds(10), "字符串形态命令未回传");
        Check(hub.FindReport("q-string")!.Contains("ChampSelect"), "字符串形态命令解析并回传 LCU 结果");

        // 白名单拒绝路径
        await hub.PushCommandAsync(new JsonObject { ["endpoint"] = "/lol-endgame/v1/secret" }.ToJsonString(), "q-deny");
        await WaitForAsync(() => hub.FindReport("q-deny") is not null, TimeSpan.FromSeconds(10), "未授权命令未回传");
        Check(hub.FindReport("q-deny")!.Contains("endpoint 未授权"), "未授权路径被拒绝并回传错误");

        // 连接成功 1 秒后自动推送召唤师对齐信息（eventType/data/timestamp 包装）。
        // 注意：载荷经默认编码器输出，中文为 \uXXXX 转义（与旧反射路径行为一致），
        // 断言用结构化解析而不是文本匹配。
        await WaitForAsync(() => hub.Reports.Any(r => r.Contains("summoner_info")), TimeSpan.FromSeconds(10), "召唤师对齐信息未推送");
        string info = hub.Reports.First(r => r.Contains("summoner_info"));
        using (var infoDoc = JsonDocument.Parse(info[(info.IndexOf('|', info.IndexOf('|') + 1) + 1)..]))
        {
            var root = infoDoc.RootElement;
            Check(root.GetProperty("eventType").GetString() == "summoner_info"
                && root.GetProperty("data").GetProperty("summonerName").GetString() == "验证召唤师"
                && root.TryGetProperty("timestamp", out _),
                "summoner_info 载荷保持 eventType/data/timestamp 包装与字段");
        }

        // 业务心跳（30 秒周期）——只等一轮
        await WaitForAsync(() => hub.HeartbeatUserIds.Count > 0, TimeSpan.FromSeconds(40), "业务心跳未在 40 秒内到达");
        Check(hub.HeartbeatUserIds.First() == "verify-user", "心跳携带解析后的 userId");

        // 断线重连：杀掉宿主 → 库级自动重连耗尽 → RetryLoop 5 秒兜底 → 宿主重启后重连成功
        await hub.StopAsync();
        await Task.Delay(TimeSpan.FromSeconds(16));
        var hub2 = StartHost(hubBase);
        try
        {
            await WaitForAsync(() => service.Current.State == SignalrConnectionState.Connected, TimeSpan.FromSeconds(45),
                $"断线后未重连（当前状态 {service.Current.State} {service.Current.Error}）");
            await WaitForAsync(() => hub2.HeartbeatUserIds.Count > 0, TimeSpan.FromSeconds(40), "重连后业务心跳未恢复");
            Check(true, "断线重连后心跳恢复");
        }
        finally
        {
            await service.StopAsync();
            service.Dispose();
            await hub2.StopAsync();
        }

        Console.WriteLine("== R4 SignalR 全链路完成 ==");
    }

    private static VerifyHubHost StartHost(string baseUri)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // 服务端 JSON 协议同样挂源生成解析器（本进程反射序列化已关闭）
        builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolver = VerifyJsonContext.Default);
        builder.WebHost.UseUrls(baseUri);

        var app = builder.Build();
        app.MapHub<VerifyHub>("/lcuHub");
        app.StartAsync().GetAwaiter().GetResult();

        var context = app.Services.GetRequiredService<IHubContext<VerifyHub>>();
        return new VerifyHubHost(app, context);
    }

    /// <summary>验收用 Hub：记录 Heartbeat / ReportResult，并在客户端接入时下发对象形态命令。</summary>
    public sealed class VerifyHub : Hub
    {
        public void Heartbeat(string userId) => HostState.HeartbeatUserIds.Enqueue(userId);

        public void ReportResult(string userId, string payload, string queryId) =>
            HostState.Reports.Enqueue($"{queryId}|{userId}|{payload}");

        public override async Task OnConnectedAsync()
        {
            Interlocked.Increment(ref HostState.ConnectedCount);
            using var doc = JsonDocument.Parse("""{"endpoint":"/lol-summoner/v1/current-summoner"}""");
            await Clients.All.SendAsync("ReceiveCommand", doc.RootElement.Clone(), "q-connect");
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            Interlocked.Decrement(ref HostState.ConnectedCount);
            return Task.CompletedTask;
        }
    }

    private static class HostState
    {
        public static readonly ConcurrentQueue<string> Reports = new();
        public static readonly ConcurrentQueue<string> HeartbeatUserIds = new();
        public static int ConnectedCount;

        public static void Reset()
        {
            Reports.Clear();
            HeartbeatUserIds.Clear();
            ConnectedCount = 0;
        }
    }

    private sealed class VerifyHubHost(WebApplication app, IHubContext<VerifyHub> context)
    {
        public ConcurrentQueue<string> Reports => HostState.Reports;
        public ConcurrentQueue<string> HeartbeatUserIds => HostState.HeartbeatUserIds;

        public bool IsClientConnected => Volatile.Read(ref HostState.ConnectedCount) > 0;

        public string? FindReport(string queryId) =>
            Reports.FirstOrDefault(r => r.StartsWith(queryId + "|", StringComparison.Ordinal));

        /// <summary>对象形态命令：wire 上是 JSON 对象（后端 LcuCommandMessage 常规路径）。</summary>
        public async Task PushCommandAsync(string commandJson, string queryId)
        {
            using var doc = JsonDocument.Parse(commandJson);
            await context.Clients.All.SendAsync("ReceiveCommand", doc.RootElement.Clone(), queryId);
        }

        /// <summary>字符串形态命令：wire 上是 JSON 字符串值（后端容错路径）。</summary>
        public async Task PushCommandStringAsync(string commandJson, string queryId) =>
            await context.Clients.All.SendAsync("ReceiveCommand", commandJson, queryId);

        public async Task StopAsync()
        {
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        throw new TimeoutException(failureMessage);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>假 LCU 通道：current-summoner 返回固定载荷，其余统一返回 gameflow 载荷。</summary>
    private sealed class FakeLcuTransport : ILcuTransport
    {
        private const string SummonerJson = """
            { "displayName": "验证召唤师", "summonerId": 123456, "puuid": "puuid-verify",
              "platformId": "TJ101", "summonerLevel": 33, "profileIconId": 42 }
            """;

        public Task<LcuResponse> SendAsync(LcuRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(request.Path.Contains("/lol-summoner/v1/current-summoner", StringComparison.Ordinal)
                ? new LcuResponse(200, SummonerJson)
                : new LcuResponse(200, "{\"phase\":\"ChampSelect\"}"));

        public void Dispose()
        {
        }
    }
}

/// <summary>服务端载荷元数据：仅覆盖验收链路实际收发的参数类型（string / JsonElement）。</summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class VerifyJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
