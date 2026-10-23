using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Lcu;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Features.Replay;

/// <summary>
/// <see cref="IReplayService"/> 的 LCU 实现。
/// </summary>
public sealed class ReplayService : IReplayService
{
    private const string ComponentTypePayload = "{\"componentType\":\"replay-button_match-history\"}";
    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly ILogger<ReplayService>? _logger;

    public ReplayService(
        Func<ILcuTransport?> transportProvider,
        ILogger<ReplayService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);
        _transportProvider = transportProvider;
        _logger = logger;
    }

    public async Task<string> GetReplayStatusAsync(long gameId, CancellationToken cancellationToken = default)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return "unknown";
        }

        string path = $"/lol-replays/v1/metadata/{gameId}";
        try
        {
            LcuResponse resp = await transport.SendAsync(LcuRequest.Get(path), cancellationToken).ConfigureAwait(false);

            if (!resp.IsSuccess)
            {
                // 若尚未存在本地元数据（LCU 返回 404），先触发元数据创建再重查
                await transport.SendAsync(
                    LcuRequest.Post($"/lol-replays/v2/metadata/{gameId}/create", "{}"),
                    cancellationToken).ConfigureAwait(false);

                resp = await transport.SendAsync(LcuRequest.Get(path), cancellationToken).ConfigureAwait(false);
            }

            if (!resp.IsSuccess || string.IsNullOrWhiteSpace(resp.Body))
            {
                return "unknown";
            }

            using JsonDocument doc = JsonDocument.Parse(resp.Body);
            string state = JsonRead.String(doc.RootElement, "state") ?? "unknown";
            return state;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "获取对局 {GameId} 回放状态异常", gameId);
            return "unknown";
        }
    }

    public async Task<bool> DownloadReplayAsync(long gameId, CancellationToken cancellationToken = default)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return false;
        }

        try
        {
            // 确保本地元数据已初始化注册
            await transport.SendAsync(
                LcuRequest.Post($"/lol-replays/v2/metadata/{gameId}/create", "{}"),
                cancellationToken).ConfigureAwait(false);

            LcuResponse resp = await transport.SendAsync(
                LcuRequest.Post($"/lol-replays/v1/rofls/{gameId}/download", ComponentTypePayload),
                cancellationToken).ConfigureAwait(false);

            return resp.IsSuccess;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "请求下载对局 {GameId} 回放失败", gameId);
            return false;
        }
    }

    public async Task<bool> WatchReplayAsync(long gameId, CancellationToken cancellationToken = default)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return false;
        }

        try
        {
            LcuResponse resp = await transport.SendAsync(
                LcuRequest.Post($"/lol-replays/v1/rofls/{gameId}/watch", ComponentTypePayload),
                cancellationToken).ConfigureAwait(false);

            return resp.IsSuccess;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "唤起对局 {GameId} 回放播放失败", gameId);
            return false;
        }
    }
}
