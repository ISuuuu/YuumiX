using YuumiX.Core.Features.Match.Models;

namespace YuumiX.Core.Parsers;

/// <summary>
/// 分路判定：把 LCU 那套互相矛盾的字段收敛成一个 <see cref="MatchPosition"/>。
///
/// 对应原实现 <c>match_parser.rs</c> 的 <c>is_laned_mode</c> / <c>resolve_role_bound_item_position</c> /
/// <c>resolve_position</c> / <c>is_support_item</c> / <c>is_jungle_item</c>。之所以把整条链路也搬进来
/// （而不只是各段小函数）：判定顺序本身就是语义，拆开后调用方很容易自己重排优先级。
///
/// <para><b>不再用 <c>timeline.lane</c> / <c>timeline.role</c> 兜底（2026-10-29 起）</b></para>
/// 旧实现把 timeline 作为最后一档，实测在国服 430（盲选匹配）里 <c>role</c> 对<b>全部 10 人</b>恒为
/// <c>SUPPORT</c>（上单、中单、下路 AD 一律如此），照它兜底等于把整局判成五个辅助；<c>lane</c> 同样会错
/// （<c>real/jungle-none</c> 夹具：lane=JUNGLE、实际为上单）。既然两个维度都不可靠，它就不该参与推断——
/// <b>拿不准时判 <c>null</c></b>，界面不显示分路，好过显示一个错的。
///
/// <para><b>roleBoundItem 是权威依据，且恰好是每队完整五路分区（实测 50 局 / 500 人次）</b></para>
/// 该字段由游戏在开局按分路槽位下发，<b>同队五人的值互不重复</b>，恰好覆盖五个分路（96/100 队成立，
/// 余下 4 队全员为 0）。各分路的取值集合：
/// <list type="bullet">
///   <item>上：1200 / 1220 / 1221 / 1222；中：1201 / 1206；野：1204 / 1205 / 1209 / 1210 / 1211；</item>
///   <item>下：1202 / 1207，<b>以及全部靴子（1001、2422、3006、3008、3009、3020、3047、3111、3158、3117 及其 31xx 升级版）</b>——
///         下路任务的奖励就是"靴子升级"，所以下路玩家的字段值记录的是被绑定的那双鞋；</item>
///   <item>辅：1203 / 1208，<b>以及 2055 控制守卫</b>——辅助任务的绑定物是真眼。</item>
/// </list>
/// 样板：<c>gid=11420374915</c> 两队下行位都是射手（提莫、崔丝塔娜），两人字段值都是 <c>3006 狂战士胫甲</c>，
/// 而 1202 一次都没出现；同局辅助菲兹的字段值是 <c>2055</c>。因此<b>不能</b>把靴子 / 2055 当成"非任务道具"排除。
///
/// <para><b>为什么靴子只能在字段上判定，不能进装备栏扫描</b></para>
/// 靴子是任何分路都会买的通用装备；若把 <see cref="IsBootsItem"/> 混进"按装备栏遍历"的那一支，
/// 穿鞋的上单/中单会被一律判成下路。所以拆成两个入口：<see cref="ResolveRoleBoundPosition"/> 只喂字段值，
/// <see cref="ResolveRoleBoundItem"/> 只认任务道具、继续供装备栏扫描使用。
///
/// <para><b>字段缺失时的替补信号，按可靠性排序</b></para>
/// <list type="number">
///   <item>装备栏里的分路任务道具——与字段同源，只是玩家把它放进了背包；</item>
///   <item>惩戒（唯一带惩戒的分路就是打野）、辅助装、打野幼体——装备语义直接指向分路；</item>
///   <item>SGP 的 <c>individualPosition</c>——官方按局内站位与经济推演；</item>
///   <item><c>teamPosition</c>、<c>selectedPosition</c>——队伍结构槽位与选人意向，可靠性依次递减
///         （实测 teamPosition 40 局错 3 局，如 <c>gid=11420374915</c> 的 AD 被标成 UTILITY）；</item>
///   <item>其余一律 <c>null</c>——极短重开局、既没出装也没下发任务道具时，本来就没有可信信号，
///         此时宁可不显示分路。</item>
/// </list>
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
    /// <b>只认任务道具</b>——本方法同时供"装备栏 7 格扫描"使用，若把靴子 / 2055 之类的通用装备
    /// 混进来，穿鞋的上单会被判成下路。读 <c>roleBoundItem</c> 字段请改用
    /// <see cref="ResolveRoleBoundPosition"/>。
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

    /// <summary>控制守卫（真眼）ID：辅助任务的绑定物，只在 <c>roleBoundItem</c> 上有分路含义。</summary>
    public const int ControlWardItemId = 2055;

    /// <summary>
    /// 靴子类装备：基础鞋（含 1001 鞋子、2422 有点神奇之鞋）与 31xx 升级版。
    /// </summary>
    /// <remarks>
    /// 数据源为 <c>items.json</c> 中 <c>categories</c> 含 <c>Boots</c> 的条目（已剔除大乱斗 / 竞技场专用变体）。
    /// 升级关系是独占的一对一（3006→3172、3009→3170、3020→3175、3047→3174、3111→3173、3158→3171、3008→3168、3013→3176），
    /// 这正是"下路任务奖励 = 靴子升级"的证据。
    /// </remarks>
    private static readonly HashSet<int> BootsItemIds =
    [
        1001, 1111, 2422, 3005, 3006, 3008, 3009, 3010, 3013, 3020, 3047, 3111, 3117, 3158,
        3168, 3170, 3171, 3172, 3173, 3174, 3175, 3176,
    ];

    /// <summary>是否靴子类装备。</summary>
    public static bool IsBootsItem(int itemId) => BootsItemIds.Contains(itemId);

    /// <summary>
    /// <c>roleBoundItem</c> 字段专用映射：任务道具 → 靴子（下路）→ 2055 控制守卫（辅助）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ResolveRoleBoundItem"/> 的差别只有"额外接受通用装备"，因此<b>只能</b>传字段值，
    /// 不能拿装备栏里的每一格来试。实测 50 局 500 人次：
    /// 靴子类 83 条中 80 条落在下路（96.4%），控制守卫 28 条中 24 条落在辅助（85.7%）；
    /// 余下分歧经逐局核对，属盲选换线（同队真射手拿到 1220 上路奖励）或 <c>individualPosition</c> 自身出错，
    /// 字段本身是按槽位分配的完整五路分区，自洽性优于任何启发式字段。
    /// </remarks>
    public static MatchPosition? ResolveRoleBoundPosition(int itemId)
    {
        if (ResolveRoleBoundItem(itemId) is { } fromQuestItem)
        {
            return fromQuestItem;
        }

        if (IsBootsItem(itemId))
        {
            return MatchPosition.Bottom;
        }

        return itemId == ControlWardItemId ? MatchPosition.Utility : null;
    }

    /// <summary>是否辅助装（工资装系列）。</summary>
    public static bool IsSupportItem(int itemId) => itemId is (>= 3850 and <= 3871) or (>= 3876 and <= 3884);

    /// <summary>是否打野装 / 打野幼体（1101–1103）。</summary>
    public static bool IsJungleItem(int itemId) => itemId is >= 1101 and <= 1103;

    /// <summary>
    /// 完整判定链：非 5v5 模式恒为 <c>null</c>，其余按
    /// <c>roleBoundItem → 装备栏分路道具 → 惩戒/辅助装/打野装 → individualPosition → teamPosition → selectedPosition</c>
    /// 依次尝试，全部落空则返回 <c>null</c>（不猜）。
    /// </summary>
    /// <param name="queueId">队列 ID，用于判断是否具备分路。</param>
    /// <param name="roleBoundItem">
    /// LCU <c>stats.roleBoundItem</c>，可能缺失或为 0。取值含任务道具、靴子（下路）与控制守卫（辅助），
    /// 见 <see cref="ResolveRoleBoundPosition"/>。
    /// </param>
    /// <param name="itemIds">装备栏 7 格（未出装为 0）。</param>
    /// <param name="spell1Id">召唤师技能 1。</param>
    /// <param name="spell2Id">召唤师技能 2。</param>
    /// <param name="individualPosition">
    /// SGP 参与者上的 <c>individualPosition</c>（官方按实际站位/经济推演的分路）。
    /// 只有 SGP 载荷才有，LCU 调用方传 <c>null</c>。
    /// </param>
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
        string? individualPosition = null,
        string? teamPosition = null,
        string? selectedPosition = null)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        if (!IsLanedMode(queueId))
        {
            return null;
        }

        if (roleBoundItem is { } boundItem && ResolveRoleBoundPosition(boundItem) is { } fromBoundItem)
        {
            return fromBoundItem;
        }

        // 装备栏这一支只认任务道具（ResolveRoleBoundItem）：靴子、控制守卫是所有人都可能买的通用装备，
        // 混进来会把穿鞋的上单判成下路。
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

        // SGP 比 LCU 多三级依据，其中 individualPosition 最可靠：它是官方按实际站位与经济
        // 推演出来的分路，而 teamPosition 读的是队伍结构里的位置槽——盲选局里位置槽经常与实际不符
        // （实测 40 局里 teamPosition 错 3 局，individualPosition 只在分路任务装备自相矛盾时给出不同值）。
        // 所以 individualPosition 必须排在 teamPosition 之前。
        if (MatchPositionText.TryParseLcuName(individualPosition) is { } fromIndividualPosition)
        {
            return fromIndividualPosition;
        }

        // teamPosition 是官方按队伍结构分配的，可靠性高于只反映选人意向的 selectedPosition。
        if (MatchPositionText.TryParseLcuName(teamPosition) is { } fromTeamPosition)
        {
            return fromTeamPosition;
        }

        if (MatchPositionText.TryParseLcuName(selectedPosition) is { } fromSelectedPosition)
        {
            return fromSelectedPosition;
        }

        // 到这一步说明既没有分路任务绑定物、也没出任何带分路语义的装备/技能、SGP 位置字段也缺失
        // （典型是极短重开局）。不拿 timeline 兜底：盲选局 role 恒为 SUPPORT、lane 也常与实位不符，
        // 用它只会把 AD 判成辅助。返回 null，界面即不显示分路。
        return null;
    }
}
