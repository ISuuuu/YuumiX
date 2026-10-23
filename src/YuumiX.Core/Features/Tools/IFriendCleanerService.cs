using YuumiX.Core.Features.Tools.Models;

namespace YuumiX.Core.Features.Tools;

/// <summary>
/// 好友管理与僵尸号清理服务。
/// </summary>
public interface IFriendCleanerService
{
    /// <summary>
    /// 全量扫描好友并并发分批检测每位好友的最后活跃与对局时间。
    /// </summary>
    /// <param name="onProgress">进度回调 (completed, total)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<FriendEntry>> ScanFriendsAsync(
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量删除指定 ID 的好友。
    /// </summary>
    Task<int> BatchDeleteFriendsAsync(
        IReadOnlyList<string> friendIds,
        CancellationToken cancellationToken = default);
}
