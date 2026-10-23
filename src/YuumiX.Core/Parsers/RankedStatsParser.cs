using System.Text.Json;
using YuumiX.Core.Features.Career.Models;

namespace YuumiX.Core.Parsers;

/// <summary>
/// 排位总览解析：<c>/lol-ranked/v1/ranked-stats/{puuid}</c> 原始 JSON → <see cref="RankedStats"/>。
///
/// 与其它解析器一致，这里是纯函数：输入 <see cref="JsonElement"/>，输出 record，
/// 不读文件、不发起网络、不查资源表。容错取值统一走 <see cref="JsonRead"/>。
///
/// <b>顶层兜底</b>：真实响应里 <c>highestTier</c> / <c>previousSeasonEndTier</c> 既可能
/// 出现在队列条目上，也可能只在响应顶层给一份（国服不同版本不一致）。这里采用
/// 「队列级优先、顶层兜底」：队列条目没给就用顶层值补，两者都没有才是 <c>null</c>。
/// 原实现只读队列级字段，顶层那份等于丢弃，于是「赛季最高」一列恒为 <c>--</c>。
///
/// <b>缺 <c>queueType</c> 的条目直接跳过</b>：没有队列标识就无法归位到"单双排 / 灵活排位"，
/// 保留它只会得到一个永远匹配不上的孤儿行。
///
/// <b>分部字段两种命名都认</b>：Riot 区服用 <c>rank</c>（<c>"II"</c>），国服 2026 赛季
/// 改为 <c>division</c>（2026-10-23 实测 <c>/lol-ranked/v1/ranked-stats/{puuid}</c>）。
/// 只认 <c>rank</c> 会让国服排位表的分部恒为空、胜点有效但段位显示成「荣耀黄金」缺分部。
/// </summary>
public static class RankedStatsParser
{
    /// <summary>
    /// 解析排位总览。
    /// </summary>
    /// <returns>
    /// 排位总览；载荷不是对象、没有 <c>queues</c> 数组或数组为空时返回
    /// <see cref="RankedStats.Empty"/>（未定级账号就是 <c>queues: []</c>）。
    /// </returns>
    public static RankedStats ParseRankedStats(JsonElement stats)
    {
        if (stats.ValueKind != JsonValueKind.Object)
        {
            return RankedStats.Empty;
        }

        JsonElement? queues = JsonRead.Array(stats, "queues");
        if (queues is null || queues.Value.GetArrayLength() == 0)
        {
            return RankedStats.Empty;
        }

        string? topHighestTier = JsonRead.String(stats, "highestTier");
        string? topHighestDivision = JsonRead.String(stats, "highestRank");
        string? topPreviousTier = JsonRead.String(stats, "previousSeasonEndTier");
        string? topPreviousDivision = JsonRead.String(stats, "previousSeasonEndRank");

        var result = new List<RankedQueueEntry>(queues.Value.GetArrayLength());

        foreach (JsonElement item in queues.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? queueType = JsonRead.String(item, "queueType");
            if (string.IsNullOrEmpty(queueType))
            {
                continue;
            }

            result.Add(new RankedQueueEntry
            {
                QueueType = queueType,
                Tier = JsonRead.String(item, "tier") ?? "NONE",
                Division = JsonRead.String(item, "division")
                    ?? JsonRead.String(item, "rank")
                    ?? string.Empty,
                LeaguePoints = JsonRead.Int(item, "leaguePoints"),
                Wins = JsonRead.Int(item, "wins"),
                Losses = JsonRead.Int(item, "losses"),
                HighestTier = JsonRead.String(item, "highestTier") ?? topHighestTier,
                HighestDivision = JsonRead.String(item, "highestDivision")
                    ?? JsonRead.String(item, "highestRank")
                    ?? topHighestDivision,
                PreviousSeasonTier = JsonRead.String(item, "previousSeasonEndTier") ?? topPreviousTier,
                PreviousSeasonDivision = JsonRead.String(item, "previousSeasonEndDivision")
                    ?? JsonRead.String(item, "previousSeasonEndRank")
                    ?? topPreviousDivision,
            });
        }

        return result.Count == 0 ? RankedStats.Empty : new RankedStats { Queues = result };
    }
}
