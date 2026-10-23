using System.Text.Json;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 任意玩家英雄熟练度查询服务实现（带 5 分钟缓存与多级降级）。
/// 对应旧版 <c>playerDataCache.ts</c> 的 <c>fetchPlayerMastery</c>。
/// </summary>
public sealed class MasteryService : IMasteryService
{
    private const int CacheCap = 100;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, (IReadOnlyList<PlayerChampionMastery> Data, DateTime Timestamp)> _cache = new();

    public MasteryService(Func<ILcuTransport?> transportProvider)
    {
        _transportProvider = transportProvider;
    }

    public async Task<IReadOnlyList<PlayerChampionMastery>> GetPlayerMasteriesAsync(
        string? puuid,
        long summonerId = 0,
        bool isMe = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(puuid) && summonerId == 0)
        {
            return [];
        }

        string cacheKey = !string.IsNullOrEmpty(puuid) ? puuid : summonerId.ToString();
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var entry))
            {
                if (DateTime.UtcNow - entry.Timestamp < CacheTtl)
                {
                    return entry.Data;
                }

                _cache.Remove(cacheKey);
            }
        }

        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return [];
        }

        LcuResponse response = new(404, string.Empty);

        // 1. 优先按 puuid 查询
        if (!string.IsNullOrEmpty(puuid))
        {
            response = await transport.SendAsync(
                LcuRequest.Get($"/lol-champion-mastery/v1/{puuid}/champion-mastery"),
                cancellationToken).ConfigureAwait(false);
        }

        // 2. 若是当前玩家且失败，降级到 local-player
        if ((!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body)) && isMe)
        {
            response = await transport.SendAsync(
                LcuRequest.Get("/lol-champion-mastery/v1/local-player/champion-mastery"),
                cancellationToken).ConfigureAwait(false);
        }

        // 3. 降级到 summonerId
        if ((!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body)) && summonerId != 0)
        {
            response = await transport.SendAsync(
                LcuRequest.Get($"/lol-champion-mastery/v1/summoners/{summonerId}/champion-mastery"),
                cancellationToken).ConfigureAwait(false);
        }

        if (response.IsSuccess && !string.IsNullOrWhiteSpace(response.Body))
        {
            try
            {
                using var doc = JsonDocument.Parse(response.Body);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<PlayerChampionMastery>();
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        int cid = JsonRead.Int(item, "championId", 0);
                        if (cid <= 0)
                        {
                            cid = JsonRead.Int(item, "champion_id", 0);
                        }

                        if (cid <= 0)
                        {
                            continue;
                        }

                        int level = JsonRead.Int(item, "championLevel", 0);
                        if (level <= 0)
                        {
                            level = JsonRead.Int(item, "masteryLevel", 0);
                        }

                        if (level <= 0)
                        {
                            level = JsonRead.Int(item, "level", 0);
                        }

                        long points = JsonRead.Long(item, "championPoints", 0);
                        if (points <= 0)
                        {
                            points = JsonRead.Long(item, "points", 0);
                        }

                        if (points <= 0)
                        {
                            points = JsonRead.Long(item, "score", 0);
                        }

                        string? grade = JsonRead.String(item, "highestGrade");
                        if (string.IsNullOrEmpty(grade))
                        {
                            grade = JsonRead.String(item, "highest_grade");
                        }

                        long sinceLast = JsonRead.Long(item, "championPointsSinceLastLevel", 0);
                        long untilNext = JsonRead.Long(item, "championPointsUntilNextLevel", 0);
                        int tokens = JsonRead.Int(item, "tokensEarned", 0);

                        list.Add(new PlayerChampionMastery(
                            cid, level, points, grade, sinceLast, untilNext, tokens));
                    }

                    lock (_cacheLock)
                    {
                        if (_cache.Count >= CacheCap)
                        {
                            var oldest = _cache.Keys.FirstOrDefault();
                            if (oldest is not null)
                            {
                                _cache.Remove(oldest);
                            }
                        }

                        _cache[cacheKey] = (list, DateTime.UtcNow);
                    }

                    return list;
                }
            }
            catch
            {
                // ignore
            }
        }

        return [];
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
        }
    }
}
