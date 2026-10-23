namespace YuumiX.Core.Features.Career.Models;

/// <summary>
/// 一名召唤师的排位总览，对应 <c>/lol-ranked/v1/ranked-stats/{puuid}</c>。
///
/// 与原实现的一处刻意差异：原 TS 类型把响应顶层字段（<c>highestTier</c> 等）交叉进
/// 每个队列条目，而加载函数只回填了 <c>queues</c>，导致「赛季最高」与「上赛季结算」
/// 两列在界面上恒为 <c>--</c>。这里把响应建模成"队列集合 + 顶层兜底"，由解析器
/// 在解析阶段合并，界面不再区分字段来自哪一层。
///
/// <see cref="Empty"/> 是"没有数据"的唯一表示：未连接客户端、请求失败、账号未定级
/// 都归到它，界面只需判断 <see cref="Queues"/> 是否为空，不必区分 <c>null</c> 与空集合。
/// </summary>
public sealed record RankedStats
{
    /// <summary>各队列成绩，按 LCU 下发顺序。</summary>
    public required IReadOnlyList<RankedQueueEntry> Queues { get; init; }

    /// <summary>空结果（未连接 / 请求失败 / 无排位记录）。</summary>
    public static RankedStats Empty { get; } = new() { Queues = [] };

    /// <summary>按队列标识查找；不存在时返回 <c>null</c>。</summary>
    public RankedQueueEntry? Find(string queueType)
    {
        foreach (RankedQueueEntry queue in Queues)
        {
            if (string.Equals(queue.QueueType, queueType, StringComparison.Ordinal))
            {
                return queue;
            }
        }

        return null;
    }

    /// <summary>
    /// 单双排成绩。
    ///
    /// <b>为什么不是直接 <c>Find("RANKED_SOLO_5x5")</c></b>：国服新赛季把单双排的队列标识改成了
    /// <c>JADE_RANKED_SOLO_5x5</c>（2026-10-23 实测），而 <c>RANKED_SOLO_5x5</c> 仍然存在、
    /// 只是恒定未定级（<c>tier: ""</c>、<c>isProvisional: true</c>）。若只认旧标识，
    /// 排位表在国服永远是「0 场 / --」。
    ///
    /// 因此规则改为「精确标识优先，同族兜底」：精确标识有成绩就用它，否则在同族
    /// （队列名含 <c>SOLO</c>）里找第一个有成绩的。这样对两个区服、以及将来再改前缀都成立，
    /// 不需要把 <c>JADE</c> 这类临时前缀写死。
    /// </summary>
    public RankedQueueEntry? SoloDuo => FindPreferred(RankedQueueTypes.SoloDuo, "SOLO");

    /// <summary>灵活排位成绩（规则同 <see cref="SoloDuo"/>）。</summary>
    public RankedQueueEntry? Flex => FindPreferred(RankedQueueTypes.Flex, "FLEX");

    /// <summary>该条目是否已经承载任何成绩（有段位或有对局）。</summary>
    private static bool HasRecord(RankedQueueEntry entry) => entry.HasRank || entry.TotalGames > 0;

    private RankedQueueEntry? FindPreferred(string exactQueueType, string familyToken)
    {
        RankedQueueEntry? exact = Find(exactQueueType);
        if (exact is not null && HasRecord(exact))
        {
            return exact;
        }

        foreach (RankedQueueEntry queue in Queues)
        {
            if (queue.QueueType.Contains(familyToken, StringComparison.Ordinal) && HasRecord(queue))
            {
                return queue;
            }
        }

        // 同族都没有成绩时仍返回精确条目（可能是未定级的正式队列），
        // 让界面显示「0 场 / --」而不是整行消失。
        return exact;
    }
}
