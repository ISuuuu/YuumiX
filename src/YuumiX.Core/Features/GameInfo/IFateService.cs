using YuumiX.Core.Features.GameInfo.Models;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 宿命检测服务（对应旧版 <c>game_info.rs</c>）。
/// </summary>
public interface IFateService
{
    /// <summary>
    /// 分析目标玩家在候选对局列表中的阵营关系与相遇次数。
    /// </summary>
    /// <param name="gameIds">候选对局 ID 列表（按优先级由高到低排列）</param>
    /// <param name="targetPuuid">目标玩家 PUUID</param>
    /// <param name="currentSummonerId">当前登录玩家的 SummonerId</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>宿命检测结果</returns>
    Task<PlayerFateInfo> GetPlayerFateInfoAsync(
        IReadOnlyList<long> gameIds,
        string targetPuuid,
        long currentSummonerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 清空对局详情缓存（32 容量满或连接重置时）。
    /// </summary>
    void InvalidateCache();
}
