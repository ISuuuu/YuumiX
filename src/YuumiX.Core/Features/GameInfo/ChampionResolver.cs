using YuumiX.Core.Features.GameInfo.Models;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 选人阶段 Action 条目。
/// </summary>
public sealed record ChampSelectActionLike(
    int ActorCellId,
    int ChampionId,
    bool Completed,
    bool IsInProgress,
    string Type);

/// <summary>
/// 选人会话队伍成员条目。
/// </summary>
public sealed record ChampSelectMemberLike(
    int CellId,
    int ChampionId = 0,
    int ChampionPickIntent = 0,
    string? Puuid = null,
    long SummonerId = 0);

/// <summary>
/// 选人会话快照。
/// </summary>
public sealed record ChampSelectSessionLike(
    IReadOnlyList<IReadOnlyList<ChampSelectActionLike>>? Actions = null,
    IReadOnlyList<ChampSelectMemberLike>? MyTeam = null,
    IReadOnlyList<ChampSelectMemberLike>? TheirTeam = null);

/// <summary>
/// 选人与对局中的玩家英雄 ID 解析器（纯函数）。
/// 对应旧版 <c>resolvePlayerChampionId</c>。
/// </summary>
public static class ChampionResolver
{
    /// <summary>
    /// 解析玩家当前选择/锁定的英雄 ID。
    /// 优先级：进行中 pick > 已确定/锁定 > 已完成 pick 动作 > 预选意图 > 人机英雄 > 历史英雄。
    /// </summary>
    public static int ResolvePlayerChampionId(
        PremadePlayerLike? player,
        ChampSelectSessionLike? session = null)
    {
        if (player is null)
        {
            return 0;
        }

        if (session is not null)
        {
            // 判定玩家归属阵营：优先使用明确的 isEnemy 字段；若无，通过 puuid/summonerId 在 theirTeam 匹配判定
            // 严禁使用 cellId >= 5 判定敌方（选人阶段我方若为红方，我方在 myTeam 中的真实 cellId 即为 5..9）
            bool isEnemy = player.IsEnemy || (session.TheirTeam ?? []).Any(m =>
                (!string.IsNullOrEmpty(player.Puuid) && m.Puuid == player.Puuid) ||
                (player.SummonerId != 0 && m.SummonerId == player.SummonerId));

            var searchPool = isEnemy ? (session.TheirTeam ?? []) : (session.MyTeam ?? []);

            ChampSelectMemberLike? match = searchPool.FirstOrDefault(m =>
                (!string.IsNullOrEmpty(player.Puuid) && m.Puuid == player.Puuid) ||
                (player.SummonerId != 0 && m.SummonerId == player.SummonerId) ||
                m.CellId == player.CellId);

            // 跨阵营身份兜底（仅限真实 puuid / summonerId，严禁跨阵营按 cellId 匹配）
            if (match is null && (!string.IsNullOrEmpty(player.Puuid) || player.SummonerId != 0))
            {
                var allMembers = (session.MyTeam ?? []).Concat(session.TheirTeam ?? []);
                match = allMembers.FirstOrDefault(m =>
                    (!string.IsNullOrEmpty(player.Puuid) && m.Puuid == player.Puuid) ||
                    (player.SummonerId != 0 && m.SummonerId == player.SummonerId));
            }

            if (isEnemy && match is null && string.IsNullOrEmpty(player.Puuid) && player.SummonerId == 0)
            {
                // 敌方匿名槽位未匹配到真实 session 成员，不尝试 actions 兜底，防止误用我方选人动作
            }
            else
            {
                int? actualCellId = match?.CellId;

                // 1.1 最高时效性：正在进行中的 Pick 动作（玩家在选人框中刚点击/悬停挑选英雄，尚未锁定）
                if (session.Actions is not null && actualCellId.HasValue)
                {
                    foreach (var group in session.Actions)
                    {
                        foreach (var act in group)
                        {
                            if (act.ActorCellId == actualCellId.Value &&
                                string.Equals(act.Type, "pick", StringComparison.OrdinalIgnoreCase) &&
                                act.IsInProgress &&
                                act.ChampionId > 0)
                            {
                                return act.ChampionId;
                            }
                        }
                    }
                }

                // 1.2 选人会话成员上的已确定/锁定/ARAM随机分配与换选英雄
                if (match is { ChampionId: > 0 })
                {
                    return match.ChampionId;
                }

                // 1.3 已完成的 Pick 动作（已锁定，但 member.championId 尚未同步到位的极短过渡微帧）
                if (session.Actions is not null && actualCellId.HasValue)
                {
                    foreach (var group in session.Actions)
                    {
                        foreach (var act in group)
                        {
                            if (act.ActorCellId == actualCellId.Value &&
                                string.Equals(act.Type, "pick", StringComparison.OrdinalIgnoreCase) &&
                                act.Completed &&
                                act.ChampionId > 0)
                            {
                                return act.ChampionId;
                            }
                        }
                    }
                }

                // 1.4 预选意图（Pick Intent：尚未轮到该玩家挑选时，展示其计划选择的英雄）
                if (match is { ChampionPickIntent: > 0 })
                {
                    return match.ChampionPickIntent;
                }
            }
        }

        // 2. 玩家对象自带预选意图兜底
        if (player.ChampionPickIntent > 0)
        {
            return player.ChampionPickIntent;
        }

        // 3. 人机英雄兜底
        if (player.BotChampionId > 0)
        {
            return player.BotChampionId;
        }

        // 4. 玩家已有历史英雄兜底
        if (player.ChampionId > 0)
        {
            return player.ChampionId;
        }

        return 0;
    }
}
