using YuumiX.Core.Common;
using YuumiX.Core.Features.GameInfo.Models;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 对局信息编排服务（对应旧版 <c>useGamePlayerData.ts</c>）。
/// </summary>
public interface IGameInfoService
{
    /// <summary>状态流，支持多个订阅者响应式更新</summary>
    EventStream<GameInfoState> StateStream { get; }

    /// <summary>当前最新状态快照</summary>
    GameInfoState CurrentState { get; }

    /// <summary>
    /// 手动刷新状态（拉取 gameflow-phase + 判定会话）。
    /// </summary>
    Task RefreshStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 从当前对局会话加载队伍及玩家。
    /// </summary>
    Task LoadFromGameflowSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 加载当前队伍中全部玩家的详细数据。
    /// </summary>
    Task LoadAllPlayersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 切换 5 列模式下左侧标签页（"my" 我方 或 "their" 敌方）。
    /// </summary>
    void SetActiveTab(string tab);

    /// <summary>
    /// 切换视图列数模式（5 或 10）。
    /// </summary>
    void SetViewColumns(int columns);

    /// <summary>
    /// 加载演示对局数据（供未在对局中时直接预览 10 人透视效果）。
    /// </summary>
    void LoadDemoState();

    /// <summary>
    /// 同步当前玩家最新战绩（10s 节流，force 可跳过）。
    /// SGP 强制拉 0..49，失败回退 IMatchService；命中自己的多个条目统一更新。
    /// </summary>
    Task SyncCurrentPlayerMatchesAsync(bool forceNetwork = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// 通知对局结束：按 2s/3.5s/5s 链式轮询当前玩家战绩；
    /// 阶段重新进入活跃时自动取消。Core 侧已自动检测阶段转换，App 侧可转发 GameLifecycle 事件补触发。
    /// </summary>
    void NotifyGameEnded();
}
