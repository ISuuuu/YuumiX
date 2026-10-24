using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.Core.Features.Summoner;

/// <summary>
/// 当前登录召唤师查询。
///
/// 为什么单独成接口而不是塞进 <see cref="Match.IMatchService"/>：
/// 战绩查询只是第一个需要 puuid 的调用方，生涯页、对局信息页、路人集都要它。
/// 混进战绩服务后，一旦某个页面只需要召唤师信息，就会被迫依赖整套战绩能力。
/// </summary>
public interface ISummonerService
{
    /// <summary>
    /// 获取当前登录召唤师。
    /// </summary>
    /// <returns>召唤师；客户端未连接、接口失败或载荷不完整时返回 <c>null</c>。</returns>
    /// <exception cref="OperationCanceledException">令牌取消。</exception>
    Task<SummonerProfile?> GetCurrentSummonerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 Riot ID 查询任意召唤师（战绩查询页的搜索框）。
    /// </summary>
    /// <param name="riotId">形如 <c>名字#TAG</c>；不带 <c>#</c> 时 LCU 会按名字前缀匹配。</param>
    /// <returns>查不到、客户端未连接或载荷不完整时返回 <c>null</c>（不抛异常，界面据此提示"未找到"）。</returns>
    /// <remarks>
    /// 与 <see cref="GetCurrentSummonerAsync"/> 分开而不是加参数：两者缓存语义相反——
    /// 当前账号在一次登录期间不变、可以缓存；搜索目标每次都不同、缓存只会把上一个查询结果端出来。
    /// </remarks>
    /// <exception cref="OperationCanceledException">令牌取消。</exception>
    Task<SummonerProfile?> FindByNameAsync(string riotId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 puuid 查询任意召唤师（查看他人生涯：最近队友点击、战绩详情跳转等入口）。
    /// </summary>
    /// <param name="puuid">玩家 puuid。</param>
    /// <returns>查不到、客户端未连接或载荷不完整时返回 <c>null</c>（不抛异常，调用方用占位对象兜底）。</returns>
    /// <remarks>
    /// 走 <c>/lol-summoner/v2/summoners/puuid/{puuid}</c>，与战绩详情的私密检测同接口。
    /// 与 <see cref="FindByNameAsync"/> 同样刻意不缓存：查看目标每次都不同。
    /// </remarks>
    /// <exception cref="OperationCanceledException">令牌取消。</exception>
    Task<SummonerProfile?> FindByPuuidAsync(string puuid, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 summonerId 查询召唤师（用于战绩详情面板点击无 puuid 但有 summonerId 的参与者触发页内重搜）。
    /// </summary>
    /// <param name="summonerId">召唤师 ID（长整型）。</param>
    /// <returns>查不到、客户端未连接或载荷不完整时返回 <c>null</c>。</returns>
    /// <remarks>
    /// 走 <c>/lol-summoner/v1/summoners/{summonerId}</c>，刻意不缓存。
    /// </remarks>
    /// <exception cref="OperationCanceledException">令牌取消。</exception>
    Task<SummonerProfile?> FindBySummonerIdAsync(long summonerId, CancellationToken cancellationToken = default);

    /// <summary>清空缓存。切换账号或被踢下线后必须调用，否则会拿到上一个账号的资料。</summary>
    void InvalidateCache();
}
