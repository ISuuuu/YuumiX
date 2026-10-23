using YuumiX.Core.Features.Match.Models;

namespace YuumiX.Core.Parsers;

/// <summary>
/// 分路判定：把 LCU 那套互相矛盾的字段收敛成一个 <see cref="MatchPosition"/>。
///
/// 对应原实现 <c>match_parser.rs</c> 的 <c>is_laned_mode</c> / <c>resolve_role_bound_item_position</c> /
/// <c>resolve_position</c> / <c>is_support_item</c> / <c>is_jungle_item</c>，以及 <c>to_display</c> 里
/// 那条五级判定的 <c>position</c> 表达式。之所以把整条链路也搬进来（而不只是各段小函数）：
/// 判定顺序本身就是语义，拆开后调用方很容易自己重排优先级。
///
/// <b>优先级不可调整</b>——实测 21 局中有 3 局 <c>timeline</c> 给出错误值：
/// <list type="bullet">
///   <item><c>real/jungle-none</c>：LCU 报 lane=JUNGLE，实际为上单，靠 <c>roleBoundItem=1220</c> 纠正；</item>
///   <item><c>real/none-duo</c>：lane=NONE / role=DUO，只靠 role 兜底会误判为 BOTTOM，同样靠 1220 纠正；</item>
///   <item><c>real/bottom-carry</c>：<c>roleBoundItem=3006</c>（非分路任务道具）必须降级到 timeline 才得到 BOTTOM。</item>
/// </list>
/// 这四条夹具就是这套优先级的回归证据，任何调序都会让其中至少两条翻车。
/// </summary>
public static class MatchPositionResolver
{
    /// <summary>惩戒（Smite）的技能 ID，命中即判定为打野。</summary>
    public const int SmiteSpellId = 11;

    /// <summary>
    /// 具备分路的 5v5 模式白名单。
    /// </summary>
    /// <remarks>
    /// 自定义对局 3100 **不在**白名单内，因此即使 timeline 给出 NONE/SUPPORT 也不推分路
    /// （见 <c>real/custom-3100</c>）。要支持自定义局分路，先扩这张表，再补夹具。
    /// </remarks>
    private static readonly HashSet<int> LanedQueues =
    [
        400, 420, 430, 440, 480, 490, 700,
        800, 810, 820, 830, 840, 850,
        1300, 1400, 1900,
    ];

    /// <summary>是否属于有分路的召唤师峡谷类模式。</summary>
    public static bool IsLanedMode(int queueId) => LanedQueues.Contains(queueId);

    /// <summary>
    /// 分路任务 / 奖励装备 ID → 分路。
    /// </summary>
    /// <remarks>
    /// 109x 是任务起始装备，1200–1204 是五路任务装备，1205–1211 是完成奖励与打野幼体。
    /// 这是<b>最权威</b>的依据：它由游戏在开局时按玩家实际分路下发，不依赖 LCU 的启发式标注。
    /// </remarks>
    public static MatchPosition? ResolveRoleBoundItem(int itemId) => itemId switch
    {
        1090 or 1200 or 1220 or 1221 or 1222 => MatchPosition.Top,
        1091 or 1201 or 1206 => MatchPosition.Middle,
        1092 or 1202 or 1207 => MatchPosition.Bottom,
        1093 or 1203 or 1208 => MatchPosition.Utility,
        1094 or 1204 or 1205 or 1209 or 1210 or 1211 => MatchPosition.Jungle,
        _ => null,
    };

    /// <summary>是否辅助装（工资装系列）。</summary>
    public static bool IsSupportItem(int itemId) => itemId is (>= 3850 and <= 3871) or (>= 3876 and <= 3884);

    /// <summary>是否打野装 / 打野幼体（1101–1103）。</summary>
    public static bool IsJungleItem(int itemId) => itemId is >= 1101 and <= 1103;

    /// <summary>
    /// 从 LCU <c>timeline</c> 的 <c>lane</c> / <c>role</c> 推导分路。
    ///
    /// 规则（顺序即语义）：
    /// <list type="number">
    ///   <item>role 含 SUPPORT → UTILITY，不论 lane 是 BOTTOM 还是 NONE；</item>
    ///   <item>lane 有具体值 → 按 lane 判定，BOTTOM 再按 role 细分；</item>
    ///   <item>lane 为 NONE / 空 → role 含 CARRY 或 DUO 时判 BOTTOM；</item>
    ///   <item>其余一律 <c>null</c>——不猜。</item>
    /// </list>
    /// </summary>
    public static MatchPosition? ResolveFromTimeline(string? lane, string? role)
    {
        string laneText = lane?.Trim().ToUpperInvariant() ?? string.Empty;
        string roleText = role?.Trim().ToUpperInvariant() ?? string.Empty;

        if (roleText.Contains("SUPPORT", StringComparison.Ordinal))
        {
            return MatchPosition.Utility;
        }

        if (laneText.Length > 0 && laneText != "NONE")
        {
            switch (laneText)
            {
                case "TOP":
                    return MatchPosition.Top;
                case "JUNGLE":
                    return MatchPosition.Jungle;
                case "MIDDLE" or "MID":
                    return MatchPosition.Middle;
                case "BOTTOM" or "BOT":
                    return MatchPosition.Bottom;
            }
        }

        if (roleText.Contains("CARRY", StringComparison.Ordinal) || roleText.Contains("DUO", StringComparison.Ordinal))
        {
            return MatchPosition.Bottom;
        }

        return null;
    }

    /// <summary>
    /// 完整判定链：非 5v5 模式恒为 <c>null</c>，其余按
    /// <c>roleBoundItem → 装备栏分路道具 → 惩戒/辅助装/打野装 → teamPosition → selectedPosition → timeline</c>
    /// 依次尝试。
    /// </summary>
    /// <param name="queueId">队列 ID，用于判断是否具备分路。</param>
    /// <param name="roleBoundItem">LCU <c>stats.roleBoundItem</c>，可能缺失或为 0。</param>
    /// <param name="itemIds">装备栏 7 格（未出装为 0）。</param>
    /// <param name="spell1Id">召唤师技能 1。</param>
    /// <param name="spell2Id">召唤师技能 2。</param>
    /// <param name="lane">LCU <c>timeline.lane</c>。</param>
    /// <param name="role">LCU <c>timeline.role</c>。</param>
    /// <param name="teamPosition">
    /// SGP 参与者上的 <c>teamPosition</c>。只有 SGP 载荷才有，LCU 调用方传 <c>null</c>。
    /// </param>
    /// <param name="selectedPosition">
    /// SGP 参与者上的 <c>selectedPosition</c>（玩家选人时选的位置），可靠性低于 <paramref name="teamPosition"/>。
    /// </param>
    public static MatchPosition? Resolve(
        int queueId,
        int? roleBoundItem,
        IReadOnlyList<int> itemIds,
        int spell1Id,
        int spell2Id,
        string? lane,
        string? role,
        string? teamPosition = null,
        string? selectedPosition = null)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        if (!IsLanedMode(queueId))
        {
            return null;
        }

        if (roleBoundItem is { } boundItem && ResolveRoleBoundItem(boundItem) is { } fromBoundItem)
        {
            return fromBoundItem;
        }

        foreach (int itemId in itemIds)
        {
            if (ResolveRoleBoundItem(itemId) is { } fromItem)
            {
                return fromItem;
            }
        }

        if (spell1Id == SmiteSpellId || spell2Id == SmiteSpellId)
        {
            return MatchPosition.Jungle;
        }

        if (itemIds.Any(IsSupportItem))
        {
            return MatchPosition.Utility;
        }

        if (itemIds.Any(IsJungleItem))
        {
            return MatchPosition.Jungle;
        }

        // SGP 比 LCU 多两级更可靠的依据：teamPosition 是官方按队伍结构分配的，
        // selectedPosition 只反映选人时的意向，故 teamPosition 优先。
        if (MatchPositionText.TryParseLcuName(teamPosition) is { } fromTeamPosition)
        {
            return fromTeamPosition;
        }

        if (MatchPositionText.TryParseLcuName(selectedPosition) is { } fromSelectedPosition)
        {
            return fromSelectedPosition;
        }

        return ResolveFromTimeline(lane, role);
    }
}
