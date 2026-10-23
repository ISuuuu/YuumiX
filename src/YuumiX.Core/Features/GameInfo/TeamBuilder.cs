using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 队伍构建上下文。
/// </summary>
public sealed record TeamBuildContext
{
    public long CurrentSummonerId { get; init; }
    public string? CurrentSummonerPuuid { get; init; }
    public string? MapSide { get; init; }
    public IReadOnlyList<PremadePlayerLike> ChampSelectTeamSnapshot { get; init; } = [];
    public IReadOnlyList<PremadePlayerLike> ChampSelectTheirTeamSnapshot { get; init; } = [];
    public IReadOnlyList<PremadePlayerLike> SessionAllyTeam { get; init; } = [];
    public IReadOnlyList<PremadePlayerLike> SessionEnemyTeam { get; init; } = [];
    public IReadOnlyList<PremadePlayerLike> GameflowMyTeam { get; init; } = [];
    public IReadOnlyList<PremadePlayerLike> GameflowTheirTeam { get; init; } = [];
    public IReadOnlyDictionary<string, GameInfoPlayer> PlayerData { get; init; } = new Dictionary<string, GameInfoPlayer>();
}

/// <summary>
/// 阵营与队伍分析构建器（纯函数）。
/// 对应旧版 <c>teamBuilder.ts</c>。
/// </summary>
public static class TeamBuilder
{
    /// <summary>
    /// 判定 teamOne / teamTwo 哪边是我方。
    /// teamOne 在 LOL 中恒为蓝方，teamTwo 恒为红方。
    /// 严禁使用 cellId >= 5 判定敌方！
    /// </summary>
    public static (IReadOnlyList<PremadePlayerLike> AllyTeam, IReadOnlyList<PremadePlayerLike> EnemyTeam, string? Side) ResolveTeamSide(
        IReadOnlyList<PremadePlayerLike> teamOne,
        IReadOnlyList<PremadePlayerLike> teamTwo,
        TeamBuildContext ctx)
    {
        bool CheckMatches(PremadePlayerLike p)
        {
            bool matchLocal = (ctx.CurrentSummonerId != 0 && p.SummonerId == ctx.CurrentSummonerId) ||
                              (!string.IsNullOrEmpty(ctx.CurrentSummonerPuuid) && !string.IsNullOrEmpty(p.Puuid) &&
                               string.Equals(p.Puuid, ctx.CurrentSummonerPuuid, StringComparison.OrdinalIgnoreCase));
            if (matchLocal)
            {
                return true;
            }

            return ctx.ChampSelectTeamSnapshot.Any(cs =>
                (!string.IsNullOrEmpty(p.Puuid) && !string.IsNullOrEmpty(cs.Puuid) &&
                 string.Equals(p.Puuid, cs.Puuid, StringComparison.OrdinalIgnoreCase)) ||
                (p.SummonerId != 0 && cs.SummonerId != 0 && p.SummonerId == cs.SummonerId) ||
                (!string.IsNullOrEmpty(p.SummonerName) && !string.IsNullOrEmpty(cs.DisplayName) &&
                 string.Equals(p.SummonerName, cs.DisplayName, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(p.GameName) && !string.IsNullOrEmpty(cs.GameName) &&
                 string.Equals(p.GameName, cs.GameName, StringComparison.OrdinalIgnoreCase)));
        }

        bool isTeamOne = teamOne.Any(CheckMatches);
        bool isTeamTwo = teamTwo.Any(CheckMatches);

        if (isTeamOne)
        {
            return (teamOne, teamTwo, "blue");
        }

        if (isTeamTwo)
        {
            return (teamTwo, teamOne, "red");
        }

        // 脱敏对局（排位赛等）：未直接匹配到当前玩家，结合多重信息推断阵营
        string? determinedSide = ctx.MapSide;

        // 1. 若当前 mapSide 未设置，从选人阶段快照 cellId 推断（蓝方 0..4，红方 5..9）
        if (string.IsNullOrEmpty(determinedSide) && ctx.ChampSelectTeamSnapshot.Count > 0)
        {
            bool hasRedCell = ctx.ChampSelectTeamSnapshot.Any(cs => cs.CellId >= 5);
            bool hasBlueCell = ctx.ChampSelectTeamSnapshot.Any(cs => cs.CellId < 5);
            if (hasRedCell && !hasBlueCell)
            {
                determinedSide = "red";
            }
            else if (hasBlueCell && !hasRedCell)
            {
                determinedSide = "blue";
            }
        }

        if (string.Equals(determinedSide, "red", StringComparison.OrdinalIgnoreCase))
        {
            return (
                teamTwo.Count > 0 ? teamTwo : teamOne,
                teamTwo.Count > 0 ? teamOne : teamTwo,
                "red");
        }

        if (string.Equals(determinedSide, "blue", StringComparison.OrdinalIgnoreCase))
        {
            return (
                teamOne.Count > 0 ? teamOne : teamTwo,
                teamOne.Count > 0 ? teamTwo : teamOne,
                "blue");
        }

        // 最终兜底：非空队伍优先作为 allyTeam
        return (
            teamOne.Count > 0 ? teamOne : teamTwo,
            teamOne.Count > 0 ? teamTwo : teamOne,
            teamOne.Count > 0 ? "blue" : (teamTwo.Count > 0 ? "red" : null));
    }

    /// <summary>
    /// 把 gameflow 双方参与者映射为队伍成员。
    /// 蓝方 (100) 对应 cellId 0..4；红方 (200) 对应 cellId 5..9。
    /// 我方是红方还是蓝方，决定了 0..4 与 5..9 属于我方还是敌方！
    /// </summary>
    public static (IReadOnlyList<PremadePlayerLike> MappedMy, IReadOnlyList<PremadePlayerLike> MappedTheir) BuildGameflowTeams(
        IReadOnlyList<PremadePlayerLike> allyTeam,
        IReadOnlyList<PremadePlayerLike> enemyTeam,
        string? mapSide,
        TeamBuildContext ctx)
    {
        bool isAllyRed = string.Equals(mapSide, "red", StringComparison.OrdinalIgnoreCase);
        int allyOffset = isAllyRed ? 5 : 0;
        int enemyOffset = isAllyRed ? 0 : 5;

        var mappedMy = allyTeam.Select((p, idx) => MapParticipant(p, idx, allyOffset, false, ctx)).ToList();
        var mappedTheir = enemyTeam.Select((p, idx) => MapParticipant(p, idx, enemyOffset, true, ctx)).ToList();

        return (mappedMy, mappedTheir);
    }

    private static PremadePlayerLike MapParticipant(
        PremadePlayerLike p,
        int idx,
        int offset,
        bool isEnemy,
        TeamBuildContext ctx)
    {
        string resolvedName = !string.IsNullOrEmpty(p.GameName)
            ? (!string.IsNullOrEmpty(p.TagLine) ? $"{p.GameName}#{p.TagLine}" : p.GameName)
            : (!string.IsNullOrEmpty(p.SummonerName)
                ? p.SummonerName
                : (!string.IsNullOrEmpty(p.DisplayName)
                    ? p.DisplayName
                    : (!string.IsNullOrEmpty(p.BotName)
                        ? p.BotName
                        : (p.Bot || p.IsBot ? "电脑" : string.Empty))));

        int stableCellId = offset + idx;

        int resolvedChampId = p.ChampionId > 0 ? p.ChampionId : p.BotChampionId;
        int profileIconId = p.ProfileIconId;
        var targetSnapshot = isEnemy ? ctx.ChampSelectTheirTeamSnapshot : ctx.ChampSelectTeamSnapshot;

        PremadePlayerLike? snap = FindInSnapshot(p, targetSnapshot);
        if (snap is not null)
        {
            if (resolvedChampId <= 0)
            {
                resolvedChampId = snap.ChampionId > 0 ? snap.ChampionId : snap.ChampionPickIntent;
            }

            if (profileIconId <= 0 && snap.ProfileIconId > 0)
            {
                profileIconId = snap.ProfileIconId;
            }
        }

        if (resolvedChampId <= 0)
        {
            var prevTeam = isEnemy ? ctx.GameflowTheirTeam : ctx.GameflowMyTeam;
            var prevPlayer = prevTeam.FirstOrDefault(pp =>
                (!string.IsNullOrEmpty(p.Puuid) && !string.IsNullOrEmpty(pp.Puuid) && pp.Puuid == p.Puuid) ||
                (p.SummonerId != 0 && pp.SummonerId != 0 && pp.SummonerId == p.SummonerId) ||
                pp.CellId == stableCellId);
            if (prevPlayer is { ChampionId: > 0 })
            {
                resolvedChampId = prevPlayer.ChampionId;
            }
        }

        if (resolvedChampId <= 0)
        {
            GameInfoPlayer? pd = null;
            if (!string.IsNullOrEmpty(p.Puuid))
            {
                ctx.PlayerData.TryGetValue(p.Puuid, out pd);
            }

            if (pd is null && p.SummonerId != 0)
            {
                ctx.PlayerData.TryGetValue(p.SummonerId.ToString(), out pd);
            }

            if (pd is null)
            {
                ctx.PlayerData.TryGetValue(stableCellId.ToString(), out pd);
            }

            if (pd is { ChampionId: > 0 })
            {
                resolvedChampId = pd.ChampionId;
            }
        }

        string? resolvedTpid = PlayerIdentity.GetValidTpid(p);
        if (string.IsNullOrEmpty(resolvedTpid) && snap is not null)
        {
            resolvedTpid = PlayerIdentity.GetValidTpid(snap);
        }

        if (string.IsNullOrEmpty(resolvedTpid))
        {
            var sessionTeam = isEnemy ? ctx.SessionEnemyTeam : ctx.SessionAllyTeam;
            var matchedSession = sessionTeam.FirstOrDefault(sp =>
                (!string.IsNullOrEmpty(p.Puuid) && !string.IsNullOrEmpty(sp.Puuid) && p.Puuid == sp.Puuid) ||
                (p.SummonerId != 0 && sp.SummonerId != 0 && p.SummonerId == sp.SummonerId) ||
                (!string.IsNullOrEmpty(p.SummonerName) && !string.IsNullOrEmpty(sp.DisplayName) && p.SummonerName == sp.DisplayName) ||
                (!string.IsNullOrEmpty(p.GameName) && !string.IsNullOrEmpty(sp.GameName) && p.GameName == sp.GameName));
            if (matchedSession is not null)
            {
                resolvedTpid = PlayerIdentity.GetValidTpid(matchedSession);
            }
        }

        return p with
        {
            CellId = stableCellId,
            IsEnemy = isEnemy,
            ChampionId = resolvedChampId,
            TeamParticipantId = resolvedTpid ?? p.TeamParticipantId,
            PartyId = resolvedTpid ?? p.PartyId,
            SummonerId = p.SummonerId != 0 ? p.SummonerId : (snap?.SummonerId ?? 0),
            Puuid = !string.IsNullOrEmpty(p.Puuid) ? p.Puuid : snap?.Puuid,
            GameName = !string.IsNullOrEmpty(p.GameName) ? p.GameName : snap?.GameName,
            TagLine = !string.IsNullOrEmpty(p.TagLine) ? p.TagLine : snap?.TagLine,
            ProfileIconId = profileIconId,
            DisplayName = resolvedName,
            Bot = p.Bot || p.IsBot,
            IsBot = p.Bot || p.IsBot,
        };
    }

    private static PremadePlayerLike? FindInSnapshot(PremadePlayerLike p, IReadOnlyList<PremadePlayerLike> snapshot)
    {
        return snapshot.FirstOrDefault(cs =>
            (!string.IsNullOrEmpty(p.Puuid) && !string.IsNullOrEmpty(cs.Puuid) && cs.Puuid == p.Puuid) ||
            (p.SummonerId != 0 && cs.SummonerId != 0 && cs.SummonerId == p.SummonerId) ||
            (!string.IsNullOrEmpty(p.SummonerName) && !string.IsNullOrEmpty(cs.DisplayName) && cs.DisplayName == p.SummonerName) ||
            (!string.IsNullOrEmpty(p.GameName) && !string.IsNullOrEmpty(cs.GameName) && cs.GameName == p.GameName) ||
            (!string.IsNullOrEmpty(p.DisplayName) && !string.IsNullOrEmpty(cs.DisplayName) && cs.DisplayName == p.DisplayName));
    }

    /// <summary>
    /// 为队伍成员预填充基础占位，避免后台异步请求完成前界面出现空白。
    /// </summary>
    public static void SeedInitialPlayerData(
        IReadOnlyList<PremadePlayerLike> players,
        Dictionary<string, GameInfoPlayer> playerData)
    {
        foreach (var p in players)
        {
            string key = p.CellId.ToString();
            bool isBot = p.Bot || p.IsBot || p.BotChampionId > 0;

            playerData.TryGetValue(p.Puuid ?? string.Empty, out var byPuuid);
            playerData.TryGetValue(p.SummonerId != 0 ? p.SummonerId.ToString() : string.Empty, out var bySid);
            playerData.TryGetValue(key, out var byCell);

            var existing = new[] { byPuuid, bySid, byCell }.FirstOrDefault(e =>
                e is not null && PlayerIdentity.IsIdentityCompatible(e, p.Puuid, p.SummonerId, p.CellId));

            if (existing is not null)
            {
                if (p.ChampionId > 0 && existing.ChampionId != p.ChampionId)
                {
                    existing = existing with { ChampionId = p.ChampionId };
                }

                playerData[key] = existing;
                if (p.SummonerId != 0)
                {
                    playerData[p.SummonerId.ToString()] = existing;
                }

                if (!string.IsNullOrEmpty(p.Puuid))
                {
                    playerData[p.Puuid] = existing;
                }

                continue;
            }

            int carriedChamp = 0;
            if (byCell is not null)
            {
                carriedChamp = PlayerIdentity.InheritPlaceholderChampion(byCell, p.CellId, p.Puuid, p.SummonerId);
                playerData.Remove(key);
            }

            if (!playerData.ContainsKey(key))
            {
                string fallbackName = !string.IsNullOrEmpty(p.DisplayName)
                    ? p.DisplayName
                    : (!string.IsNullOrEmpty(p.GameName)
                        ? p.GameName
                        : (!string.IsNullOrEmpty(p.SummonerName)
                            ? p.SummonerName
                            : (isBot ? "电脑" : $"玩家{p.CellId + 1}")));

                var placeholder = new GameInfoPlayer
                {
                    Info = new SummonerProfile
                    {
                        AccountId = 0,
                        SummonerId = p.SummonerId,
                        SummonerLevel = 0,
                        PercentCompleteForNextLevel = 0,
                        XpSinceLastLevel = 0,
                        XpUntilNextLevel = 0,
                        Puuid = p.Puuid ?? string.Empty,
                        DisplayName = fallbackName,
                        GameName = p.GameName ?? string.Empty,
                        TagLine = p.TagLine ?? string.Empty,
                        ProfileIconId = p.ProfileIconId,
                    },
                    Loading = !isBot,
                    MatchHistoryHidden = isBot,
                    ChampionId = p.ChampionId > 0 ? p.ChampionId : carriedChamp,
                };

                playerData[key] = placeholder;

                if (p.SummonerId != 0 && p.SummonerId != p.CellId && !playerData.ContainsKey(p.SummonerId.ToString()))
                {
                    playerData[p.SummonerId.ToString()] = placeholder;
                }

                if (!string.IsNullOrEmpty(p.Puuid) && !playerData.ContainsKey(p.Puuid))
                {
                    playerData[p.Puuid] = placeholder;
                }
            }
        }
    }

    /// <summary>
    /// 新组队颜色优先；新一轮缺失组队信息时沿用（迁移后）的旧颜色，避免配色闪烁。
    /// </summary>
    public static Dictionary<string, int> ResolvePremadeColors(
        IReadOnlyList<PremadePlayerLike> team,
        IReadOnlyDictionary<string, int> existingColors)
    {
        var newColors = PremadeAnalyzer.ComputePremadeColors(team);
        if (PremadeAnalyzer.HasPremadeGroup(newColors))
        {
            return newColors;
        }

        if (!PremadeAnalyzer.HasPremadeGroup(existingColors))
        {
            return newColors;
        }

        var migrated = new Dictionary<string, int>();
        foreach (var p in team)
        {
            int? cIdx = null;
            if (!string.IsNullOrEmpty(p.Puuid) && existingColors.TryGetValue(p.Puuid, out int puuidIdx))
            {
                cIdx = puuidIdx;
            }
            else if (p.SummonerId != 0 && existingColors.TryGetValue(p.SummonerId.ToString(), out int sidIdx))
            {
                cIdx = sidIdx;
            }

            if (cIdx.HasValue)
            {
                if (!string.IsNullOrEmpty(p.Puuid))
                {
                    migrated[p.Puuid] = cIdx.Value;
                }

                if (p.SummonerId != 0)
                {
                    migrated[p.SummonerId.ToString()] = cIdx.Value;
                }

                migrated[p.CellId.ToString()] = cIdx.Value;
            }
        }

        return PremadeAnalyzer.HasPremadeGroup(migrated) ? migrated : new Dictionary<string, int>(existingColors);
    }
}
