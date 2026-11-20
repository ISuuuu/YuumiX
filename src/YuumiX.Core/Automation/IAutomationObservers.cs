using YuumiX.Core.Lcu;

namespace YuumiX.Core.Automation;

/// <summary>
/// 接收游戏阶段变化通知的自动化代理接口。
/// </summary>
public interface IGamePhaseObserver
{
    /// <summary>响应游戏阶段变化。</summary>
    void OnPhaseChanged(GamePhase phase);
}

/// <summary>
/// 标记代理需要参与 LCU 连接成功后的阶段对账。
/// 未实现该接口的 <see cref="IGamePhaseObserver"/> 仅由实时事件流驱动，
/// 避免对助手启动前已推进到一半的对局重复触发。
/// </summary>
public interface IPhaseReconcileObserver : IGamePhaseObserver;

/// <summary>
/// 接收 LCU 断连重置通知的自动化代理接口。
/// 实现该接口的代理在 LCU 断连时收到 <see cref="OnConnectionReset"/>，
/// 不再额外接收 <see cref="GamePhase.None"/> 回退通知。
/// </summary>
public interface IConnectionResetObserver
{
    /// <summary>响应 LCU 连接断开后的状态重置。</summary>
    void OnConnectionReset();
}

/// <summary>
/// 接收特定 LCU WebSocket 事件的自动化代理接口。
/// </summary>
public interface ILcuEventObserver
{
    /// <summary>判断是否订阅指定 URI 的 LCU 事件。</summary>
    bool CanHandle(string uri);

    /// <summary>处理匹配的 LCU 事件。</summary>
    ValueTask OnLcuEventAsync(LcuEvent evt);
}
