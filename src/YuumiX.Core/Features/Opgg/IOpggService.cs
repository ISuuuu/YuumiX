using YuumiX.Core.Features.Opgg.Models;

namespace YuumiX.Core.Features.Opgg;

/// <summary>
/// OP.GG 业务服务接口：负责梯队获取、出装获取、符文与出装写入 LCU 客户端，以及选人阶段英雄锁定联动事件。
/// </summary>
public interface IOpggService
{
    /// <summary>
    /// 选人阶段锁定英雄后发布的出装联动事件流。
    /// </summary>
    IObservable<OpggBuildEvent> ChampSelectBuildEvents { get; }

    /// <summary>
    /// 发布选人出装事件。
    /// </summary>
    void PublishChampSelectBuild(OpggBuildEvent e);

    /// <summary>
    /// 获取英雄梯度榜（根据大区、模式、段位与排位分路）。
    /// </summary>
    Task<IReadOnlyList<TierListItem>> GetTierListAsync(
        string region,
        string mode,
        string tier,
        string? position = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取单英雄出装详情。
    /// </summary>
    Task<OpggBuildPayload?> GetBuildAsync(
        string region,
        string mode,
        string tier,
        int championId,
        string? position = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 一键应用符文页至英雄联盟客户端（含可删除页快照备份与失败自动回滚）。
    /// </summary>
    Task<string> ApplyRunePageAsync(
        string name,
        int primaryStyleId,
        int subStyleId,
        IReadOnlyList<int> selectedPerkIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 一键将推荐出装写入英雄联盟客户端自定义装备集（防覆盖 [Yuumi] 过滤，PUT 持久化）。
    /// </summary>
    Task<string> ApplyItemSetAsync(
        int championId,
        string champName,
        string? position,
        OpggBuildData buildData,
        CancellationToken cancellationToken = default);
}
