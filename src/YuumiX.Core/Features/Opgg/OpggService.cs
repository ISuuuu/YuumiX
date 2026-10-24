using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Common;
using YuumiX.Core.Features.Opgg.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Features.Opgg;

/// <summary>
/// OP.GG 业务服务实现：路由请求、双级缓存、LCU 符文/出装写入与选人联动。
/// </summary>
public sealed class OpggService : IOpggService
{
    private const string BaseApiUrl = "https://lol-api-champion.op.gg/api";

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly OpggHttpClient _httpClient;
    private readonly ILogger<OpggService>? _logger;
    private readonly EventStream<OpggBuildEvent> _buildEvents = new();

    public OpggService(
        Func<ILcuTransport?> transportProvider,
        OpggHttpClient httpClient,
        ILogger<OpggService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);
        ArgumentNullException.ThrowIfNull(httpClient);

        _transportProvider = transportProvider;
        _httpClient = httpClient;
        _logger = logger;
    }

    public IObservable<OpggBuildEvent> ChampSelectBuildEvents => _buildEvents;

    public void PublishChampSelectBuild(OpggBuildEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _logger?.LogInformation("发布 OP.GG 选人出装事件: ChampionId={ChampId}, Mode={Mode}, Pos={Pos}",
            e.ChampionId, e.Mode, e.Position);
        _buildEvents.Publish(e);
    }

    /// <summary>
    /// 构建梯队列表请求 URL。
    /// </summary>
    public static string BuildTierListUrl(string region, string mode)
    {
        return $"{BaseApiUrl}/{region}/champions/{mode}";
    }

    /// <summary>
    /// 构建单英雄出装请求 URL。
    /// </summary>
    public static string BuildChampionBuildUrl(string region, string mode, int championId, string? position)
    {
        if (string.Equals(mode, OpggMode.Arena, StringComparison.OrdinalIgnoreCase))
        {
            return $"{BaseApiUrl}/{region}/champions/{championId}";
        }

        if (string.Equals(mode, OpggMode.Ranked, StringComparison.OrdinalIgnoreCase))
        {
            var pos = string.IsNullOrWhiteSpace(position) ? OpggPosition.Mid : position;
            return $"{BaseApiUrl}/{region}/champions/{mode}/{championId}/{pos}";
        }

        // normal, aram, etc.
        return $"{BaseApiUrl}/{region}/champions/{mode}/{championId}/none";
    }

    public async Task<IReadOnlyList<TierListItem>> GetTierListAsync(
        string region,
        string mode,
        string tier,
        string? position = null,
        CancellationToken cancellationToken = default)
    {
        var url = BuildTierListUrl(region, mode);
        var cacheKey = OpggHttpClient.BuildCacheKey(region, mode, tier, null, position);
        var queryParams = new[] { new KeyValuePair<string, string>("tier", tier) };

        var json = await _httpClient.GetJsonAsync(url, queryParams, cacheKey, cancellationToken).ConfigureAwait(false);
        return OpggParser.ParseTierList(json, mode, position);
    }

    public async Task<OpggBuildPayload?> GetBuildAsync(
        string region,
        string mode,
        string tier,
        int championId,
        string? position = null,
        CancellationToken cancellationToken = default)
    {
        var url = BuildChampionBuildUrl(region, mode, championId, position);
        var cacheKey = OpggHttpClient.BuildCacheKey(region, mode, tier, championId, position);
        var queryParams = new[] { new KeyValuePair<string, string>("tier", tier) };

        var json = await _httpClient.GetJsonAsync(url, queryParams, cacheKey, cancellationToken).ConfigureAwait(false);
        return OpggParser.ParseBuildPayload(json);
    }

    public async Task<string> ApplyRunePageAsync(
        string name,
        int primaryStyleId,
        int subStyleId,
        IReadOnlyList<int> selectedPerkIds,
        CancellationToken cancellationToken = default)
    {
        var transport = _transportProvider();
        if (transport is null)
        {
            throw new InvalidOperationException("英雄联盟客户端未连接");
        }

        // 1. 获取当前符文页，如果可删除则保存快照并先删除腾出槽位
        JsonObject? oldPageSnapshot = null;
        try
        {
            var curResp = await transport.SendAsync(
                LcuRequest.Get("/lol-perks/v1/currentpage"),
                cancellationToken).ConfigureAwait(false);

            if (curResp.IsSuccess && !string.IsNullOrWhiteSpace(curResp.Body))
            {
                var curObj = JsonNode.Parse(curResp.Body) as JsonObject;
                var isDeletable = curObj?["isDeletable"]?.GetValue<bool>() ?? false;
                var pageId = curObj?["id"]?.GetValue<long>() ?? 0;

                if (isDeletable && pageId > 0)
                {
                    var pageResp = await transport.SendAsync(
                        LcuRequest.Get($"/lol-perks/v1/pages/{pageId}"),
                        cancellationToken).ConfigureAwait(false);

                    if (pageResp.IsSuccess && !string.IsNullOrWhiteSpace(pageResp.Body))
                    {
                        oldPageSnapshot = JsonNode.Parse(pageResp.Body) as JsonObject;
                    }

                    // 删除可删除页
                    await transport.SendAsync(
                        LcuRequest.Delete($"/lol-perks/v1/pages/{pageId}"),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "检查当前符文页异常，继续尝试创建新页");
        }

        // 2. 创建新符文页
        var newPageBody = new JsonObject
        {
            ["name"] = name,
            ["primaryStyleId"] = primaryStyleId,
            ["subStyleId"] = subStyleId,
            ["selectedPerkIds"] = new JsonArray(selectedPerkIds.Select(id => (JsonNode)id).ToArray()),
            ["current"] = true
        };

        var postResp = await transport.SendAsync(
            LcuRequest.Post("/lol-perks/v1/pages", newPageBody.ToJsonString()),
            cancellationToken).ConfigureAwait(false);

        if (!postResp.IsSuccess)
        {
            _logger?.LogWarning("创建新符文页失败: HTTP {Status}, Body={Body}", postResp.StatusCode, postResp.Body);

            // 失败回滚原符文页
            if (oldPageSnapshot is not null)
            {
                try
                {
                    oldPageSnapshot.Remove("id");
                    oldPageSnapshot.Remove("current");

                    _logger?.LogInformation("尝试恢复被删除的原符文页...");
                    await transport.SendAsync(
                        LcuRequest.Post("/lol-perks/v1/pages", oldPageSnapshot.ToJsonString()),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception rbEx)
                {
                    _logger?.LogError(rbEx, "恢复原符文页失败");
                }
            }

            throw new InvalidOperationException($"应用符文页失败 (HTTP {postResp.StatusCode}): {postResp.Body}");
        }

        _logger?.LogInformation("符文页已成功应用: {Name}", name);
        return "符文页已应用";
    }

    public async Task<string> ApplyItemSetAsync(
        int championId,
        string champName,
        string? position,
        OpggBuildData buildData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildData);

        var transport = _transportProvider();
        if (transport is null)
        {
            throw new InvalidOperationException("英雄联盟客户端未连接");
        }

        // 1. 构建 3 个装备块
        var seenStarter = new HashSet<int>();
        var starterItems = new List<JsonObject>();
        if (buildData.StarterItems is not null)
        {
            foreach (var set in buildData.StarterItems.Take(2))
            {
                foreach (var id in set.Ids)
                {
                    if (id > 0 && seenStarter.Add(id))
                    {
                        starterItems.Add(new JsonObject { ["id"] = id.ToString(), ["count"] = 1 });
                    }
                }
            }
        }

        var seenCore = new HashSet<int>();
        var coreItems = new List<JsonObject>();
        if (buildData.CoreItems is not null)
        {
            foreach (var set in buildData.CoreItems.Take(2))
            {
                foreach (var id in set.Ids)
                {
                    if (id > 0 && seenCore.Add(id))
                    {
                        coreItems.Add(new JsonObject { ["id"] = id.ToString(), ["count"] = 1 });
                    }
                }
            }
        }
        if (buildData.Boots is not null)
        {
            foreach (var set in buildData.Boots.Take(2))
            {
                foreach (var id in set.Ids)
                {
                    if (id > 0 && seenCore.Add(id))
                    {
                        coreItems.Add(new JsonObject { ["id"] = id.ToString(), ["count"] = 1 });
                    }
                }
            }
        }

        var seenLast = new HashSet<int>();
        var lastItems = new List<JsonObject>();
        if (buildData.LastItems is not null)
        {
            foreach (var set in buildData.LastItems.Take(10))
            {
                foreach (var id in set.Ids)
                {
                    if (id > 0 && !seenCore.Contains(id) && seenLast.Add(id))
                    {
                        lastItems.Add(new JsonObject { ["id"] = id.ToString(), ["count"] = 1 });
                    }
                }
            }
        }

        var blocks = new JsonArray();
        if (starterItems.Count > 0)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "出门装备",
                ["items"] = new JsonArray(starterItems.ToArray())
            });
        }
        if (coreItems.Count > 0)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "核心装备",
                ["items"] = new JsonArray(coreItems.ToArray())
            });
        }
        if (lastItems.Count > 0)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "可选与推荐装备",
                ["items"] = new JsonArray(lastItems.ToArray())
            });
        }

        if (blocks.Count == 0)
        {
            return "当前英雄暂无推荐出装数据";
        }

        var title = $"[Yuumi] {champName}{(string.IsNullOrWhiteSpace(position) ? "" : $" - {position}")}";

        // 2. 获取当前召唤师 ID 与 Account ID
        var sumResp = await transport.SendAsync(
            LcuRequest.Get("/lol-summoner/v1/current-summoner"),
            cancellationToken).ConfigureAwait(false);

        if (!sumResp.IsSuccess || string.IsNullOrWhiteSpace(sumResp.Body))
        {
            throw new InvalidOperationException("无法获取当前召唤师信息");
        }

        var sumObj = JsonNode.Parse(sumResp.Body) as JsonObject;
        long summonerId = 0;
        if (sumObj?.TryGetPropertyValue("summonerId", out var sIdNode) == true && sIdNode is not null)
        {
            summonerId = sIdNode.GetValue<long>();
        }
        else if (sumObj?.TryGetPropertyValue("id", out var idNode) == true && idNode is not null)
        {
            summonerId = idNode.GetValue<long>();
        }

        if (summonerId == 0)
        {
            throw new InvalidOperationException("解析当前召唤师 ID 失败");
        }

        long accountId = summonerId;
        if (sumObj?.TryGetPropertyValue("accountId", out var aIdNode) == true && aIdNode is not null)
        {
            accountId = aIdNode.GetValue<long>();
        }

        // 3. 拉取现有装备集并做防覆盖过滤
        var setsPath = $"/lol-item-sets/v1/item-sets/{summonerId}/sets";
        var existingResp = await transport.SendAsync(LcuRequest.Get(setsPath), cancellationToken).ConfigureAwait(false);

        JsonObject root;
        if (existingResp.IsSuccess && !string.IsNullOrWhiteSpace(existingResp.Body) &&
            JsonNode.Parse(existingResp.Body) is JsonObject parsedRoot)
        {
            root = parsedRoot;
        }
        else
        {
            root = new JsonObject
            {
                ["accountId"] = accountId,
                ["itemSets"] = new JsonArray(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        // 过滤非 [Yuumi] 的装备集
        var keptSets = new List<JsonNode>();
        if (root.TryGetPropertyValue("itemSets", out var itemSetsNode) && itemSetsNode is JsonArray arr)
        {
            foreach (var set in arr)
            {
                var sTitle = set?["title"]?.GetValue<string>() ?? "";
                if (!sTitle.StartsWith("[Yuumi]", StringComparison.Ordinal))
                {
                    keptSets.Add(set!.DeepClone());
                }
            }
        }

        // 追加新装备集
        var newSet = new JsonObject
        {
            ["uid"] = Guid.NewGuid().ToString(),
            ["title"] = title,
            ["associatedChampions"] = new JsonArray((JsonNode)championId),
            ["associatedMaps"] = new JsonArray((JsonNode)11, (JsonNode)12),
            ["blocks"] = blocks,
            ["map"] = "any",
            ["mode"] = "any",
            ["preferredItemSlots"] = new JsonArray(),
            ["sortrank"] = 1,
            ["startedFrom"] = "blank",
            ["type"] = "custom"
        };
        keptSets.Add(newSet);

        root["itemSets"] = new JsonArray(keptSets.ToArray());
        root["accountId"] ??= accountId;
        root["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 4. PUT 持久化写入
        var putResp = await transport.SendAsync(
            LcuRequest.Put(setsPath, root.ToJsonString()),
            cancellationToken).ConfigureAwait(false);

        if (!putResp.IsSuccess)
        {
            _logger?.LogWarning("PUT 装备集失败: HTTP {Status}, Body={Body}", putResp.StatusCode, putResp.Body);
            throw new InvalidOperationException($"持久化装备集失败 (HTTP {putResp.StatusCode}): {putResp.Body}");
        }

        _logger?.LogInformation("成功持久化装备配置页至 LCU (PUT {SetsPath}), title: {Title}", setsPath, title);
        return $"装备页应用成功: {title}";
    }
}
