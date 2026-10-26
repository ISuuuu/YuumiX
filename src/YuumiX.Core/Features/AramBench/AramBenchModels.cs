namespace YuumiX.Core.Features.AramBench;

/// <summary>
/// 一次「抢/换板凳席英雄」的结果。
///
/// 为什么需要枚举而不是 <c>bool</c>：LCU 的 <c>bench/swap</c> 接口语义含糊——
/// 前 15 秒队友保护期内它返回 2xx 但不生效，对队友的英雄同样如此。
/// 因此「HTTP 成功」与「真的换到了」是两件事，必须分开表达，
/// 否则 UI 只能统一提示「前 15 秒保护期」（旧版即如此），
/// 无法区分「未拥有该英雄」这种本可以避免的误点。
/// </summary>
public enum AramBenchSwapOutcome
{
    /// <summary>交换已生效（会话回推确认手持英雄变为目标英雄）。</summary>
    Swapped,

    /// <summary>本局不在选人阶段，或拿不到传输层。</summary>
    NoSession,

    /// <summary>账号未拥有该英雄且不在周免内。</summary>
    NotOwned,

    /// <summary>处于前 15 秒队友保护期，或目标是队友手持的英雄（LCU 静默不生效）。</summary>
    Protected,

    /// <summary>请求层面失败（网络异常、非预期状态码）。</summary>
    Failed,
}

/// <summary>
/// 板凳席上的一张英雄卡，附带是否可点与不可点的原因。
///
/// <see cref="Reason"/> 是给 tooltip 与提示条用的中文说明，**不用于程序判断**：
/// 判断一律看 <see cref="Clickable"/>，避免文案调整破坏逻辑（AGENTS.md §4）。
/// </summary>
public sealed record AramBenchChampion(int ChampionId, bool Clickable, string Reason);

/// <summary>
/// 悬浮窗渲染所需的完整快照。
///
/// 用记录类型而非可变类：UI 侧 <c>UseState</c> 只需替换引用即可触发重渲染，
/// 且值相等语义让「连续两次内容相同的事件」不产生多余渲染。
/// </summary>
/// <param name="IsChampSelect">当前是否处于选人阶段。</param>
/// <param name="BenchEnabled">LCU 是否报告本局启用了板凳席（只有大乱斗族模式为 true）。</param>
/// <param name="IsProtected">是否处于前 15 秒队友保护期。</param>
/// <param name="Champions">板凳席英雄（已按 LCU 顺序排列，已滤掉非法 ID）。</param>
public sealed record AramBenchState(
    bool IsChampSelect,
    bool BenchEnabled,
    bool IsProtected,
    IReadOnlyList<AramBenchChampion> Champions)
{
    /// <summary>空闲态：未进入选人阶段时的唯一取值。</summary>
    public static readonly AramBenchState Idle = new(false, false, false, []);
}
