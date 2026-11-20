using System.Text.Json;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// ChampSelect / Gameflow 会话 JSON 解析、选人快照增量合并与阵营探测辅助。
/// </summary>
internal static class GameInfoSessionParser
{
    /// <summary>gameflow session 拉取结果（重试分支复用）。</summary>
    internal readonly record struct GameflowTeamsFetch(
        List<PremadePlayerLike> TeamOne,
        List<PremadePlayerLike> TeamTwo,
        long? GameId,
        bool IsCustomGame);

    /// <summary>选人会话装配结果。</summary>
    internal readonly record struct ChampSelectTeams(
        List<PremadePlayerLike> MyTeam,
        List<PremadePlayerLike> TheirTeam);

    /// <summary>队列与云顶模式信息。</summary>
    internal readonly record struct QueueInfo(int QueueId, bool IsTft);

    /// <summary>从 gameflow session JSON 根节点解析队列与云顶模式信息。</summary>
    internal static bool TryParseQueueInfo(JsonElement root, out QueueInfo info)
    {
        if (root.TryGetProperty("gameData", out var gd) &&
            gd.TryGetProperty("queue", out var queue))
        {
            int qId = JsonRead.Int(queue, "id", 0);
            string? gameMode = JsonRead.String(queue, "gameMode");
            bool isTft = string.Equals(gameMode, "TFT", StringComparison.OrdinalIgnoreCase) ||
                         (qId >= 1090 && qId <= 1200);
            info = new QueueInfo(qId, isTft);
            return true;
        }

        info = default;
        return false;
    }

    /// <summary>从 gameflow gameData 节点解析双方队伍、gameId 与自定义标记。</summary>
    internal static GameflowTeamsFetch ParseGameflowGameData(JsonElement gameData)
    {
        long? gameId = null;
        if (gameData.TryGetProperty("gameId", out var gameIdEl) && gameIdEl.ValueKind == JsonValueKind.Number)
        {
            long gid = gameIdEl.GetInt64();
            if (gid > 0)
            {
                gameId = gid;
            }
        }

        bool isCustom = false;
        if (gameData.TryGetProperty("queue", out var queueEl) && queueEl.ValueKind == JsonValueKind.Object)
        {
            isCustom = JsonRead.Bool(queueEl, "isCustom", false);
        }

        return new GameflowTeamsFetch(
            ParseParticipants(gameData, "teamOne"),
            ParseParticipants(gameData, "teamTwo"),
            gameId,
            isCustom);
    }

    /// <summary>重新拉取 gameflow session 的双方队伍（用于队伍缺失时重试）。</summary>
    internal static async Task<GameflowTeamsFetch> FetchGameflowTeamsAsync(
        ILcuTransport transport,
        CancellationToken cancellationToken)
    {
        try
        {
            LcuResponse resp = await transport.SendAsync(
                LcuRequest.Get("/lol-gameflow/v1/session"),
                cancellationToken).ConfigureAwait(false);

            if (!resp.IsSuccess || string.IsNullOrWhiteSpace(resp.Body))
            {
                return new GameflowTeamsFetch([], [], null, false);
            }

            using var doc = JsonDocument.Parse(resp.Body);
            if (!doc.RootElement.TryGetProperty("gameData", out var gameData))
            {
                return new GameflowTeamsFetch([], [], null, false);
            }

            return ParseGameflowGameData(gameData);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new GameflowTeamsFetch([], [], null, false);
        }
    }

    /// <summary>解析选人阶段 session JSON 并构建双方初始队伍。</summary>
    internal static ChampSelectTeams BuildChampSelectTeams(JsonElement root)
    {
        var session = ParseChampSelectSession(root);

        // 队伍归属以 LCU 原生语义为准：myTeam（含自定义人机 isHumanoid）= 我方，theirTeam = 敌方。
        // isHumanoid 成员补标 bot（下游走本地占位，不请求战绩接口），不改变归属
        static bool IsBotMember(ChampSelectMemberLike m) => m.IsHumanoid;

        var myMembers = session.MyTeam ?? [];
        var theirMembers = (session.TheirTeam ?? [])
            .Where(m => m.IsHumanoid || !string.IsNullOrEmpty(m.Puuid) || m.SummonerId != 0 || m.ChampionId > 0)
            .ToList();

        // 自定义会话（敌方 cell 可能与我方共用编号空间）：敌方需重映射到 5+ 稳定区间隔离
        bool isCustomSession = session.IsCustomGame ||
            myMembers.Any(m => m.IsHumanoid) ||
            (session.TheirTeam ?? []).Any(m => m.IsHumanoid);

        var myTeam = myMembers.Select(m => new PremadePlayerLike
        {
            CellId = m.CellId,
            ChampionId = ChampionResolver.ResolvePlayerChampionId(
                new PremadePlayerLike { CellId = m.CellId, Puuid = m.Puuid, SummonerId = m.SummonerId },
                session),
            ChampionPickIntent = m.ChampionPickIntent,
            Puuid = m.Puuid,
            SummonerId = m.SummonerId,
            IsEnemy = false,
            Bot = IsBotMember(m),
            IsBot = IsBotMember(m),
            IsHumanoid = m.IsHumanoid,
        }).ToList();

        var theirTeam = theirMembers.Select((m, idx) => new PremadePlayerLike
        {
            CellId = isCustomSession ? 5 + idx : m.CellId,
            ChampionId = ChampionResolver.ResolvePlayerChampionId(
                new PremadePlayerLike { CellId = m.CellId, Puuid = m.Puuid, SummonerId = m.SummonerId, IsEnemy = true },
                session),
            ChampionPickIntent = m.ChampionPickIntent,
            Puuid = m.Puuid,
            SummonerId = m.SummonerId,
            IsEnemy = true,
            Bot = IsBotMember(m),
            IsBot = IsBotMember(m),
            IsHumanoid = m.IsHumanoid,
        }).ToList();

        return new ChampSelectTeams(myTeam, theirTeam);
    }

    /// <summary>
    /// 选人阶段快照增量合并（对应旧版 <c>mergeSnapshot</c>）：
    /// 按 puuid / summonerId / cellId 对齐上一帧，英雄 ID 与组队 ID 只增不减。
    /// </summary>
    internal static List<PremadePlayerLike> MergeChampSelectSnapshot(
        List<PremadePlayerLike> incoming,
        List<PremadePlayerLike> previous)
    {
        if (previous.Count == 0)
        {
            return incoming;
        }

        var merged = new List<PremadePlayerLike>(incoming.Count);
        foreach (var p in incoming)
        {
            var prev = previous.FirstOrDefault(old =>
                (!string.IsNullOrEmpty(p.Puuid) && !string.IsNullOrEmpty(old.Puuid) && p.Puuid == old.Puuid) ||
                (p.SummonerId != 0 && old.SummonerId != 0 && p.SummonerId == old.SummonerId) ||
                old.CellId == p.CellId);

            int champId = p.ChampionId > 0
                ? p.ChampionId
                : (prev is { ChampionId: > 0 } ? prev.ChampionId : 0);

            string? tpid = PlayerIdentity.GetValidTpid(p) ?? PlayerIdentity.GetValidTpid(prev);

            merged.Add(p with
            {
                ChampionId = champId,
                TeamParticipantId = tpid ?? p.TeamParticipantId,
                PartyId = tpid ?? p.PartyId,
                SummonerId = p.SummonerId != 0 ? p.SummonerId : (prev?.SummonerId ?? 0),
                Puuid = !string.IsNullOrEmpty(p.Puuid) ? p.Puuid : prev?.Puuid,
            });
        }

        return merged;
    }

    /// <summary>
    /// 根据本机召唤师与选人快照推断 gameflow session 中的友方/敌方队伍。
    /// </summary>
    internal static (List<PremadePlayerLike> Ally, List<PremadePlayerLike> Enemy) SplitSessionTeams(
        IReadOnlyList<PremadePlayerLike> teamOne,
        IReadOnlyList<PremadePlayerLike> teamTwo,
        long summonerId,
        string? puuid,
        IReadOnlyList<PremadePlayerLike> champSelectTeamSnapshot)
    {
        bool MatchesLocal(PremadePlayerLike p) =>
            (summonerId != 0 && p.SummonerId == summonerId) ||
            (!string.IsNullOrEmpty(puuid) && !string.IsNullOrEmpty(p.Puuid) &&
             string.Equals(p.Puuid, puuid, StringComparison.OrdinalIgnoreCase)) ||
            champSelectTeamSnapshot.Any(cs =>
                (!string.IsNullOrEmpty(p.Puuid) && !string.IsNullOrEmpty(cs.Puuid) &&
                 string.Equals(p.Puuid, cs.Puuid, StringComparison.OrdinalIgnoreCase)) ||
                (p.SummonerId != 0 && cs.SummonerId != 0 && p.SummonerId == cs.SummonerId) ||
                (!string.IsNullOrEmpty(p.GameName) && !string.IsNullOrEmpty(cs.GameName) &&
                 string.Equals(p.GameName, cs.GameName, StringComparison.OrdinalIgnoreCase)));

        bool isTeamOne = teamOne.Any(MatchesLocal);
        var ally = isTeamOne || teamTwo.Count == 0 ? teamOne : teamTwo;
        var enemy = isTeamOne || teamTwo.Count == 0 ? teamTwo : teamOne;
        return (ally.ToList(), enemy.ToList());
    }

    /// <summary>
    /// 将 session 友方队员中的组队 ID 回填进选人快照（供预组队着色与继承）。
    /// </summary>
    internal static void BackfillTeamParticipantIds(
        List<PremadePlayerLike> champSelectTeamSnapshot,
        IReadOnlyList<PremadePlayerLike> ally)
    {
        for (int i = 0; i < champSelectTeamSnapshot.Count; i++)
        {
            var cs = champSelectTeamSnapshot[i];
            if (!string.IsNullOrEmpty(PlayerIdentity.GetValidTpid(cs)))
            {
                continue;
            }

            var matched = ally.FirstOrDefault(ap =>
                (!string.IsNullOrEmpty(cs.Puuid) && !string.IsNullOrEmpty(ap.Puuid) && cs.Puuid == ap.Puuid) ||
                (cs.SummonerId != 0 && ap.SummonerId != 0 && cs.SummonerId == ap.SummonerId) ||
                (!string.IsNullOrEmpty(cs.GameName) && !string.IsNullOrEmpty(ap.GameName) && cs.GameName == ap.GameName));

            string? tpid = PlayerIdentity.GetValidTpid(matched);
            if (tpid is null)
            {
                continue;
            }

            champSelectTeamSnapshot[i] = cs with { TeamParticipantId = tpid, PartyId = tpid };
        }
    }

    /// <summary>
    /// 脱敏对局阵营探测（对应旧版 Rust <c>get_map_side</c>）：
    /// ① pin-drop-notification.mapSide；② 选人 session myTeam 的 cellId 区间；
    /// ③ gameflow session 中本机玩家所在队伍；④ LiveClientData 本机操作玩家的 team。
    /// </summary>
    internal static async Task<string?> ResolveMapSideAsync(
        ILcuTransport transport,
        ILiveClientService? liveClientService,
        IReadOnlyList<PremadePlayerLike> teamOne,
        IReadOnlyList<PremadePlayerLike> teamTwo,
        long currentSummonerId,
        string? currentSummonerPuuid,
        CancellationToken cancellationToken)
    {
        // ① pin-drop-notification（选人会话初始化可能延迟，短重试）
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                LcuResponse pinResp = await transport.SendAsync(
                    LcuRequest.Get("/lol-champ-select/v1/pin-drop-notification"),
                    cancellationToken).ConfigureAwait(false);

                if (!pinResp.IsSuccess || string.IsNullOrWhiteSpace(pinResp.Body))
                {
                    continue;
                }

                using var pinDoc = JsonDocument.Parse(pinResp.Body);
                if (pinDoc.RootElement.TryGetProperty("mapSide", out var mapSideEl) &&
                    mapSideEl.ValueKind == JsonValueKind.String)
                {
                    string? value = mapSideEl.GetString()?.ToLowerInvariant();
                    if (value is "blue" or "red")
                    {
                        return value;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 落入下一种探测方式
            }
        }

        // ② 选人 session：myTeam 最小/最大 cellId 判定（蓝方 0..4，红方 5..9）
        try
        {
            LcuResponse sessionResp = await transport.SendAsync(
                LcuRequest.Get("/lol-champ-select/v1/session"),
                cancellationToken).ConfigureAwait(false);

            if (sessionResp.IsSuccess && !string.IsNullOrWhiteSpace(sessionResp.Body))
            {
                using var sessionDoc = JsonDocument.Parse(sessionResp.Body);
                if (sessionDoc.RootElement.TryGetProperty("myTeam", out var myTeamEl) &&
                    myTeamEl.ValueKind == JsonValueKind.Array)
                {
                    int minCell = int.MaxValue;
                    int maxCell = -1;
                    foreach (var m in myTeamEl.EnumerateArray())
                    {
                        int cellId = JsonRead.Int(m, "cellId", -1);
                        if (cellId < 0)
                        {
                            continue;
                        }

                        minCell = Math.Min(minCell, cellId);
                        maxCell = Math.Max(maxCell, cellId);
                    }

                    if (maxCell >= 0)
                    {
                        if (minCell >= 5)
                        {
                            return "red";
                        }

                        if (maxCell < 5)
                        {
                            return "blue";
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 落入下一种探测方式
        }

        // ③ gameflow session：本机玩家落在 teamOne（蓝）还是 teamTwo（红）
        long summonerId = currentSummonerId;
        string? puuid = currentSummonerPuuid;

        if (summonerId == 0 && string.IsNullOrEmpty(puuid))
        {
            try
            {
                LcuResponse meResp = await transport.SendAsync(
                    LcuRequest.Get("/lol-summoner/v1/current-summoner"),
                    cancellationToken).ConfigureAwait(false);

                if (meResp.IsSuccess && !string.IsNullOrWhiteSpace(meResp.Body))
                {
                    using var meDoc = JsonDocument.Parse(meResp.Body);
                    summonerId = JsonRead.Long(meDoc.RootElement, "summonerId", 0);
                    puuid = JsonRead.String(meDoc.RootElement, "puuid");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 忽略，继续下一种探测
            }
        }

        bool InTeam(IReadOnlyList<PremadePlayerLike> team) => team.Any(p =>
            (!string.IsNullOrEmpty(puuid) && !string.IsNullOrEmpty(p.Puuid) &&
             string.Equals(p.Puuid, puuid, StringComparison.OrdinalIgnoreCase)) ||
            (summonerId != 0 && p.SummonerId == summonerId));

        if (InTeam(teamOne))
        {
            return "blue";
        }

        if (InTeam(teamTwo))
        {
            return "red";
        }

        // ④ LiveClientData：本机操作玩家所属 team（ORDER=蓝 / CHAOS=红）
        if (liveClientService is null)
        {
            return null;
        }

        try
        {
            string? activeName = await liveClientService.GetActivePlayerNameAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(activeName))
            {
                return null;
            }

            var players = await liveClientService.GetPlayerListAsync(cancellationToken).ConfigureAwait(false);
            var matched = players
                .Where(p => string.Equals(p.RiotIdGameName, activeName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.SummonerName, activeName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.RiotId, activeName, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Team)
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 命中多个不同阵营（如盲选同名英雄）时不可采信
            if (matched.Count == 1)
            {
                return string.Equals(matched[0], "ORDER", StringComparison.OrdinalIgnoreCase) ? "blue" : "red";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 探测失败
        }

        return null;
    }

    internal static ChampSelectSessionLike ParseChampSelectSession(JsonElement root)
    {
        var actionsList = new List<List<ChampSelectActionLike>>();
        if (root.TryGetProperty("actions", out var actionsProp) && actionsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in actionsProp.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Array) continue;
                var groupList = new List<ChampSelectActionLike>();
                foreach (var act in group.EnumerateArray())
                {
                    int actorCellId = JsonRead.Int(act, "actorCellId", -1);
                    int champId = JsonRead.Int(act, "championId", 0);
                    bool completed = JsonRead.Bool(act, "completed", false);
                    bool inProgress = JsonRead.Bool(act, "isInProgress", false);
                    string type = JsonRead.String(act, "type") ?? string.Empty;
                    groupList.Add(new ChampSelectActionLike(actorCellId, champId, completed, inProgress, type));
                }
                actionsList.Add(groupList);
            }
        }

        var myTeamList = ParseChampSelectMembers(root, "myTeam");
        var theirTeamList = ParseChampSelectMembers(root, "theirTeam");
        bool isCustomGame = JsonRead.Bool(root, "isCustomGame", false);

        return new ChampSelectSessionLike(actionsList, myTeamList, theirTeamList, isCustomGame);
    }

    internal static List<ChampSelectMemberLike> ParseChampSelectMembers(JsonElement root, string propName)
    {
        var list = new List<ChampSelectMemberLike>();
        if (root.TryGetProperty(propName, out var teamProp) && teamProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in teamProp.EnumerateArray())
            {
                int cellId = JsonRead.Int(m, "cellId", -1);
                int champId = JsonRead.Int(m, "championId", 0);
                int pickIntent = JsonRead.Int(m, "championPickIntent", 0);
                string? puuid = JsonRead.String(m, "puuid");
                long sid = JsonRead.Long(m, "summonerId", 0);
                bool isHumanoid = JsonRead.Bool(m, "isHumanoid", false);
                list.Add(new ChampSelectMemberLike(cellId, champId, pickIntent, puuid, sid, isHumanoid));
            }
        }
        return list;
    }

    internal static List<PremadePlayerLike> ParseParticipants(JsonElement gameData, string teamName)
    {
        var list = new List<PremadePlayerLike>();
        if (gameData.TryGetProperty(teamName, out var team) && team.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in team.EnumerateArray())
            {
                long sid = JsonRead.Long(p, "summonerId", 0);
                string? puuid = JsonRead.String(p, "puuid");
                string? displayName = JsonRead.String(p, "displayName");
                string? summonerName = JsonRead.String(p, "summonerName");
                string? gameName = JsonRead.String(p, "gameName");
                string? tagLine = JsonRead.String(p, "tagLine");
                int profileIconId = JsonRead.Int(p, "profileIconId", 0);
                int championId = JsonRead.Int(p, "championId", 0);
                int cellId = JsonRead.Int(p, "cellId", 0);
                string? tpid = JsonRead.String(p, "teamParticipantId") ?? JsonRead.Long(p, "teamParticipantId", 0).ToString();
                string? partyId = JsonRead.String(p, "partyId") ?? JsonRead.Long(p, "partyId", 0).ToString();
                bool isHumanoid = JsonRead.Bool(p, "isHumanoid", false);
                bool bot = JsonRead.Bool(p, "bot", false) || JsonRead.Bool(p, "isBot", false) || isHumanoid;
                int botChampId = JsonRead.Int(p, "botChampionId", 0);
                string? botName = JsonRead.String(p, "botName");

                list.Add(new PremadePlayerLike
                {
                    SummonerId = sid,
                    Puuid = puuid,
                    DisplayName = displayName,
                    SummonerName = summonerName,
                    GameName = gameName,
                    TagLine = tagLine,
                    ProfileIconId = profileIconId,
                    ChampionId = championId,
                    CellId = cellId,
                    TeamParticipantId = tpid,
                    PartyId = partyId,
                    Bot = bot,
                    IsBot = bot,
                    IsHumanoid = isHumanoid,
                    BotChampionId = botChampId,
                    BotName = botName,
                });
            }
        }
        return list;
    }
}
