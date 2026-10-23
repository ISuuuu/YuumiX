using YuumiX.Core.Features.Match.Models;

namespace YuumiX.Core.Lcu.Sgp;

/// <summary>
/// 腾讯国服 SGP（Server Game Platform）外网服务。
///
/// 专用于腾讯国服环境下的扩展查询（如突破 LCU 20 条战绩限制、观战等）。
/// </summary>
public interface ISgpService
{
    /// <summary>
    /// 判断大区标识是否属于支持 SGP 的腾讯国服。
    /// </summary>
    bool IsSupported(string? server);

    /// <summary>
    /// 获取 SGP 鉴权用 accessToken（/entitlements/v1/token 的 accessToken 字段）。
    /// </summary>
    /// <param name="forceRefresh">是否强制刷新（遇 401 时使用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<string?> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// 通过 SGP 接口获取战绩摘要列表。
    /// </summary>
    /// <param name="server">大区标识（如 hn1, bgp2 等）。</param>
    /// <param name="puuid">目标玩家 puuid。</param>
    /// <param name="startIndex">起始下标（从 0 开始）。</param>
    /// <param name="count">拉取条数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析后的对局摘要列表；失败或不支持时返回空集合。</returns>
    Task<IReadOnlyList<MatchSummary>> GetMatchHistoryAsync(
        string? server,
        string puuid,
        int startIndex,
        int count,
        CancellationToken cancellationToken = default);
}
