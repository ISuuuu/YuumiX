using YuumiX.Core.Features.Career.Models;

namespace YuumiX.Core.Features.Career;

/// <summary>
/// 生涯相关数据的查询入口。
///
/// 目前只包含排位总览（生涯页顶部的段位表），后续的战利品与英雄熟练度
/// 会作为独立方法/接口加入，而不是塞进这里——三者的取数节奏完全不同：
/// 段位在打完一局后才变，战利品在开箱后立刻变，熟练度每局都在变。
/// 合成一个"生涯服务"会让任意一个刷新动作都牵动另外两份数据的失效。
///
/// <b>失败不抛异常</b>（除取消除外）：客户端未连接、账号未定级、接口 404 都返回
/// <see cref="RankedStats.Empty"/>，界面据此渲染 <c>--</c>，不需要写 try/catch。
/// </summary>
public interface ICareerService
{
    /// <summary>
    /// 获取指定召唤师的排位总览。
    /// </summary>
    /// <param name="puuid">玩家 puuid。</param>
    /// <param name="forceRefresh">忽略缓存重新拉取（刷新按钮用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>排位总览；未连接、失败或无排位记录时为 <see cref="RankedStats.Empty"/>。</returns>
    Task<RankedStats> GetRankedStatsAsync(
        string puuid,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取当前登录账号的战利品与材料概览。
    /// </summary>
    Task<PlayerLootOverview> GetLootOverviewAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取当前登录账号的全英雄熟练度与里程碑概览。
    /// </summary>
    Task<MasteryOverview> GetMasteryOverviewAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>清空全部缓存。切换账号或被踢下线后必须调用，否则会看到上一个账号的段位。</summary>
    void InvalidateCache();
}
