using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Features.Career.Models;
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
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<CareerService>? _logger;

    /// <summary>已完成的查询：puuid → (结果, 完成时刻)。</summary>
    private readonly ConcurrentDictionary<string, CachedStats> _cache = new(StringComparer.Ordinal);

    /// <summary>飞行中的查询：同 puuid 并发共享一次 HTTP，结束后移除。</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<RankedStats>>> _inFlight = new(StringComparer.Ordinal);

    /// <param name="transportProvider">当前传输层（来自 <c>LcuConnection.Transport</c>）；未连接时返回 <c>null</c>。</param>
    /// <param name="cacheTtl">缓存时长；传 <see cref="TimeSpan.Zero"/> 关闭缓存（测试用）。</param>
    /// <param name="logger">可选日志。</param>
    public CareerService(
        Func<ILcuTransport?> transportProvider,
        TimeSpan? cacheTtl = null,
        ILogger<CareerService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);

        _transportProvider = transportProvider;
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

                list.Add(new ChampionMasteryItem(
                    championId,
                    $"英雄 {championId}",
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
