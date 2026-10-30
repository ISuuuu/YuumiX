using System.Globalization;
using Microsoft.UI.Reactor.Core;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor.Hooks;
using YuumiX.Core;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Upload;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 战绩查询页的 effect 回调体与命令辅助（R14 拆分）：页面保留 hooks 编排，
/// 具体副作用实现集中在此，便于页面主文件守住规模上限。
/// </summary>
internal static class SearchEffects
{
    /// <summary>
    /// 载入一页后补传本页对局（对齐旧 Search.vue：翻页即补传，服务端已有会返回 exists）。
    /// </summary>
    internal static void RunAutoUpload(
        AppServices services,
        IReadOnlyList<MatchSummary> matches,
        HashSet<long> uploadedGameIds,
        string? summonerPuuid,
        Action<string?, int> setNotice,
        CancellationToken cancellationToken)
    {
        _ = RunUploadBatchAsync(matches.Select(m => m.GameId), uploadedGameIds,
            (pending, ct) => SearchLoaders.UploadMatchesAsync(services, pending, summonerPuuid, setNotice, ct), cancellationToken,
            services.LoggerFactory.CreateLogger("YuumiX.App.Pages.Search"));
    }

    internal static async Task RunUploadBatchAsync(
        IEnumerable<long> gameIds, HashSet<long> uploadedGameIds,
        Func<long[], CancellationToken, Task<bool>> upload, CancellationToken cancellationToken, ILogger? logger = null)
    {
        long[] pending;
        lock (uploadedGameIds)
            pending = gameIds.Where(uploadedGameIds.Add).ToArray();
        if (pending.Length == 0) return;

        int rolledBack = 0;
        void RollBack()
        {
            if (Interlocked.Exchange(ref rolledBack, 1) != 0) return;
            lock (uploadedGameIds)
                foreach (long id in pending) uploadedGameIds.Remove(id);
        }

        using var registration = cancellationToken.Register(RollBack);
        try
        {
            if (cancellationToken.IsCancellationRequested || !await upload(pending, cancellationToken).ConfigureAwait(false))
                RollBack();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 取消时同步撤销去重标记，返回页面即可重试。
            RollBack();
        }
        catch (Exception ex)
        {
            RollBack();
            logger?.LogWarning(ex, "上传对局批次失败");
        }
    }

    /// <summary>上传开关写回（对齐旧 config.rs）：仅保存配置状态，不主动触发任何上传。</summary>
    internal static void ApplyUploadToggle(
        AppServices services,
        UploadQueueService uploadQueue,
        bool enabled,
        Action<bool> applyUploadOn,
        Action<string?> setNotice)
    {
        applyUploadOn(enabled);
        services.Config.Functions.UploadEnabled = enabled;
        try
        {
            services.ConfigStore.SaveWithBackup(services.Config);
        }
        catch (Exception)
        {
            setNotice("上传开关保存失败");
        }
    }

    /// <summary>复制游戏 ID 到剪贴板，2 秒后清除提示。</summary>
    internal static void CopyGameId(long gameId, Action<string?, int> setNotice)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(gameId.ToString(CultureInfo.InvariantCulture));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            setNotice($"游戏 ID: {gameId} 已复制到剪贴板", 2000);
        }
        catch
        {
            setNotice("复制到剪贴板失败", 0);
        }
    }

    /// <summary>对局结束后的延迟梯度轮询刷新（对齐旧版 Search.vue：立即刷新 + 1.5s/2s/3s/3s 重试梯度，检测到新对局即退出）。</summary>
    internal static async Task DelayedRefreshAsync(
        Ref<int> requestSeq,
        Action<int> apply,
        Func<long?> getCurrentLatestGameId,
        long? prevLatestId,
        long? expectedNewGameId,
        CancellationToken cancellationToken = default,
        ILogger? logger = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            requestSeq.Current += 1;
            apply(requestSeq.Current);

            int[] retryDelays = [1500, 2000, 3000, 3000];
            foreach (int delay in retryDelays)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                long? currentLatest = getCurrentLatestGameId();
                if (expectedNewGameId is { } target && target > 0 && currentLatest == target)
                {
                    break;
                }
                if (prevLatestId is { } prev && prev > 0 && currentLatest is { } cur && cur > 0 && cur != prev)
                {
                    break;
                }

                requestSeq.Current += 1;
                apply(requestSeq.Current);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 离页或新的刷新请求会取消旧轮询，属于正常结束。
        }
        catch (Exception ex) { logger?.LogWarning(ex, "刷新战绩轮询失败"); }
    }

    internal static string HistoryCacheKey(string? puuid, int pageIndex, int connectionStatus) =>
        $"search/matches/{puuid ?? string.Empty}/{pageIndex}/{connectionStatus}";

    internal static MatchHistoryPage? CurrentHistoryPage(AsyncValue<SearchHistoryResult> resource, string key, SearchHistoryResult? previous = null)
    {
        var result = resource switch
        {
            AsyncValue<SearchHistoryResult>.Data data => data.Value,
            AsyncValue<SearchHistoryResult>.Reloading reloading => reloading.Previous,
            AsyncValue<SearchHistoryResult>.Loading => previous,
            _ => null,
        };
        return result?.Key == key ? result.Page : null;
    }

    internal static async Task ClearNoticeLaterAsync(Action clear, int delayMilliseconds, CancellationToken cancellationToken, ILogger? logger = null)
    {
        try
        {
            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            clear();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 离页或新提示会取消旧计时，避免清除较新的提示。
        }
        catch (Exception ex) { logger?.LogWarning(ex, "清除搜索提示失败"); }
    }
}
