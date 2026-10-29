using YuumiX.Core.Common;
using YuumiX.Core.Features.Match.Models;

namespace YuumiX.Core.Features.Match;

/// <summary>
/// 战绩查询服务：把"分页拉取 → 解析 → 去重缓存"串成一条可被界面直接消费的链路。
///
/// 对应原项目的三处散落逻辑：
/// <list type="bullet">
///   <item>Rust 侧 <c>get_match_history</c> / <c>get_match_history_sgp</c>（HTTP + 解析）；</item>
///   <item>Vue 侧的增量加载与预取（<c>loadedGameIndex</c>、<c>cacheGet</c> 那一套）；</item>
///   <item>详情页直接打 LCU 的 <c>/lol-match-history/v1/games/{gameId}</c>。</item>
/// </list>
/// 合并进服务的理由：单进程下没有 IPC 边界，界面不该自己拼 URL、管缓存、算分页。
///
/// 三条实现纪律（契约层面固定，实现方不得放宽）：
/// <list type="number">
///   <item>HTTP 一律复用 <c>LcuConnection</c> 的传输与其并发限流，不新建 <c>HttpClient</c>；</item>
///   <item>状态经 <see cref="Status"/> 推送，投递时不持锁、单个观察者异常不外溢（ADR-10）；</item>
///   <item>解析交给 <c>Parsers/MatchParser</c>，服务只负责取数与编排，不自己解析字段。</item>
/// </list>
/// </summary>
public interface IMatchService
{
    /// <summary>
    /// 请求状态流。发布在调用方的线程上（通常是后台线程），
    /// 界面订阅时必须按 ADR-07 处理线程封送。
    /// </summary>
    EventStream<MatchServiceStatus> Status { get; }

    /// <summary>
    /// 拉取一页战绩。
    /// </summary>
    /// <param name="puuid">目标玩家 puuid。</param>
    /// <param name="beginIndex">起始下标（含），从 0 开始。</param>
    /// <param name="endIndex">结束下标（含）。LCU 语义如此，与服务端的"开区间"惯例不同。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 本页战绩。无对局时返回 <see cref="MatchHistoryPage.Empty"/> 而不是 <c>null</c>——
    /// "查询成功但没有战绩"是正常结果，不该让每个调用点写一遍空判断。
    /// </returns>
    /// <exception cref="OperationCanceledException">令牌取消。</exception>
    Task<MatchHistoryPage> GetMatchHistoryAsync(
        string puuid,
        int beginIndex,
        int endIndex,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 拉取单局详情。
    /// </summary>
    /// <param name="gameId">对局 ID。</param>
    /// <param name="queriedPuuid">
    /// 被查询玩家 puuid，用于填充 <see cref="MatchDetail.QueriedPlayerWin"/>；传 <c>null</c> 时不判定。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>详情；对局不存在（404）或载荷无参与者时返回 <c>null</c>。</returns>
    /// <exception cref="OperationCanceledException">令牌取消。</exception>
    Task<MatchDetail?> GetMatchDetailAsync(
        long gameId,
        string? queriedPuuid = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 清空去重缓存。用于"刷新"按钮，以及检测到客户端重启（端口变化）后强制重取。
    /// </summary>
    void InvalidateCache();

    /// <summary>
    /// 只让列表池失效（详情缓存保留）。用于「对局结束 / 新对局开始」这类
    /// 只影响列表首位、不影响已结束对局详情的事件——不依赖任何页面挂载即可生效。
    /// </summary>
    void InvalidateHistoryCache();
}
