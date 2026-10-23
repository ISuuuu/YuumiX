using YuumiX.Core.Features.GameInfo.Models;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 任意玩家英雄熟练度查询服务。
/// </summary>
public interface IMasteryService
{
    /// <summary>
    /// 获取指定玩家的全英雄熟练度列表。
    /// 依次按 puuid → isMe local-player → summonerId 降级查询。
    /// </summary>
    Task<IReadOnlyList<PlayerChampionMastery>> GetPlayerMasteriesAsync(
        string? puuid,
        long summonerId = 0,
        bool isMe = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 清除内存熟练度缓存。
    /// </summary>
    void InvalidateCache();
}
