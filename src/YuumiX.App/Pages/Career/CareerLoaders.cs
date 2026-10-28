using Microsoft.Extensions.Logging;
using YuumiX.Core;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.App.Pages.Career;

/// <summary>最近队友请求载荷：Nonce 打破 record 值相等去重，允许重复点击重新拉取。</summary>
internal sealed record RecentTeammatesRequest(string Puuid, long[] GameIds, int Nonce);

/// <summary>
/// 生涯页的数据加载与纯函数辅助（自 <c>CareerPage</c> 拆出，R14）。
/// 本文件 343 行（R14 过渡期，超 300 理由：LoadSelf/LoadOther 各自是"解析→并行取数→回写"的单块编排，拆开失去对应关系）。
/// </summary>
internal static class CareerLoaders
{
    /// <summary>空列表共享实例（<c>UseState</c> 初值只读一次，集合表达式会每次新建，REACTOR_HOOKS_013）。</summary>
    internal static readonly IReadOnlyList<MatchSummary> EmptyMatches = [];

    /// <summary>空战利品选中集合共享实例。</summary>
    internal static IReadOnlySet<string> EmptyLootSelection() => new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// 解析队列筛选项：第一项为「全部 (N)」，随后仅保留当前对局列表中实际存在的模式（按场数倒序，带场数）。
    /// 结果按源列表引用由调用方 UseMemo 缓存。
    /// </summary>
    internal static (string[] Labels, string[] ModeNames) BuildFilterLabels(IReadOnlyList<MatchSummary> matches)
    {
        var isEn = Composition.AppHost.Services?.I18n.CurrentLanguage == YuumiX.Core.Localization.SupportedLanguages.EnUs;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (MatchSummary m in matches)
        {
            if (!string.IsNullOrEmpty(m.QueueName))
            {
                counts[m.QueueName] = counts.GetValueOrDefault(m.QueueName) + 1;
            }
        }

        var allPrefix = isEn ? "All" : "全部";
        var labels = new List<string> { $"{allPrefix} ({matches.Count})" };
        var modes = new List<string> { string.Empty };

        // 实际有战绩的模式按场数倒序
        foreach (var kvp in counts.OrderByDescending(p => p.Value))
        {
            var translatedName = Search.SearchFilters.TranslateQueueLabel(kvp.Key, isEn);
            labels.Add($"{translatedName} ({kvp.Value})");
            modes.Add(kvp.Key);
        }

        return (labels.ToArray(), modes.ToArray());
    }

    /// <summary>
    /// 查看他人时资料解析失败的占位对象（旧版兜底：名「召唤师」、icon 29、等级 0），不阻断排位/战绩加载。
    /// 队友卡片传来的名字可能形如 <c>名字#TAG</c>，这里做与旧版一致的拆分。
    /// </summary>
    internal static SummonerProfile PlaceholderSummoner(string puuid, string? name = null, int profileIconId = 29)
    {
        string gameName = name ?? "召唤师";
        string tagLine = string.Empty;
        int hashIndex = gameName.IndexOf('#');
        if (hashIndex >= 0)
        {
            tagLine = gameName[(hashIndex + 1)..];
            gameName = gameName[..hashIndex];
        }

        return new SummonerProfile
        {
            AccountId = 0,
            DisplayName = gameName,
            GameName = gameName,
            TagLine = tagLine,
            Puuid = puuid,
            SummonerId = 0,
            SummonerLevel = 0,
            ProfileIconId = profileIconId,
            PercentCompleteForNextLevel = 0,
            XpSinceLastLevel = 0,
            XpUntilNextLevel = 0,
        };
    }

    /// <summary>"已复制"提示 1.5 秒后自动消失。</summary>
    internal static async Task ResetCopiedAsync(Action<bool> setCopied, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
            setCopied(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>对局结束后的延迟梯度轮询刷新（对齐旧版 MatchHistoryTab.vue：立即刷新 + 1.5s/2s/3s/3s 重试梯度，检测到新对局即退出）。</summary>
    internal static async Task DelayedRefreshAsync(
        Microsoft.UI.Reactor.Core.Ref<int> refreshSeq,
        Action<int> apply,
        Func<long?> getCurrentLatestGameId,
        long? prevLatestId,
        long? expectedNewGameId)
    {
        refreshSeq.Current += 1;
        apply(refreshSeq.Current);

        int[] retryDelays = [1500, 2000, 3000, 3000];
        foreach (int delay in retryDelays)
        {
            await Task.Delay(delay).ConfigureAwait(false);

            // 检查当前是否已经刷新出了新对局：若已刷新出目标对局或最新对局已改变，停止后续重试（对齐旧版 MatchHistoryTab.vue）
            long? currentLatest = getCurrentLatestGameId();
            if (expectedNewGameId is { } target && target > 0 && currentLatest == target)
            {
                break;
            }
            if (prevLatestId is { } prev && prev > 0 && currentLatest is { } cur && cur > 0 && cur != prev)
            {
                break;
            }

            refreshSeq.Current += 1;
            apply(refreshSeq.Current);
        }
    }

    /// <summary>
    /// 首屏与刷新共用的取数入口（UseEffect 回调体）：查看目标、refreshToken 或对局加载数量
    /// 变化即重新取数，token &gt; 0 时强制绕过排位与战绩缓存。返回值即 effect 的 cleanup。
    /// 代际计数在加载入口递增、回写前校验（先点 A 再点 B，慢响应不回写）。
    /// </summary>
    internal static Action BeginCareerLoad(
        AppServices services,
        ISummonerService summonerService,
        ICareerService careerService,
        IMatchService matchService,
        CareerViewTarget? viewTarget,
        int refreshToken,
        Microsoft.UI.Reactor.Core.Ref<int> requestSeq,
        Action<SummonerProfile?> applySummoner,
        Action<RankedStats> applyRanked,
        Action<IReadOnlyList<MatchSummary>> applyMatches,
        Action<PlayerLootOverview?> applyLoot,
        Action<MasteryOverview?> applyMastery,
        Action<bool> applyLoading,
        ILogger? logger)
    {
        int seq = ++requestSeq.Current;
        Func<bool> isStale = () => seq != requestSeq.Current;
        var cancellation = new CancellationTokenSource();
        var gamesNumber = Math.Clamp(services.Config.Functions.CareerGamesNumber, 1, 50);

        logger?.LogDebug(
            "生涯取数开始: 目标={Target}，强制刷新={ForceRefresh}，场次={Games}，代际={Seq}",
            viewTarget?.Puuid ?? "(自己)",
            refreshToken > 0,
            gamesNumber,
            seq);

        _ = viewTarget is { } target
            ? LoadOtherCareerAsync(
                summonerService,
                careerService,
                matchService,
                target,
                gamesNumber,
                refreshToken > 0,
                isStale,
                applySummoner,
                applyRanked,
                applyMatches,
                applyLoading,
                cancellation.Token,
                logger)
            : LoadSelfAsync(
                summonerService,
                careerService,
                matchService,
                gamesNumber,
                refreshToken > 0,
                isStale,
                applySummoner,
                applyRanked,
                applyMatches,
                applyLoot,
                applyMastery,
                applyLoading,
                cancellation.Token,
                logger);

        return () =>
        {
            cancellation.Cancel();
            cancellation.Dispose();
        };
    }

    /// <summary>
    /// 自己的生涯：当前召唤师 → 排位总览 + 最近对局（数量由「默认对局数量」设置决定）+ 战利品 + 英雄熟练度。
    /// 四路并行，失败只显示空态或提示，不弹框打扰。
    /// </summary>
    internal static async Task LoadSelfAsync(
        ISummonerService summonerService,
        ICareerService careerService,
        IMatchService matchService,
        int gamesNumber,
        bool forceRefresh,
        Func<bool> isStale,
        Action<SummonerProfile?> applySummoner,
        Action<RankedStats> applyRanked,
        Action<IReadOnlyList<MatchSummary>> applyMatches,
        Action<PlayerLootOverview?> applyLoot,
        Action<MasteryOverview?> applyMastery,
        Action<bool> applyLoading,
        CancellationToken cancellationToken,
        ILogger? logger)
    {
        try
        {
            applyLoading(true);

            SummonerProfile? profile = await summonerService
                .GetCurrentSummonerAsync(cancellationToken)
                .ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested || isStale())
            {
                return;
            }

            applySummoner(profile);
            if (profile is null)
            {
                // 未登录：全部给空态/失败提示，绝不回退样例数据（D1）
                // 这条是「页面一片空白但没有任何错误」的唯一可诊断线索，必须留痕：
                // 客户端端口已通、登录还没完成时 current-summoner 返回 404，属正常时序。
                logger?.LogInformation("生涯取数中止：当前召唤师不可用（客户端可能尚未登录），已按空态收尾");
                applyRanked(RankedStats.Empty);
                applyMatches(EmptyMatches);
                applyLoot(null);
                applyMastery(null);
                return;
            }

            if (forceRefresh)
            {
                matchService.InvalidateCache();
            }

            Task<RankedStats> rankedTask = careerService.GetRankedStatsAsync(
                profile.Puuid, forceRefresh, cancellationToken);

            Task<MatchHistoryPage> historyTask = matchService.GetMatchHistoryAsync(
                profile.Puuid, 0, gamesNumber - 1, cancellationToken);

            Task<PlayerLootOverview?> lootTask = careerService.GetLootOverviewAsync(
                forceRefresh, cancellationToken);

            Task<MasteryOverview?> masteryTask = careerService.GetMasteryOverviewAsync(
                forceRefresh, cancellationToken);

            await Task.WhenAll(rankedTask, historyTask, lootTask, masteryTask).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested || isStale())
            {
                return;
            }

            RankedStats rankedResult = await rankedTask.ConfigureAwait(false);
            MatchHistoryPage historyResult = await historyTask.ConfigureAwait(false);
            PlayerLootOverview? lootResult = await lootTask.ConfigureAwait(false);
            MasteryOverview? masteryResult = await masteryTask.ConfigureAwait(false);

            applyRanked(rankedResult);
            applyMatches(historyResult.Games);
            applyLoot(lootResult);
            applyMastery(masteryResult);

            // 战利品/熟练度为 null 表示「读取失败」（界面会出提示条），与「成功但为空」不同，
            // 这里分开记录，避免下次又只能靠猜。
            logger?.LogInformation(
                "生涯数据加载完成: 战绩 {MatchCount} 场，排位队列 {RankedQueues} 个，战利品={Loot}，熟练度={Mastery}",
                historyResult.Games.Count,
                rankedResult.Queues.Count,
                lootResult is null ? "失败" : "正常",
                masteryResult is null ? "失败" : "正常");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "生涯数据加载失败，已降级为空态");
            applyRanked(RankedStats.Empty);
            applyMatches(EmptyMatches);
            applyLoot(null);
            applyMastery(null);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                applyLoading(false);
            }
        }
    }

    /// <summary>
    /// 他人的生涯（§2.8）：优先用预载资料 → 按 puuid 拉权威资料 → 都失败用占位对象；
    /// 不阻断排位与战绩加载。战利品/熟练度属于当前登录账号，此模式不加载（页签已隐藏）。
    /// </summary>
    internal static async Task LoadOtherCareerAsync(
        ISummonerService summonerService,
        ICareerService careerService,
        IMatchService matchService,
        CareerViewTarget target,
        int gamesNumber,
        bool forceRefresh,
        Func<bool> isStale,
        Action<SummonerProfile?> applySummoner,
        Action<RankedStats> applyRanked,
        Action<IReadOnlyList<MatchSummary>> applyMatches,
        Action<bool> applyLoading,
        CancellationToken cancellationToken,
        ILogger? logger)
    {
        try
        {
            applyLoading(true);

            // 1. 优先用预载/占位对象先让界面展示基础信息（对齐旧版 loadCareerSummoner）
            SummonerProfile profile = target.Preloaded ?? PlaceholderSummoner(target.Puuid);
            applySummoner(profile);

            // 2. 预载资料只是占位（如队友卡片只有名字）或等级为 0 时，拉一次权威资料；
            //    失败（404/未连接）保留占位对象，不阻断排位与战绩加载。
            if (target.Preloaded is null || target.Preloaded.SummonerLevel == 0)
            {
                SummonerProfile? remote = await summonerService
                    .FindByPuuidAsync(target.Puuid, cancellationToken)
                    .ConfigureAwait(false);
                if (remote is not null)
                {
                    profile = remote;
                    applySummoner(profile);
                }
            }

            if (cancellationToken.IsCancellationRequested || isStale())
            {
                return;
            }

            if (forceRefresh)
            {
                matchService.InvalidateCache();
            }

            Task<RankedStats> rankedTask = careerService.GetRankedStatsAsync(
                target.Puuid, forceRefresh, cancellationToken);

            Task<MatchHistoryPage> historyTask = matchService.GetMatchHistoryAsync(
                target.Puuid, 0, gamesNumber - 1, cancellationToken);

            await Task.WhenAll(rankedTask, historyTask).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested || isStale())
            {
                return;
            }

            applyRanked(await rankedTask.ConfigureAwait(false));
            applyMatches((await historyTask.ConfigureAwait(false)).Games);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "他人生涯数据加载失败，已降级为空态（目标={Target}）", target.Puuid);
            applyRanked(RankedStats.Empty);
            applyMatches(EmptyMatches);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                applyLoading(false);
            }
        }
    }
}
