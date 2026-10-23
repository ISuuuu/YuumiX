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
}
