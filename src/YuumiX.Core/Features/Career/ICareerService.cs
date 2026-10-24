using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.SavedPlayers.Models;

namespace YuumiX.Core.Features.Career;

/// <summary>
/// 生涯相关数据的查询入口。
///
/// 目前只包含排位总览（生涯页顶部的段位表），后续的战利品与英雄熟练度
/// 会作为独立方法/接口加入，而不是塞进这里——三者的取数节奏完全不同：
/// 段位在打完一局后才变，战利品在开箱后立刻变，熟练度每局都在变。
/// 合成一个"生涯服务"会让任意一个刷新动作都牵动另外两份数据的失效。
///
/// <b>失败不抛异常</b>（除取消除外）：客户端未连接、账号未定级、接口 404 都返回
/// <see cref="RankedStats.Empty"/>，界面据此渲染 <c>--</c>，不需要写 try/catch。
/// </summary>
public interface ICareerService
{
    /// <summary>
    /// 获取指定召唤师的排位总览。
    /// </summary>
    /// <param name="puuid">玩家 puuid。</param>
    /// <param name="forceRefresh">忽略缓存重新拉取（刷新按钮用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>排位总览；未连接、失败或无排位记录时为 <see cref="RankedStats.Empty"/>。</returns>
    Task<RankedStats> GetRankedStatsAsync(
        string puuid,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取当前登录账号的战利品与材料概览。
    /// </summary>
    /// <returns>概览（可为全空的成功结果）；未连接或读取失败时为 <c>null</c>，界面据此区分"失败提示"与"空账号"。</returns>
    Task<PlayerLootOverview?> GetLootOverviewAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>开启指定战利品配方。</summary>
    Task<LootOperationResult> OpenLootAsync(
        string recipeName,
        IReadOnlyList<string> ingredients,
        int repeatCount = 1,
        IProgress<LootOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>智能一键开启全部箱子/法球。</summary>
    Task<LootOperationResult> SmartOpenAllLootsAsync(
        IProgress<LootOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>批量分解选中的碎片。</summary>
    Task<LootOperationResult> DisenchantLootsAsync(
        IReadOnlyList<(string LootId, int Count)> items,
        IProgress<LootOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 三合一重铸选中的碎片。按 lootId 分组、每组取 3 的倍数、余数保留；
    /// 一组都凑不齐时返回失败结果（界面提示"无法重随"）。
    /// </summary>
    Task<LootOperationResult> RerollLootsAsync(
        IReadOnlyList<(string LootId, int Count)> items,
        IProgress<LootOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>批量升级/解锁选中的碎片为永久物品。</summary>
    Task<LootOperationResult> UpgradeLootsAsync(
        IReadOnlyList<(string LootId, int Count)> items,
        IProgress<LootOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取当前登录账号的全英雄熟练度与里程碑概览。
    /// </summary>
    /// <returns>概览（可为全空的成功结果）；未连接或读取失败时为 <c>null</c>，界面据此区分"失败提示"与"空账号"。</returns>
    Task<MasteryOverview?> GetMasteryOverviewAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取指定召唤师近期对局中的同队队友聚合统计。
    /// </summary>
    /// <param name="puuid">目标玩家 puuid。</param>
    /// <param name="gameIds">候选对局 ID 列表。</param>
    /// <param name="selfPuuid">当前登录账号 puuid（用于查询路人集标记）；若为 null 则使用目标 puuid。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<RecentTeammatesResult> GetRecentTeammatesAsync(
        string puuid,
        IReadOnlyList<long> gameIds,
        string? selfPuuid = null,
        CancellationToken cancellationToken = default);

    /// <summary>清空全部缓存。切换账号或被踢下线后必须调用，否则会看到上一个账号的段位。</summary>
    void InvalidateCache();
}
