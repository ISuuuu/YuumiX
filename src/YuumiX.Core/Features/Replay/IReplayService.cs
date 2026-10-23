namespace YuumiX.Core.Features.Replay;

/// <summary>
/// 对局 ROFL 回放服务：状态查询、下载、拉起客户端观战。
/// 对应原实现的 <c>download_game_replay</c>、<c>get_replay_status</c> 与 <c>watch_game_replay</c>。
/// </summary>
public interface IReplayService
{
    /// <summary>
    /// 查询指定对局回放的状态（如 "watch", "downloading", "download", "unsupported", "incompatible" 等）。
    /// </summary>
    Task<string> GetReplayStatusAsync(long gameId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 请求下载指定对局回放文件。
    /// </summary>
    Task<bool> DownloadReplayAsync(long gameId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 唤起客户端启动并播放指定对局回放。
    /// </summary>
    Task<bool> WatchReplayAsync(long gameId, CancellationToken cancellationToken = default);
}
