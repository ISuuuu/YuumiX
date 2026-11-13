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
    /// <b>只认精确标识，禁止同族兜底</b>：国服另有 <c>JADE_RANKED_SOLO_5x5</c>，它不是单双排，
    /// 而是经典模式（客户端 <c>kJade</c> 模式组）「召唤师之旅」的进度——段位是
    /// 盐 → <b>木头</b> → 白银 → 黄金 → 铂金 → 钻石 → 传奇 那一套。2026-11-13 实测：
    /// 未打过单双排的账号在该队列有 WOOD III / 20 点 / 1 胜，而 <c>RANKED_SOLO_5x5</c>
    /// 是 <c>tier: ""</c>、<c>isProvisional: true</c> 的未定级条目。
    /// 按「队列名含 <c>SOLO</c>」兜底会把经典模式的成绩当成单双排，界面因此凭空多出
    /// 「1 场 / 木头 III」。经典模式进度没有对应界面，不得混入排位表。
    /// </summary>
    public RankedQueueEntry? SoloDuo => Find(RankedQueueTypes.SoloDuo);

    /// <summary>灵活排位成绩（规则同 <see cref="SoloDuo"/>，只认精确标识）。</summary>
    public RankedQueueEntry? Flex => Find(RankedQueueTypes.Flex);
}
