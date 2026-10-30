using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using YuumiX.Core;
using YuumiX.Core.Assets;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;
using YuumiX.Core.Upload;

namespace YuumiX.App.Pages.Search;

/// <summary>搜索命令的解析目标：Riot ID / puuid / 召唤师 ID 三选一，GameId 为可选的跳转选中局。</summary>
internal sealed record SearchTargetInput(string? Query, string? Puuid, long? SummonerId, long TargetGameId, int Sequence = 0)
{
    /// <summary>三种入口的"未找到"文案口径不同（名字搜索 / 召唤师 ID 点击），保持原样区分。</summary>
    public string NotFoundMessage =>
        SummonerId is > 0
            ? $"未找到召唤师 ID: {SummonerId}"
            : $"未找到召唤师：{(!string.IsNullOrEmpty(Query) ? Query : Puuid)}";
}

/// <summary>
/// 战绩查询页的异步辅助（自 <c>SearchPage</c> 拆出，R14）。
/// 数据通路：<c>ISummonerService</c> 取 puuid → <c>IMatchService</c> 取列表/详情；
/// 均可能因"客户端没开"返回空，异常一律吞掉让页面停在空列表，不抛给 UI。
/// 本文件 345 行（R14 过渡期，超 300 理由：10 人名单段位/隐私渐进加载为单块并发逻辑，拆开反而难读）。
/// </summary>
internal sealed record SearchHistoryResult(string Key, MatchHistoryPage Page);
internal sealed record SearchEnrichmentResult(MatchDetail? Detail, bool HasFailures);

internal static class SearchLoaders
{
    /// <summary>战绩列表单页条数：前两页由 LCU 预拉取，第 21 局起走腾讯 SGP 接口增量。</summary>
    internal const int PageSize = 10;

    /// <summary>段位与私密状态跨选中缓存（TTL 5min / 10min）。</summary>
    private static readonly ConcurrentDictionary<string, (string? Tier, string? RankText, long Timestamp)> RankCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, (bool IsPrivate, long Timestamp)> PrivacyCache = new(StringComparer.Ordinal);

    private static readonly TimeSpan RankCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PrivacyCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 搜索命令的解析段（UseMutation 的 mutator）：按 puuid → 召唤师 ID → 名字 依次解析，
    /// 找到后写入搜索历史。**不在这里拉战绩**：换人会改变页面的取数依赖键，
    /// 由那一侧统一拉第一页，否则同一页会请求两次。
    /// </summary>
    internal static async Task<SummonerProfile?> ResolveTargetAsync(
        ISummonerService summonerService,
        SearchHistoryStore historyStore,
        SearchTargetInput input,
        CancellationToken cancellationToken)
    {
        SummonerProfile? found = null;
        if (!string.IsNullOrWhiteSpace(input.Puuid))
        {
            found = await summonerService.FindByPuuidAsync(input.Puuid, cancellationToken).ConfigureAwait(false);
        }

        if (found is null && input.SummonerId is > 0)
        {
            found = await summonerService.FindBySummonerIdAsync(input.SummonerId.Value, cancellationToken).ConfigureAwait(false);
        }

        if (found is null && !string.IsNullOrWhiteSpace(input.Query))
        {
            found = await summonerService.FindByNameAsync(input.Query, cancellationToken).ConfigureAwait(false);
        }

        if (found is null)
        {
            return null;
        }

        // 保存到搜索历史；列表刷新由 mutation 的 InvalidateKeys 驱动页面 UseResource 重拉
        string historyName = !string.IsNullOrEmpty(found.TagLine)
            ? $"{found.GameName}#{found.TagLine}"
            : found.DisplayName;
        await historyStore.AddAsync(historyName, cancellationToken).ConfigureAwait(false);
        return found;
    }

    internal static async Task<MatchHistoryPage> GetHistoryAsync(
        AppServices services, IMatchService matchService, string? puuid, int pageIndex, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(puuid)) return MatchHistoryPage.Empty;
        int begin = pageIndex * PageSize;
        var page = await matchService.GetMatchHistoryAsync(puuid, begin, begin + PageSize - 1, cancellationToken).ConfigureAwait(false);
        if (!page.Succeeded) throw new InvalidOperationException("读取战绩失败");
        if (page.Games.Count > 0)
        {
            var assets = services.Services.GetRequiredService<AssetService>();
            await Task.WhenAll(page.Games.Select(g => assets.GetCachedPathAsync(SearchVisuals.ChampionIcon(g.ChampionId), cancellationToken))).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return page;
    }

    internal static async Task<MatchDetail?> GetDetailAsync(IMatchService matchService, long gameId, string? puuid, CancellationToken cancellationToken)
    {
        if (gameId == 0) return null;
        var detail = await matchService.GetMatchDetailAsync(gameId, puuid, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return detail ?? throw new InvalidOperationException("读取对局详情失败");
    }

    /// <summary>后台渐进加载对局 10 人名单的段位与战绩隐私状态。</summary>
    internal static async Task<SearchEnrichmentResult> LoadParticipantRanksAndPrivacyAsync(
        AppServices services,
        MatchDetail? detail,
        CancellationToken cancellationToken)
    {
        if (detail is null) return new(null, false);
        var careerService = services.Services.GetService<ICareerService>();
        var lcu = services.Services.GetService<LcuConnection>();
        if (careerService is null || lcu is null) return new(detail, false);

        var allPlayers = detail.Blue.Players.Concat(detail.Red.Players).Where(p => !string.IsNullOrEmpty(p.Puuid)).ToList();
        if (allPlayers.Count == 0) return new(detail, false);

        // 「对局详情中显示段位」关闭时跳过段位拉取（Tier 为空即不渲染勋章，
        // 对照旧 Search.vue loadRanksInBackground 的 showTier 判定）
        bool showTier = services.Config.Functions.ShowTierInGameInfo;

        using var semaphore = new SemaphoreSlim(3, 3);
        var rankMap = new ConcurrentDictionary<string, (string? Tier, string? RankText)>(StringComparer.Ordinal);
        var privacyMap = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

        long now = Environment.TickCount64;
        int failures = 0;
        var logger = services.LoggerFactory.CreateLogger("YuumiX.App.Pages.Search");

        var tasks = allPlayers.Select(async p =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RunParticipantEnrichmentAsync(async () =>
                {
                    // 1. 段位（仅在配置开启时加载，TTL 5min 缓存，solo 优先、flex 兜底）
                    if (showTier)
                    {
                        if (RankCache.TryGetValue(p.Puuid, out var cachedRank) && now - cachedRank.Timestamp < RankCacheTtl.TotalMilliseconds)
                        {
                            if (!string.IsNullOrEmpty(cachedRank.Tier))
                            {
                                rankMap[p.Puuid] = (cachedRank.Tier, cachedRank.RankText);
                            }
                        }
                        else
                        {
                            var stats = await careerService.GetRankedStatsAsync(p.Puuid, cancellationToken: cancellationToken).ConfigureAwait(false);
                            var entry = stats.SoloDuo;
                            if (entry is null || string.IsNullOrEmpty(entry.Tier) || entry.Tier == "NONE" || entry.Tier == "UNRANKED")
                            {
                                entry = stats.Flex;
                            }

                            if (entry is not null && !string.IsNullOrEmpty(entry.Tier) && entry.Tier != "NONE" && entry.Tier != "UNRANKED")
                            {
                                rankMap[p.Puuid] = (entry.Tier, entry.TierText);
                                RankCache[p.Puuid] = (entry.Tier, entry.TierText, now);
                            }
                            else
                            {
                                RankCache[p.Puuid] = (null, null, now);
                            }
                        }
                    }

                    // 2. 隐私状态（TTL 10min 缓存，仅缓存 PRIVATE）
                    if (PrivacyCache.TryGetValue(p.Puuid, out var cachedPriv) && now - cachedPriv.Timestamp < PrivacyCacheTtl.TotalMilliseconds)
                    {
                        if (cachedPriv.IsPrivate)
                        {
                            privacyMap[p.Puuid] = true;
                        }
                    }
                    else
                    {
                        var transport = lcu.Transport;
                        if (transport is not null)
                        {
                            var resp = await transport.SendAsync(
                                LcuRequest.Get($"/lol-summoner/v2/summoners/puuid/{p.Puuid}"),
                                cancellationToken).ConfigureAwait(false);
                            if (!resp.IsSuccess) throw new InvalidOperationException("读取隐私状态失败");
                            if (!string.IsNullOrWhiteSpace(resp.Body))
                            {
                                using var doc = JsonDocument.Parse(resp.Body);
                                string? privacy = doc.RootElement.TryGetProperty("privacy", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.String
                                    ? pEl.GetString()
                                    : null;
                                bool isPriv = string.Equals(privacy, "PRIVATE", StringComparison.OrdinalIgnoreCase);
                                if (isPriv)
                                {
                                    privacyMap[p.Puuid] = true;
                                    PrivacyCache[p.Puuid] = (true, now);
                                }
                                else
                                {
                                    PrivacyCache[p.Puuid] = (false, now);
                                }
                            }
                        }
                    }
                }, ex =>
                {
                    Interlocked.Increment(ref failures);
                    logger.LogWarning(ex, "读取参与者段位或隐私失败（Puuid={Puuid}）", p.Puuid);
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        MatchDetailPlayer MapPlayer(MatchDetailPlayer player)
        {
            var rank = rankMap.GetValueOrDefault(player.Puuid);
            bool isPrivate = privacyMap.GetValueOrDefault(player.Puuid, false);
            return player with
            {
                Tier = rank.Tier ?? player.Tier,
                RankText = rank.RankText ?? player.RankText,
                IsPrivate = isPrivate,
            };
        }

        var updatedBlue = detail.Blue with { Players = detail.Blue.Players.Select(MapPlayer).ToList() };
        var updatedRed = detail.Red with { Players = detail.Red.Players.Select(MapPlayer).ToList() };
        return new(detail with { Blue = updatedBlue, Red = updatedRed }, failures > 0);
    }

    internal static async Task RunParticipantEnrichmentAsync(Func<Task> load, Action<Exception> onError, CancellationToken cancellationToken)
    {
        try
        {
            await load().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            onError(ex);
        }
    }

    /// <summary>
    /// 补传一页对局（对齐旧 Search.vue 调 batch_upload_matches 后的三档提示）。
    ///
    /// 无论成败都给一次提示：静默失败会让人误以为传过了，静默成功又无从确认。
    /// </summary>
    internal static async Task<bool> UploadMatchesAsync(
        AppServices services,
        long[] gameIds,
        string? currentSummonerPuuid,
        Action<string?, int> setNotice,
        CancellationToken cancellationToken)
    {
        try
        {
            var batch = services.Services.GetRequiredService<UploadBatchService>();
            BatchUploadResult result = await batch
                .UploadAsync(gameIds, currentSummonerPuuid, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            setNotice(result switch
            {
                { Error: not null, SuccessCount: 0 } => $"上传失败: {result.Error}",
                { SuccessCount: > 0 } => $"已上传 {result.SuccessCount} 场对局",
                _ => "所有对局已存在，无需上传",
            }, 2500);
            return result.FailedCount == 0 && result.Error is null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 页面离开时停止等待上传结果，不再显示提示。
            return false;
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested) setNotice($"上传异常: {ex.Message}", 0);
            return false;
        }
    }
}
