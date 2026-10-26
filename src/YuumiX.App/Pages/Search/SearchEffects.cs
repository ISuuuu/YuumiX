using System.Globalization;
using Microsoft.UI.Reactor.Core;
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
    /// <summary>路由跳转触发的搜索：若上一轮搜索还在进行中，等待空闲再提交（防重入，最多 30s）。</summary>
    internal static async Task RunRoutedSearchAsync(
        Mutation<SearchTargetInput, SummonerProfile?> mutation,
        string? query,
        string? puuid,
        long targetGameId)
    {
        int waitCount = 0;
        while (mutation.IsPending && waitCount < 600)
        {
            await Task.Delay(50).ConfigureAwait(false);
            waitCount++;
        }

        _ = mutation.RunAsync(new SearchTargetInput(query, puuid, null, targetGameId));
    }

    /// <summary>
    /// 首屏 / 翻页 / 刷新共用的取数入口（UseEffect 回调体）：
    /// 还没有召唤师就先解析当前登录账号（setSummoner 会改变依赖键，下一轮再拉战绩，
    /// 不会重复请求）；有 puuid 就拉这一页。返回值即 effect 的 cleanup（取消在途请求）。
    /// </summary>
    internal static Action BeginHistoryLoad(
        AppServices services,
        IMatchService matchService,
        ISummonerService summonerService,
        SummonerProfile? summoner,
        int pageIndex,
        Action<SummonerProfile?> applySummoner,
        Action<bool> applySummonerResolved,
        Action<IReadOnlyList<MatchSummary>> applyMatches,
        Action<bool> applyHasMore)
    {
        if (summoner is null)
        {
            var resolveCancellation = new CancellationTokenSource();
            _ = SearchLoaders.ResolveSummonerAsync(summonerService, applySummoner, applySummonerResolved, resolveCancellation.Token);
            return () =>
            {
                resolveCancellation.Cancel();
                resolveCancellation.Dispose();
            };
        }

        var cancellation = new CancellationTokenSource();
        _ = SearchLoaders.LoadHistoryAsync(services, matchService, summoner.Puuid, pageIndex, applyMatches, applyHasMore, cancellation.Token);
        return () =>
        {
            cancellation.Cancel();
            cancellation.Dispose();
        };
    }

    /// <summary>详情加载 effect 回调体：先清空再取详情（服务内部按 gameId 缓存，来回切换不重复请求）。</summary>
    internal static Action BeginDetailLoad(
        AppServices services,
        IMatchService matchService,
        long gameId,
        string? puuid,
        Action<MatchDetail?> apply)
    {
        if (gameId == 0)
        {
            apply(null);
            return static () => { };
        }

        // 先清空：否则新选中项会短暂顶着上一局的 10 人名单，看起来像数据错位
        apply(null);

        var cancellation = new CancellationTokenSource();
        _ = SearchLoaders.LoadDetailAsync(services, matchService, gameId, puuid, apply, cancellation.Token);
        return () =>
        {
            cancellation.Cancel();
            cancellation.Dispose();
        };
    }

    /// <summary>
    /// 载入一页后补传本页对局（对齐旧 Search.vue：翻页即补传，服务端已有会返回 exists）。
    /// </summary>
    internal static void RunAutoUpload(
        AppServices services,
        IReadOnlyList<MatchSummary> matches,
        HashSet<long> uploadedGameIds,
        string? summonerPuuid,
        Action<string?> setNotice)
    {
        long[] pending = matches.Select(m => m.GameId)
            .Where(id => uploadedGameIds.Add(id))
            .ToArray();
        if (pending.Length > 0)
        {
            _ = SearchLoaders.UploadMatchesAsync(services, pending, summonerPuuid, setNotice);
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
    internal static void CopyGameId(long gameId, Action<string?> setNotice)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(gameId.ToString(CultureInfo.InvariantCulture));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            setNotice($"游戏 ID: {gameId} 已复制到剪贴板");
            _ = ClearNoticeLaterAsync(setNotice, 2000);
        }
        catch
        {
            setNotice("复制到剪贴板失败");
        }
    }

    /// <summary>对局结束后的延迟梯度轮询刷新（对齐旧版 Search.vue：立即刷新 + 1.5s/2s/3s/3s 重试梯度，检测到新对局即退出）。</summary>
    internal static async Task DelayedRefreshAsync(
        Ref<int> requestSeq,
        Action<int> apply,
        Func<long?> getCurrentLatestGameId,
        long? prevLatestId,
        long? expectedNewGameId)
    {
        requestSeq.Current += 1;
        apply(requestSeq.Current);

        int[] retryDelays = [1500, 2000, 3000, 3000];
        foreach (int delay in retryDelays)
        {
            await Task.Delay(delay).ConfigureAwait(false);

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

    /// <summary>提示文案自动消失（复制游戏 ID 等）。页面卸载后 setter 为 no-op，无需取消。</summary>
    internal static async Task ClearNoticeLaterAsync(Action<string?> apply, int delayMilliseconds)
    {
        await Task.Delay(delayMilliseconds).ConfigureAwait(false);
        apply(null);
    }
}
