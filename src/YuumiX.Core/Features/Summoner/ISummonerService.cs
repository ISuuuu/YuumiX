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

    /// <summary>清空缓存。切换账号或被踢下线后必须调用，否则会拿到上一个账号的资料。</summary>
    void InvalidateCache();
}
