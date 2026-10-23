using YuumiX.Core.Automation.Models;

namespace YuumiX.Core.Automation;

/// <summary>
/// 写操作目标验证异常。
/// </summary>
public sealed class ActionVerificationException : Exception
{
    public ActionVerificationException(string message, Exception? inner = null) : base(message, inner) { }

    public static ActionVerificationException LifecycleStale(StaleGenerationException inner) =>
        new($"生命周期失效: {inner.Message}", inner);

    public static ActionVerificationException ActionNotFound(int actionId) =>
        new($"未找到目标 Action: {actionId}");

    public static ActionVerificationException ActionTypeMismatch(string expected, string actual) =>
        new($"Action 类型不匹配: 期望={expected}, 实际={actual}");

    public static ActionVerificationException ActorMismatch(int expected, int actual) =>
        new($"Action 席位不匹配: 期望={expected}, 实际={actual}");

    public static ActionVerificationException ActionAlreadyCompleted(int actionId) =>
        new($"Action {actionId} 已完成，拒绝重复写入");
}

/// <summary>
/// 受保护的 BP 操作租约（ActionLease，对应旧版 agents/action.rs::ActionLease）。
/// 绑定了代际守卫与具体操作槽位，在写操作前做终极双重断言（消除 TOCTOU 竞态）。
/// </summary>
public sealed record ActionLease(
    GenerationGuard Guard,
    int ActionId,
    int ActorCellId,
    string ActionType,
    int ExpectedChampionId)
{
    /// <summary>
    /// 在真正向 LCU 发送写操作之前，结合最新的生命周期状态与最新 session 结构进行终极双重断言。
    /// </summary>
    public void VerifyTarget(
        LifecycleState currentLifecycle,
        ChampSelectSession freshSession)
    {
        ArgumentNullException.ThrowIfNull(currentLifecycle);
        ArgumentNullException.ThrowIfNull(freshSession);

        // 1. 先验证生命周期代际与 session 是否严格有效
        try
        {
            Guard.EnsureValid(currentLifecycle);
        }
        catch (StaleGenerationException ex)
        {
            throw ActionVerificationException.LifecycleStale(ex);
        }

        // 2. 验证当前席位一致性
        if (freshSession.LocalPlayerCellId != ActorCellId)
        {
            throw ActionVerificationException.ActorMismatch(ActorCellId, freshSession.LocalPlayerCellId);
        }

        // 3. 寻找 freshSession 中的目标 action 并比对属性
        ChampSelectAction? target = null;
        foreach (var group in freshSession.Actions)
        {
            foreach (var action in group)
            {
                if (action.Id == ActionId)
                {
                    target = action;
                    break;
                }
            }
            if (target is not null) break;
        }

        if (target is null)
        {
            throw ActionVerificationException.ActionNotFound(ActionId);
        }

        if (target.ActorCellId != ActorCellId)
        {
            throw ActionVerificationException.ActorMismatch(ActorCellId, target.ActorCellId);
        }

        if (!string.Equals(target.ActionType, ActionType, StringComparison.OrdinalIgnoreCase))
        {
            throw ActionVerificationException.ActionTypeMismatch(ActionType, target.ActionType);
        }

        if (target.Completed)
        {
            throw ActionVerificationException.ActionAlreadyCompleted(ActionId);
        }
    }
}
