using YuumiX.Core.Features.GameInfo.Models;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 预组队分析器（纯函数）。
/// 对应旧版 <c>usePremadeGroup.ts</c>。
/// </summary>
public static class PremadeAnalyzer
{
    /// <summary>
    /// 根据 <c>teamParticipantId</c> 或 <c>partyId</c> 分组，计算队伍组队颜色索引。
    /// 单人开黑返回 -1（无高亮），多人开黑返回递增索引 0, 1, 2...
    /// </summary>
    public static Dictionary<string, int> ComputePremadeColors(IReadOnlyList<PremadePlayerLike>? team)
    {
        var result = new Dictionary<string, int>();
        if (team is null || team.Count == 0)
        {
            return result;
        }

        var tIdToMembers = new Dictionary<string, List<PremadePlayerLike>>();

        foreach (var p in team)
        {
            string? tpid = GetValidTpid(p);
            if (string.IsNullOrEmpty(tpid))
            {
                continue;
            }

            if (!tIdToMembers.TryGetValue(tpid, out var list))
            {
                list = [];
                tIdToMembers[tpid] = list;
            }

            list.Add(p);
        }

        int currentColor = 0;

        foreach (var members in tIdToMembers.Values)
        {
            int colorToAssign = members.Count == 1 ? -1 : currentColor++;
            foreach (var p in members)
            {
                AssignPlayerColor(result, p, colorToAssign);
            }
        }

        return result;
    }

    /// <summary>
    /// 判断颜色映射中是否包含有效开黑组队（至少有一组索引 >= 0）。
    /// </summary>
    public static bool HasPremadeGroup(IReadOnlyDictionary<string, int>? colors)
    {
        return colors is not null && colors.Values.Any(v => v >= 0);
    }

    /// <summary>
    /// 构造用于界面的预组队分组列表（按颜色索引升序排列）。
    /// </summary>
    public static IReadOnlyList<PremadeGroup> BuildPremadeGroups(
        IReadOnlyList<PremadePlayerLike>? team,
        IReadOnlyDictionary<string, int>? colors,
        IReadOnlyDictionary<string, GameInfoPlayer>? playerDataMap)
    {
        if (team is null || team.Count == 0 || colors is null)
        {
            return [];
        }

        var groupMap = new Dictionary<int, List<PremadeMember>>();

        foreach (var p in team)
        {
            int colorIdx = GetPlayerColorIdx(p, colors);
            if (colorIdx < 0)
            {
                continue;
            }

            if (!groupMap.TryGetValue(colorIdx, out var members))
            {
                members = [];
                groupMap[colorIdx] = members;
            }

            GameInfoPlayer? pData = FindPlayerData(p, playerDataMap);

            int champId = p.ChampionId > 0
                ? p.ChampionId
                : (p.ChampionPickIntent > 0 ? p.ChampionPickIntent : pData?.ChampionId ?? 0);

            string name = pData?.Info?.GameName
                          ?? pData?.Info?.DisplayName
                          ?? p.DisplayName
                          ?? p.SummonerName
                          ?? string.Empty;

            long sid = p.SummonerId != 0 ? p.SummonerId : p.CellId;

            members.Add(new PremadeMember(sid, name, champId));
        }

        return groupMap
            .OrderBy(kv => kv.Key)
            .Select(kv => new PremadeGroup(kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>
    /// 获取指定玩家在当前颜色映射中的组队颜色索引。
    /// </summary>
    public static int GetPlayerColorIdx(PremadePlayerLike? p, IReadOnlyDictionary<string, int>? colors)
    {
        if (p is null || colors is null)
        {
            return -1;
        }

        if (!string.IsNullOrEmpty(p.Puuid) && colors.TryGetValue(p.Puuid, out int cPuuid))
        {
            return cPuuid;
        }

        if (p.SummonerId != 0 && colors.TryGetValue(p.SummonerId.ToString(), out int cSid))
        {
            return cSid;
        }

        if (colors.TryGetValue(p.CellId.ToString(), out int cCell))
        {
            return cCell;
        }

        return -1;
    }

    private static void AssignPlayerColor(Dictionary<string, int> result, PremadePlayerLike p, int color)
    {
        if (!string.IsNullOrEmpty(p.Puuid))
        {
            result[p.Puuid] = color;
        }

        if (p.SummonerId != 0)
        {
            result[p.SummonerId.ToString()] = color;
        }

        result[p.CellId.ToString()] = color;
    }

    private static string? GetValidTpid(PremadePlayerLike p)
    {
        string? tpid = p.TeamParticipantId;
        if (!string.IsNullOrEmpty(tpid) && tpid != "0")
        {
            return tpid;
        }

        string? partyId = p.PartyId;
        if (!string.IsNullOrEmpty(partyId) && partyId != "0")
        {
            return partyId;
        }

        return null;
    }

    private static GameInfoPlayer? FindPlayerData(
        PremadePlayerLike p,
        IReadOnlyDictionary<string, GameInfoPlayer>? playerDataMap)
    {
        if (playerDataMap is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(p.Puuid) && playerDataMap.TryGetValue(p.Puuid, out var byPuuid))
        {
            return byPuuid;
        }

        if (playerDataMap.TryGetValue(p.CellId.ToString(), out var byCell))
        {
            return byCell;
        }

        if (p.SummonerId != 0 && playerDataMap.TryGetValue(p.SummonerId.ToString(), out var bySid))
        {
            return bySid;
        }

        return null;
    }
}
