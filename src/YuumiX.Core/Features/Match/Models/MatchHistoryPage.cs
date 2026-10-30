namespace YuumiX.Core.Features.Match.Models;

/// <summary>
/// 一页战绩。
///
/// 为什么返回值不是 <c>IReadOnlyList&lt;MatchSummary&gt;</c>：原实现的分页状态分散在
/// 前端（<c>currentPageNum</c> / <c>loadedGameIndex</c> / <c>hasMore</c> 三个 ref），
/// 而"还有没有下一页"实际取决于 LCU 返回的 <c>gameCount</c>，这个信息只有服务端知道。
/// 把分页元数据一起返回，界面就不必自己维护一套可能与缓存去重结果不一致的下标推算。
/// </summary>
public sealed record MatchHistoryPage
{
    /// <summary>本页对局，按 LCU 下发顺序（新到旧）。</summary>
    public required IReadOnlyList<MatchSummary> Games { get; init; }

    /// <summary>本次请求的起始下标（含）。</summary>
    public required int BeginIndex { get; init; }

    /// <summary>本次请求的结束下标（含）。LCU 语义即 <c>endIndex</c>，不是"开区间"。</summary>
    public required int EndIndex { get; init; }

    /// <summary>该玩家对局总数。LCU 未返回 <c>gameCount</c> 时为 <c>null</c>。</summary>
    public int? TotalCount { get; init; }

    /// <summary>是否还有下一页。由服务端结合 <see cref="TotalCount"/> 与本页条数判定。</summary>
    public required bool HasMore { get; init; }

    /// <summary>区分成功的空结果与读取失败。</summary>
    public bool Succeeded { get; init; } = true;

    /// <summary>空页（无对局或请求失败降级时返回，避免调用方判断 <c>null</c>）。</summary>
    public static MatchHistoryPage Empty { get; } = new()
    {
        Games = [],
        BeginIndex = 0,
        EndIndex = 0,
        HasMore = false,
    };
}
