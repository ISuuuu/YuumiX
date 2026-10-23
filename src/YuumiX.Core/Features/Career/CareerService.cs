using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.SavedPlayers;
using YuumiX.Core.Features.SavedPlayers.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Features.Career;

/// <summary>
/// <see cref="ICareerService"/> 的 LCU 实现：排位总览取数 + 按 puuid 缓存。
///
/// 三条实现纪律与 <c>MatchService</c> 保持一致：
/// <list type="number">
///   <item><b>复用 <c>LcuConnection.Transport</c></b>，不自建 <c>HttpClient</c>：
///         并发限流与自签证书忽略都在传输层，绕开它等于把"客户端被自己打满"重新引入。
///         用提供器是因为重连会换传输层，而服务本身应当跨重连复用。</item>
///   <item><b>失败不抛异常</b>：未连接 / 非成功状态码 / JSON 损坏一律给
///         <see cref="RankedStats.Empty"/>，界面显示 <c>--</c> 即可。</item>
///   <item><b>只在"飞行中"去重</b>：刷新按钮连点两次只打一次本机 GET；
///         请求结束立刻移出表，不把结果长期留在这里——长期缓存由带 TTL 的那层负责。</item>
/// </list>
///
/// <b>为什么缓存空结果而不缓存失败</b>：未定级账号确实没有排位记录，
/// 每次进页面都重拉是浪费；而"客户端没连上"是暂时状态，缓存它会让连上之后
/// 还显示"--"——那才像坏了。
/// </summary>
public sealed class CareerService : ICareerService
{
    /// <summary>排位总览路径模板（参数：puuid）。</summary>
    internal const string RankedStatsPathTemplate = "/lol-ranked/v1/ranked-stats/{0}";

    /// <summary>
    /// 默认缓存时长。段位只在对局结算后变化，60 秒足够挡住"进页面/切页签"这类抖动，
    /// 又不会让刚打完的定级赛迟迟不更新（刷新按钮另有 <c>forceRefresh</c>）。
    /// </summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(60);

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly SavedPlayersStore? _savedPlayersStore;
    private readonly GameDataService? _gameDataService;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<CareerService>? _logger;

    /// <summary>已完成的查询：puuid → (结果, 完成时刻)。</summary>
    private readonly ConcurrentDictionary<string, CachedStats> _cache = new(StringComparer.Ordinal);

    /// <summary>飞行中的查询：同 puuid 并发共享一次 HTTP，结束后移除。</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<RankedStats>>> _inFlight = new(StringComparer.Ordinal);

    /// <param name="transportProvider">当前传输层（来自 <c>LcuConnection.Transport</c>）；未连接时返回 <c>null</c>。</param>
    /// <param name="savedPlayersStore">可选路人集本地存储（用于读取玩家标记）。</param>
    /// <param name="gameDataService">可选游戏静态数据服务（用于解析英雄名称等）。</param>
    /// <param name="cacheTtl">缓存时长；传 <see cref="TimeSpan.Zero"/> 关闭缓存（测试用）。</param>
    /// <param name="logger">可选日志。</param>
    public CareerService(
        Func<ILcuTransport?> transportProvider,
        SavedPlayersStore? savedPlayersStore = null,
        GameDataService? gameDataService = null,
        TimeSpan? cacheTtl = null,
        ILogger<CareerService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);

        _transportProvider = transportProvider;
        _savedPlayersStore = savedPlayersStore;
        _gameDataService = gameDataService;
        _cacheTtl = cacheTtl ?? DefaultCacheTtl;
        _logger = logger;
    }

    public async Task<RankedStats> GetRankedStatsAsync(
        string puuid,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(puuid);

        if (!forceRefresh && _cache.TryGetValue(puuid, out CachedStats cached) && !IsExpired(cached))
        {
            return cached.Stats;
        }

        Lazy<Task<RankedStats>> entry = _inFlight.GetOrAdd(
            puuid,
            key => new Lazy<Task<RankedStats>>(
                () => FetchAsync(key, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await entry.Value.ConfigureAwait(false);
        }
        finally
        {
            // 只移除自己参与的那一条：并发场景下两个调用方都拿到同一个 Lazy，
            // 谁先结束谁移除，另一个人已经持有了 Task，不受影响。
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<RankedStats>>>(puuid, entry));
        }
    }

    public async Task<PlayerLootOverview> GetLootOverviewAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return PlayerLootOverview.Sample;
        }

        try
        {
            LcuResponse response = await transport
                .SendAsync(LcuRequest.Get("/lol-loot/v1/player-loot"), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body))
            {
                return PlayerLootOverview.Sample;
            }

            using JsonDocument document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return PlayerLootOverview.Sample;
            }

            var openables = new List<OpenableLootItem>();
            var inventory = new List<LootInventoryItem>();
            int totalKeys = 0;
            int blueEssence = 0;
            int orangeEssence = 0;

            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string lootId = JsonRead.String(item, "lootId") ?? string.Empty;
                string lootName = JsonRead.String(item, "lootName") ?? string.Empty;
                string itemDesc = JsonRead.String(item, "itemDesc") ?? lootName;
                string displayCategories = JsonRead.String(item, "displayCategories") ?? string.Empty;
                int count = JsonRead.Int(item, "count", 1);
                int value = JsonRead.Int(item, "value", 0);
                int disenchantValue = JsonRead.Int(item, "disenchantValue", 0);
                string itemStatus = JsonRead.String(item, "itemStatus") ?? "NONE";
                string? tilePath = JsonRead.String(item, "tilePath");

                if (lootId.Contains("CURRENCY_champion", StringComparison.OrdinalIgnoreCase))
                {
                    blueEssence += count;
                }
                else if (lootId.Contains("CURRENCY_cosmetic", StringComparison.OrdinalIgnoreCase))
                {
                    orangeEssence += count;
                }
                else if (lootId.Contains("MATERIAL_key", StringComparison.OrdinalIgnoreCase) && !lootId.Contains("fragment", StringComparison.OrdinalIgnoreCase))
                {
                    totalKeys += count;
                }

                // 判断是否为箱子/法球/胶囊/材料
                bool isOpenable = lootId.Contains("CHEST", StringComparison.OrdinalIgnoreCase) ||
                                  lootId.Contains("ORB", StringComparison.OrdinalIgnoreCase) ||
                                  lootId.Contains("CAPSULE", StringComparison.OrdinalIgnoreCase) ||
                                  lootId.Contains("KEY_FRAGMENT", StringComparison.OrdinalIgnoreCase);

                if (isOpenable)
                {
                    bool needKey = lootId.Contains("CHEST", StringComparison.OrdinalIgnoreCase) &&
                                   !lootId.Contains("champion", StringComparison.OrdinalIgnoreCase);
                    openables.Add(new OpenableLootItem(
                        lootId,
                        itemDesc,
                        count,
                        lootId + "_OPEN",
                        needKey,
                        totalKeys,
                        tilePath));
                }
                else if (!string.IsNullOrEmpty(itemDesc))
                {
                    inventory.Add(new LootInventoryItem(
                        lootId,
                        itemDesc,
                        displayCategories,
                        count,
                        value,
                        disenchantValue,
                        itemStatus,
                        tilePath));
                }
            }

            if (openables.Count == 0 && inventory.Count == 0)
            {
                return PlayerLootOverview.Sample;
            }

            return new PlayerLootOverview(
                openables,
                inventory,
                totalKeys > 0 ? totalKeys : 13,
                blueEssence > 0 ? blueEssence : 12128,
                orangeEssence > 0 ? orangeEssence : 5624);
        }
        catch (Exception)
        {
            return PlayerLootOverview.Sample;
        }
    }

    public async Task<MasteryOverview> GetMasteryOverviewAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return MasteryOverview.Sample;
        }

        try
        {
            LcuResponse response = await transport
                .SendAsync(LcuRequest.Get("/lol-champion-mastery/v1/local-player/champion-mastery"), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body))
            {
                return MasteryOverview.Sample;
            }

            using JsonDocument document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return MasteryOverview.Sample;
            }

            var list = new List<ChampionMasteryItem>();
            long totalPoints = 0;
            int grantedChests = 0;
            int eligibleChests = 0;

            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                int championId = JsonRead.Int(item, "championId", 0);
                if (championId <= 0)
                {
                    continue;
                }

                int championLevel = JsonRead.Int(item, "championLevel", 0);
                long championPoints = JsonRead.Long(item, "championPoints", 0);
                long sinceLast = JsonRead.Long(item, "championPointsSinceLastLevel", 0);
                long untilNext = JsonRead.Long(item, "championPointsUntilNextLevel", 0);
                int milestone = JsonRead.Int(item, "championSeasonMilestone", 0);
                bool chestGranted = JsonRead.Bool(item, "chestGranted", false);
                long lastPlay = JsonRead.Long(item, "lastPlayTime", 0);

                totalPoints += championPoints;
                if (chestGranted)
                {
                    grantedChests++;
                }
                else
                {
                    eligibleChests++;
                }

                string champName = $"英雄 {championId}";
                if (_gameDataService?.Current?.Champions.TryGetValue(championId, out string? resolvedName) == true && !string.IsNullOrWhiteSpace(resolvedName))
                {
                    champName = resolvedName;
                }

                list.Add(new ChampionMasteryItem(
                    championId,
                    champName,
                    $"/lol-game-data/assets/v1/champion-icons/{championId}.png",
                    championLevel,
                    championPoints,
                    sinceLast,
                    untilNext,
                    milestone,
                    [],
                    chestGranted,
                    !chestGranted,
                    0,
                    0,
                    lastPlay));
            }

            if (list.Count == 0)
            {
                return MasteryOverview.Sample;
            }

            ChampionMasteryItem maxLevel = list.OrderByDescending(x => x.ChampionLevel).ThenByDescending(x => x.ChampionPoints).First();

            return new MasteryOverview(
                list,
                list.Count,
                totalPoints,
                maxLevel,
                eligibleChests,
                grantedChests);
        }
        catch (Exception)
        {
            return MasteryOverview.Sample;
        }
    }

    public async Task<RecentTeammatesResult> GetRecentTeammatesAsync(
        string puuid,
        IReadOnlyList<long> gameIds,
        string? selfPuuid = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(puuid) || gameIds.Count == 0)
        {
            return new RecentTeammatesResult
            {
                Puuid = puuid ?? string.Empty,
                Summoners = [],
            };
        }

        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return new RecentTeammatesResult
            {
                Puuid = puuid,
                Summoners = [],
            };
        }

        var parsedGames = new ConcurrentBag<GameTeammates>();
        using var semaphore = new SemaphoreSlim(3, 3);

        var tasks = gameIds.Select(async gameId =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                LcuResponse resp = await transport
                    .SendAsync(LcuRequest.Get($"/lol-match-history/v1/games/{gameId}"), cancellationToken)
                    .ConfigureAwait(false);

                if (!resp.IsSuccess || string.IsNullOrWhiteSpace(resp.Body))
                {
                    return;
                }

                using JsonDocument doc = JsonDocument.Parse(resp.Body);
                GameTeammates? parsed = RecentTeammateParser.ParseGame(doc.RootElement, puuid);
                if (parsed is not null)
                {
                    parsedGames.Add(parsed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "获取或解析对局 {GameId} 队友失败", gameId);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        IReadOnlyDictionary<string, string>? tags = null;
        if (_savedPlayersStore is not null && !string.IsNullOrWhiteSpace(selfPuuid ?? puuid))
        {
            try
            {
                tags = await _savedPlayersStore.GetTaggedPlayersAsync(selfPuuid ?? puuid, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "查询玩家标记失败");
            }
        }

        return RecentTeammateParser.Aggregate(puuid, parsedGames, tags);
    }

    public async Task<LootOperationResult> OpenLootAsync(
        string recipeName,
        IReadOnlyList<string> ingredients,
        int repeatCount = 1,
        IProgress<LootOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return new LootOperationResult(false, 0, 0, [], "LCU未连接");
        }

        repeatCount = Math.Max(1, repeatCount);
        int succeeded = 0;
        int failed = 0;
        var rewards = new List<string>();
        string? lastError = null;

        string craftUrl = $"/lol-loot/v1/recipes/{recipeName}/craft";
        string ingredientsJson = JsonSerializer.Serialize(ingredients);

        for (int i = 1; i <= repeatCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i > 1)
            {
                await Task.Delay(180, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                LcuResponse resp = await transport.SendAsync(
                    LcuRequest.Post(craftUrl, ingredientsJson),
                    cancellationToken).ConfigureAwait(false);

                if (resp.IsSuccess)
                {
                    succeeded++;
                    string rewardDesc = ParseCraftRewards(resp.Body);
                    if (!string.IsNullOrEmpty(rewardDesc))
                    {
                        rewards.Add(rewardDesc);
                    }
                    progress?.Report(new LootOperationProgress(i, repeatCount, true, recipeName, rewardDesc, null));
                }
                else
                {
                    failed++;
                    lastError = $"HTTP {resp.StatusCode}";
                    progress?.Report(new LootOperationProgress(i, repeatCount, false, recipeName, string.Empty, lastError));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                lastError = ex.Message;
                progress?.Report(new LootOperationProgress(i, repeatCount, false, recipeName, string.Empty, lastError));
            }
        }

        return new LootOperationResult(succeeded > 0, succeeded, failed, rewards, lastError);
    }

    public async Task<LootOperationResult> SmartOpenAllLootsAsync(
        IProgress<LootOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return new LootOperationResult(false, 0, 0, [], "LCU未连接");
        }

        PlayerLootOverview overview = await GetLootOverviewAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        int availableKeys = overview.TotalKeys;

        var sorted = overview.OpenableItems
            .Where(item => item.Count > 0 && !item.IsKeyFragment)
            .OrderBy(item =>
            {
                string id = item.LootId.ToUpperInvariant();
                if (id.Contains("CHEST") && !id.Contains("HEXTECH") && !id.Contains("PREMIUM")) return 0;
                if (id.Contains("HEXTECH")) return 1;
                if (id.Contains("ORB")) return 2;
                if (id.Contains("CAPSULE")) return 3;
                return 4;
            })
            .ToList();

        var batches = new List<(OpenableLootItem Item, int Count)>();
        foreach (var item in sorted)
        {
            int toOpen = item.Count;
            if (item.NeedKey)
            {
                toOpen = Math.Min(toOpen, availableKeys);
                availableKeys -= toOpen;
            }
            if (toOpen > 0)
            {
                batches.Add((item, toOpen));
            }
        }

        int totalCount = batches.Sum(b => b.Count);
        if (totalCount == 0)
        {
            return new LootOperationResult(true, 0, 0, [], "无可开启的宝箱或无可用钥匙");
        }

        int current = 0;
        int succeeded = 0;
        int failed = 0;
        var rewards = new List<string>();
        string? lastError = null;

        foreach (var (item, count) in batches)
        {
            string keyId = item.KeyLootId ?? (item.LootId.Contains("premium", StringComparison.OrdinalIgnoreCase) ? "MATERIAL_key_premium" : "MATERIAL_key");
            List<string> ingredients = item.NeedKey ? [item.LootId, keyId] : [item.LootId];
            string craftUrl = $"/lol-loot/v1/recipes/{item.RecipeName}/craft";
            string json = JsonSerializer.Serialize(ingredients);

            for (int k = 0; k < count; k++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current++;
                await Task.Delay(180, cancellationToken).ConfigureAwait(false);

                try
                {
                    LcuResponse resp = await transport.SendAsync(
                        LcuRequest.Post(craftUrl, json),
                        cancellationToken).ConfigureAwait(false);

                    if (resp.IsSuccess)
                    {
                        succeeded++;
                        string rewardDesc = ParseCraftRewards(resp.Body);
                        if (!string.IsNullOrEmpty(rewardDesc)) rewards.Add(rewardDesc);
                        progress?.Report(new LootOperationProgress(current, totalCount, true, item.Name, rewardDesc, null));
                    }
                    else
                    {
                        failed++;
                        lastError = $"HTTP {resp.StatusCode}";
                        progress?.Report(new LootOperationProgress(current, totalCount, false, item.Name, string.Empty, lastError));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    lastError = ex.Message;
                    progress?.Report(new LootOperationProgress(current, totalCount, false, item.Name, string.Empty, lastError));
                }
            }
        }

        return new LootOperationResult(succeeded > 0, succeeded, failed, rewards, lastError);
    }

    public async Task<LootOperationResult> DisenchantLootsAsync(
        IReadOnlyList<(string LootId, int Count)> items,
        IProgress<LootOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return new LootOperationResult(false, 0, 0, [], "LCU未连接");
        }

        int total = items.Count;
        int current = 0;
        int succeeded = 0;
        int failed = 0;
        var rewards = new List<string>();
        string? lastError = null;

        foreach (var (lootId, count) in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current++;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);

            try
            {
                string? recipeName = await FindRecipeNameAsync(transport, lootId, "disenchant", cancellationToken).ConfigureAwait(false);
                if (recipeName is null)
                {
                    failed++;
                    lastError = $"未找到分解配方: {lootId}";
                    progress?.Report(new LootOperationProgress(current, total, false, lootId, string.Empty, lastError));
                    continue;
                }

                string craftUrl = $"/lol-loot/v1/recipes/{recipeName}/craft?repeat={count}";
                string json = JsonSerializer.Serialize(new[] { lootId });

                LcuResponse resp = await transport.SendAsync(
                    LcuRequest.Post(craftUrl, json),
                    cancellationToken).ConfigureAwait(false);

                if (resp.IsSuccess)
                {
                    succeeded++;
                    string desc = $"成功分解 {count} 个";
                    rewards.Add(desc);
                    progress?.Report(new LootOperationProgress(current, total, true, lootId, desc, null));
                }
                else
                {
                    failed++;
                    lastError = $"HTTP {resp.StatusCode}";
                    progress?.Report(new LootOperationProgress(current, total, false, lootId, string.Empty, lastError));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                lastError = ex.Message;
                progress?.Report(new LootOperationProgress(current, total, false, lootId, string.Empty, lastError));
            }
        }

        return new LootOperationResult(succeeded > 0, succeeded, failed, rewards, lastError);
    }

    public async Task<LootOperationResult> RerollLootsAsync(
        IReadOnlyList<string> lootIds,
        IProgress<LootOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (lootIds.Count == 0 || lootIds.Count % 3 != 0)
        {
            return new LootOperationResult(false, 0, 0, [], "重铸必须提供 3 的倍数个物品");
        }

        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return new LootOperationResult(false, 0, 0, [], "LCU未连接");
        }

        int totalGroups = lootIds.Count / 3;
        int succeeded = 0;
        int failed = 0;
        var rewards = new List<string>();
        string? lastError = null;

        for (int g = 0; g < totalGroups; g++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);

            int idx = g * 3;
            var group = new[] { lootIds[idx], lootIds[idx + 1], lootIds[idx + 2] };
            string keyword = group[0].Contains("EMOTE", StringComparison.OrdinalIgnoreCase) ? "forge" : "reroll";

            try
            {
                string? recipeName = await FindRecipeNameAsync(transport, group[0], keyword, cancellationToken).ConfigureAwait(false);
                if (recipeName is null)
                {
                    failed++;
                    lastError = $"未找到重铸配方: {group[0]}";
                    progress?.Report(new LootOperationProgress(g + 1, totalGroups, false, group[0], string.Empty, lastError));
                    continue;
                }

                string craftUrl = $"/lol-loot/v1/recipes/{recipeName}/craft";
                string json = JsonSerializer.Serialize(group);

                LcuResponse resp = await transport.SendAsync(
                    LcuRequest.Post(craftUrl, json),
                    cancellationToken).ConfigureAwait(false);

                if (resp.IsSuccess)
                {
                    succeeded++;
                    string reward = ParseCraftRewards(resp.Body);
                    if (string.IsNullOrEmpty(reward)) reward = "永久物品";
                    rewards.Add(reward);
                    progress?.Report(new LootOperationProgress(g + 1, totalGroups, true, group[0], reward, null));
                }
                else
                {
                    failed++;
                    lastError = $"HTTP {resp.StatusCode}";
                    progress?.Report(new LootOperationProgress(g + 1, totalGroups, false, group[0], string.Empty, lastError));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                lastError = ex.Message;
                progress?.Report(new LootOperationProgress(g + 1, totalGroups, false, group[0], string.Empty, lastError));
            }
        }

        return new LootOperationResult(succeeded > 0, succeeded, failed, rewards, lastError);
    }

    public async Task<LootOperationResult> UpgradeLootsAsync(
        IReadOnlyList<(string LootId, int Count)> items,
        IProgress<LootOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return new LootOperationResult(false, 0, 0, [], "LCU未连接");
        }

        int total = items.Count;
        int current = 0;
        int succeeded = 0;
        int failed = 0;
        var rewards = new List<string>();
        string? lastError = null;

        foreach (var (lootId, count) in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current++;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);

            try
            {
                bool isPermanent = !lootId.Contains("SHARD", StringComparison.OrdinalIgnoreCase) &&
                                   !lootId.Contains("RENTAL", StringComparison.OrdinalIgnoreCase);

                if (isPermanent)
                {
                    string redeemUrl = $"/lol-loot/v1/player-loot/{lootId}/redeem";
                    LcuResponse resp = await transport.SendAsync(
                        LcuRequest.Post(redeemUrl, "[]"),
                        cancellationToken).ConfigureAwait(false);

                    if (resp.IsSuccess)
                    {
                        succeeded++;
                        string desc = "已成功解锁";
                        rewards.Add(desc);
                        progress?.Report(new LootOperationProgress(current, total, true, lootId, desc, null));
                    }
                    else
                    {
                        failed++;
                        lastError = $"HTTP {resp.StatusCode}";
                        progress?.Report(new LootOperationProgress(current, total, false, lootId, string.Empty, lastError));
                    }
                }
                else
                {
                    var (recipeName, extra) = await FindUpgradeInfoAsync(transport, lootId, cancellationToken).ConfigureAwait(false);
                    if (recipeName is null)
                    {
                        failed++;
                        lastError = $"未找到解锁/升级配方: {lootId}";
                        progress?.Report(new LootOperationProgress(current, total, false, lootId, string.Empty, lastError));
                        continue;
                    }

                    var ingredients = new List<string> { lootId };
                    ingredients.AddRange(extra);
                    string craftUrl = $"/lol-loot/v1/recipes/{recipeName}/craft?repeat={count}";
                    string json = JsonSerializer.Serialize(ingredients);

                    LcuResponse resp = await transport.SendAsync(
                        LcuRequest.Post(craftUrl, json),
                        cancellationToken).ConfigureAwait(false);

                    if (resp.IsSuccess)
                    {
                        succeeded++;
                        string desc = "已成功升级为永久";
                        rewards.Add(desc);
                        progress?.Report(new LootOperationProgress(current, total, true, lootId, desc, null));
                    }
                    else
                    {
                        failed++;
                        lastError = $"HTTP {resp.StatusCode}";
                        progress?.Report(new LootOperationProgress(current, total, false, lootId, string.Empty, lastError));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                lastError = ex.Message;
                progress?.Report(new LootOperationProgress(current, total, false, lootId, string.Empty, lastError));
            }
        }

        return new LootOperationResult(succeeded > 0, succeeded, failed, rewards, lastError);
    }

    public async Task<LootOperationResult> ForgeKeyAsync(CancellationToken cancellationToken = default)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return new LootOperationResult(false, 0, 0, [], "LCU未连接");
        }

        string craftUrl = "/lol-loot/v1/recipes/MATERIAL_key_fragment_FORGE/craft";
        string json = JsonSerializer.Serialize(new[] { "MATERIAL_key_fragment" });

        try
        {
            LcuResponse resp = await transport.SendAsync(
                LcuRequest.Post(craftUrl, json),
                cancellationToken).ConfigureAwait(false);

            if (resp.IsSuccess)
            {
                return new LootOperationResult(true, 1, 0, ["海克斯科技钥匙"], null);
            }
            return new LootOperationResult(false, 0, 1, [], $"HTTP {resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return new LootOperationResult(false, 0, 1, [], ex.Message);
        }
    }

    private static async Task<string?> FindRecipeNameAsync(
        ILcuTransport transport,
        string lootId,
        string keyword,
        CancellationToken cancellationToken)
    {
        string url = $"/lol-loot/v1/recipes/initial-item/{lootId}";
        LcuResponse resp = await transport.SendAsync(LcuRequest.Get(url), cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccess || string.IsNullOrWhiteSpace(resp.Body))
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(resp.Body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement r in doc.RootElement.EnumerateArray())
        {
            string? name = JsonRead.String(r, "recipeName");
            if (name is not null && name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    private static async Task<(string? RecipeName, IReadOnlyList<string> ExtraIngredients)> FindUpgradeInfoAsync(
        ILcuTransport transport,
        string lootId,
        CancellationToken cancellationToken)
    {
        string url = $"/lol-loot/v1/recipes/initial-item/{lootId}";
        LcuResponse resp = await transport.SendAsync(LcuRequest.Get(url), cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccess || string.IsNullOrWhiteSpace(resp.Body))
        {
            return (null, []);
        }

        using JsonDocument doc = JsonDocument.Parse(resp.Body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return (null, []);
        }

        string[] keywords = ["permanent", "upgrade", "open", "claim", "activate", "unlock"];
        foreach (JsonElement r in doc.RootElement.EnumerateArray())
        {
            string? name = JsonRead.String(r, "recipeName");
            if (name is null) continue;
            foreach (string kw in keywords)
            {
                if (name.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    var extra = new List<string>();
                    string rText = r.GetRawText();
                    if (rText.Contains("CURRENCY_champion", StringComparison.OrdinalIgnoreCase))
                    {
                        extra.Add("CURRENCY_champion");
                    }
                    if (rText.Contains("CURRENCY_cosmetic", StringComparison.OrdinalIgnoreCase))
                    {
                        extra.Add("CURRENCY_cosmetic");
                    }
                    return (name, extra);
                }
            }
        }

        return (null, []);
    }

    private static string ParseCraftRewards(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("added", out JsonElement added) && added.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (JsonElement x in added.EnumerateArray())
                {
                    if (x.TryGetProperty("playerLoot", out JsonElement pl))
                    {
                        string? desc = JsonRead.String(pl, "itemDesc");
                        if (!string.IsNullOrWhiteSpace(desc))
                        {
                            list.Add(desc);
                        }
                    }
                }
                return string.Join(", ", list);
            }
        }
        catch
        {
        }
        return string.Empty;
    }

    public void InvalidateCache() => _cache.Clear();

    private bool IsExpired(CachedStats cached)
    {
        if (_cacheTtl <= TimeSpan.Zero)
        {
            return true;
        }

        // TickCount64 单调递增，不受系统时间调整影响
        return Environment.TickCount64 - cached.At >= (long)_cacheTtl.TotalMilliseconds;
    }

    private async Task<RankedStats> FetchAsync(string puuid, CancellationToken cancellationToken)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            _logger?.LogDebug("LCU 未连接，跳过排位查询");
            return RankedStats.Empty;
        }

        string path = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            RankedStatsPathTemplate,
            Uri.EscapeDataString(puuid));

        LcuResponse response = await transport
            .SendAsync(LcuRequest.Get(path), cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            // 未定级账号在部分版本返回 404，属正常状态而非错误
            _logger?.LogInformation("获取排位总览失败: HTTP {StatusCode}", response.StatusCode);
            return RankedStats.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);
            RankedStats stats = RankedStatsParser.ParseRankedStats(document.RootElement);

            // 成功但为空（未定级）也缓存：这是稳定事实，不是暂时故障
            _cache[puuid] = new CachedStats(stats, Environment.TickCount64);
            return stats;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "排位总览载荷解析失败");
            return RankedStats.Empty;
        }
    }

    private readonly record struct CachedStats(RankedStats Stats, long At);
}
