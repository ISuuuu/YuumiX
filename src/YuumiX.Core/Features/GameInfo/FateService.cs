using System.Text.Json;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 宿命检测解析器（纯函数）。
/// 对应旧版 <c>game_info.rs</c> 的 <c>inspect_game_fate</c>。
/// </summary>
public static class FateParser
{
    /// <summary>
    /// 检查单场对局中目标玩家与当前玩家的关系。
    /// 自身不判定关系（返回 null）。
    /// </summary>
    public static (string Flag, int? ChampionId, long? GameCreation)? InspectGameFate(
        JsonElement detail,
        string targetPuuid,
        long currentSummonerId)
    {
        if (detail.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int queueId = JsonRead.Int(detail, "queueId", 0);

        if (!detail.TryGetProperty("participants", out var participants) || participants.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        if (!detail.TryGetProperty("participantIdentities", out var identities) || identities.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        long? currentPid = null;
        long? targetPid = null;

        foreach (var ident in identities.EnumerateArray())
        {
            if (!ident.TryGetProperty("player", out var player) || player.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string puuid = JsonRead.String(player, "puuid") ?? string.Empty;
            long summonerId = JsonRead.Long(player, "summonerId", 0);
            long pid = JsonRead.Long(ident, "participantId", -1);
            if (pid < 0)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(targetPuuid) && string.Equals(puuid, targetPuuid, StringComparison.OrdinalIgnoreCase))
            {
                targetPid = pid;
            }

            if (currentSummonerId != 0 && summonerId == currentSummonerId)
            {
                currentPid = pid;
            }
        }

        if (!currentPid.HasValue || !targetPid.HasValue)
        {
            return null;
        }

        // 自身不判定关系
        if (currentPid.Value == targetPid.Value)
        {
            return null;
        }

        int? targetChampionId = null;
        long? currentTeam = null;
        long? targetTeam = null;

        foreach (var p in participants.EnumerateArray())
        {
            long pid = JsonRead.Long(p, "participantId", -1);
            if (pid < 0)
            {
                continue;
            }

            long? teamVal = null;
            if (queueId == 1700)
            {
                if (p.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
                {
                    long subteam = JsonRead.Long(stats, "subteamPlacement", -1);
                    if (subteam >= 0)
                    {
                        teamVal = subteam;
                    }
                }
            }
            else
            {
                long tid = JsonRead.Long(p, "teamId", -1);
                if (tid >= 0)
                {
                    teamVal = tid;
                }
            }

            if (pid == currentPid.Value)
            {
                currentTeam = teamVal;
            }

            if (pid == targetPid.Value)
            {
                targetTeam = teamVal;
                int cid = JsonRead.Int(p, "championId", 0);
                if (cid > 0)
                {
                    targetChampionId = cid;
                }
            }
        }

        if (!currentTeam.HasValue || !targetTeam.HasValue)
        {
            return null;
        }

        string flag = currentTeam.Value == targetTeam.Value ? "ally" : "enemy";
        long? gameCreation = null;
        if (detail.TryGetProperty("gameCreation", out var gcProp) && gcProp.TryGetInt64(out long gc))
        {
            gameCreation = gc;
        }

        return (flag, targetChampionId, gameCreation);
    }
}

/// <summary>
/// 宿命检测服务实现，对应原 <c>game_info.rs</c>。
/// 包含 32 容量详情缓存、并发拉取与优先级判定。
/// </summary>
public sealed class FateService : IFateService
{
    private const int GameDetailCacheCap = 32;

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Func<GameDataCatalog?>? _gameDataProvider;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<long, JsonElement> _detailCache = new();

    public FateService(
        Func<ILcuTransport?> transportProvider,
        Func<GameDataCatalog?>? gameDataProvider = null)
    {
        _transportProvider = transportProvider;
        _gameDataProvider = gameDataProvider;
    }

    public async Task<PlayerFateInfo> GetPlayerFateInfoAsync(
        IReadOnlyList<long> gameIds,
        string targetPuuid,
        long currentSummonerId,
        CancellationToken cancellationToken = default)
    {
        if (gameIds is null || gameIds.Count == 0 || string.IsNullOrEmpty(targetPuuid) || currentSummonerId == 0)
        {
            return PlayerFateInfo.Empty;
        }

        // 去重并保留传入顺序（越靠前优先级越高）
        var candidateGameIds = new List<long>();
        foreach (long id in gameIds)
        {
            if (id > 0 && !candidateGameIds.Contains(id))
            {
                candidateGameIds.Add(id);
            }
        }

        if (candidateGameIds.Count == 0)
        {
            return PlayerFateInfo.Empty;
        }

        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return PlayerFateInfo.Empty;
        }

        // 并发拉取候选对局详情
        var tasks = candidateGameIds.Select(async gid =>
        {
            JsonElement? detail = GetCachedDetail(gid);
            if (!detail.HasValue)
            {
                try
                {
                    LcuResponse response = await transport.SendAsync(
                        LcuRequest.Get($"/lol-match-history/v1/games/{gid}"),
                        cancellationToken).ConfigureAwait(false);

                    if (response.IsSuccess && !string.IsNullOrWhiteSpace(response.Body))
                    {
                        using var doc = JsonDocument.Parse(response.Body);
                        detail = doc.RootElement.Clone();
                        InsertCachedDetail(gid, detail.Value);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return null;
                }
            }

            if (detail.HasValue)
            {
                return FateParser.InspectGameFate(detail.Value, targetPuuid, currentSummonerId);
            }

            return null;
        }).ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        string? firstFlag = null;
        int? firstCid = null;
        long? firstGameId = null;
        long? firstGameCreation = null;
        int allyCount = 0;
        int enemyCount = 0;

        for (int i = 0; i < results.Length; i++)
        {
            var res = results[i];
            if (res.HasValue)
            {
                if (res.Value.Flag == "ally")
                {
                    allyCount++;
                }
                else
                {
                    enemyCount++;
                }

                if (firstFlag is null)
                {
                    firstFlag = res.Value.Flag;
                    firstCid = res.Value.ChampionId;
                    firstGameId = candidateGameIds[i];
                    firstGameCreation = res.Value.GameCreation;
                }
            }
        }

        string? recentlyChampionName = null;
        if (firstCid.HasValue && _gameDataProvider?.Invoke() is { } catalog)
        {
            if (catalog.Champions.TryGetValue(firstCid.Value, out var name))
            {
                recentlyChampionName = name;
            }
        }

        return new PlayerFateInfo
        {
            FateFlag = firstFlag,
            RecentlyChampionName = recentlyChampionName,
            GameId = firstGameId,
            GameCreation = firstGameCreation,
            AllyCount = allyCount,
            EnemyCount = enemyCount,
        };
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _detailCache.Clear();
        }
    }

    private JsonElement? GetCachedDetail(long gameId)
    {
        lock (_cacheLock)
        {
            return _detailCache.TryGetValue(gameId, out var elem) ? elem : null;
        }
    }

    private void InsertCachedDetail(long gameId, JsonElement detail)
    {
        lock (_cacheLock)
        {
            if (_detailCache.Count >= GameDetailCacheCap)
            {
                _detailCache.Clear();
            }

            _detailCache[gameId] = detail;
        }
    }
}
