using System.Text.Json;
using YuumiX.Core.Features.Match.Models;

namespace YuumiX.Core.Parsers;

/// <summary>
/// 战绩解析：LCU 原始 JSON → 契约模型。纯函数，不读文件、不发网络、不查资源表。
///
/// 对应原项目 <c>match_parser.rs</c> 的 <c>to_display</c>（列表）与 <c>Search.vue</c> 的
/// <c>gameDetails</c> 计算属性（详情）。详情那段原本写在前端：
/// <c>participants</c> 与 <c>participantIdentities</c> 按 <c>participantId</c> 左连接，
/// 再按 <c>teamId</c> 分蓝红并求队伍击杀和——纯函数，放在 Vue 里既无法单测也无法复用，
/// 这里下沉到 Core。
///
/// <b>与原实现的两处有意差异</b>（均有夹具回归兜底）：
/// <list type="number">
///   <item><c>subteamPlacement = 0</c> 视为无排名。原实现是 <c>Option&lt;u32&gt;</c>，字段存在即为
///         <c>Some(0)</c>，界面会渲染成"第 0 名"；<c>real/remake</c> 载荷里该字段恒为 0。</item>
///   <item><c>stats</c> 整体缺失时降级为全 0 而不是整页解析失败。原 serde 结构体把 <c>stats</c>
///         当必填，一处类型变化会让整批战绩一起丢失；而界面只是少了几个数字。</item>
/// </list>
/// </summary>
public static class MatchParser
{
    /// <summary>蓝方队伍 ID。</summary>
    public const int BlueTeamId = 100;

    /// <summary>红方队伍 ID。</summary>
    public const int RedTeamId = 200;

    /// <summary>装备栏格数（含守卫格）。</summary>
    public const int ItemSlotCount = 7;

    /// <summary>
    /// 解析战绩列表页。
    /// </summary>
    /// <param name="historyResponse">
    /// <c>/lol-match-history/v1/products/lol/{puuid}/matches</c> 的完整外层响应
    /// （形如 <c>{ "games": { "games": [...] } }</c>）。
    /// </param>
    /// <returns>按载荷顺序（新到旧）的战绩；无 <c>games.games</c> 时返回空列表。</returns>
    public static IReadOnlyList<MatchSummary> ParseHistory(JsonElement historyResponse)
    {
        JsonElement? games = JsonRead.Array(JsonRead.Property(historyResponse, "games") ?? default, "games");
        if (games is null)
        {
            return [];
        }

        var result = new List<MatchSummary>(games.Value.GetArrayLength());
        foreach (JsonElement game in games.Value.EnumerateArray())
        {
            if (ParseSummary(game) is { } summary)
            {
                result.Add(summary);
            }
        }

        return result;
    }

    /// <summary>读取该玩家对局总数（<c>games.gameCount</c>）；缺失时为 <c>null</c>。</summary>
    /// <remarks>
    /// 单独暴露而不是塞进 <see cref="ParseHistory"/>：分页的 <c>HasMore</c> 判定属于服务层策略，
    /// 解析器只负责把载荷里的数字读出来。
    /// </remarks>
    public static int? ParseGameCount(JsonElement historyResponse)
    {
        JsonElement container = JsonRead.Property(historyResponse, "games") ?? default;
        return JsonRead.NullableInt(container, "gameCount");
    }

    /// <summary>
    /// 解析单局列表项。
    /// </summary>
    /// <returns>
    /// 战绩；该游戏无参与者时返回 <c>null</c>——原实现 <c>to_display</c> 同样返回 <c>None</c>，
    /// 不依赖调用方先做非空过滤（避免 <c>participants[0]</c> 越界）。
    /// </returns>
    public static MatchSummary? ParseSummary(JsonElement game)
    {
        JsonElement? participant = JsonRead.ElementAt(JsonRead.Array(game, "participants"), 0);
        if (participant is null)
        {
            return null;
        }

        JsonElement participantValue = participant.Value;
        JsonElement stats = JsonRead.Property(participantValue, "stats") ?? default;
        JsonElement timeline = JsonRead.Property(participantValue, "timeline") ?? default;

        int queueId = JsonRead.Int(game, "queueId");

        int[] itemIds = ReadItemIds(stats);
        int spell1Id = JsonRead.Int(participantValue, "spell1Id");
        int spell2Id = JsonRead.Int(participantValue, "spell2Id");
        int? roleBoundItem = JsonRead.NullableInt(stats, "roleBoundItem");

        MatchPosition? position = MatchPositionResolver.Resolve(
            queueId,
            roleBoundItem,
            itemIds,
            spell1Id,
            spell2Id,
            JsonRead.String(timeline, "lane"),
            JsonRead.String(timeline, "role"));

        return BuildSummary(
            JsonRead.Long(game, "gameId"),
            queueId,
            JsonRead.Long(game, "gameCreation"),
            JsonRead.Int(game, "gameDuration"),
            participantValue,
            stats,
            itemIds,
            JsonRead.Int(stats, "perk0"),
            roleBoundItem,
            position);
    }

    /// <summary>
    /// 列表项的字段装配（LCU 与 SGP 共用）。
    ///
    /// 两个数据源在"怎么定位参与者、从哪读符文与分路"上不同，但落到模型上的字段完全一致，
    /// 因此赋值只写一份：分开写会让两条来源各自演进，最终表现为
    /// "同一个人同一局，LCU 与 SGP 算出的数字不一致"，而这种错位极难复现。
    /// </summary>
    private static MatchSummary BuildSummary(
        long gameId,
        int queueId,
        long gameCreation,
        int durationSeconds,
        JsonElement participant,
        JsonElement stats,
        int[] itemIds,
        int runeId,
        int? roleBoundItem,
        MatchPosition? position)
    {
        MatchQueueInfo queue = MatchQueueCatalog.Resolve(queueId);

        return new MatchSummary
        {
            GameId = gameId,
            QueueId = queueId,
            QueueName = queue.Name,
            MapName = queue.MapName,
            GameCreation = gameCreation,
            DurationSeconds = durationSeconds,
            Win = JsonRead.Bool(stats, "win"),
            Remake = JsonRead.Bool(stats, "gameEndedInEarlySurrender"),
            Placement = ReadPlacement(stats),
            ChampionId = JsonRead.Int(participant, "championId"),
            Spell1Id = JsonRead.Int(participant, "spell1Id"),
            Spell2Id = JsonRead.Int(participant, "spell2Id"),
            ChampionLevel = JsonRead.Int(stats, "champLevel"),
            Kills = JsonRead.Int(stats, "kills"),
            Deaths = JsonRead.Int(stats, "deaths"),
            Assists = JsonRead.Int(stats, "assists"),
            ItemIds = itemIds,
            RuneId = runeId,
            CreepScore = JsonRead.Int(stats, "totalMinionsKilled") + JsonRead.Int(stats, "neutralMinionsKilled"),
            Gold = JsonRead.Int(stats, "goldEarned"),
            DamageToChampions = JsonRead.Int(stats, "totalDamageDealtToChampions"),
            TotalHeal = JsonRead.Int(stats, "totalHeal"),
            AugmentIds = MatchAugments.Extract(stats),
            Position = position,
            RoleBoundItem = roleBoundItem,
        };
    }

    /// <summary>
    /// 解析 SGP 远端战绩（<c>/match-history-query/v1/products/lol/player/{puuid}/SUMMARY</c>）。
    /// </summary>
    /// <param name="sgpResponse">SGP 响应，可能形如 <c>{ "games": [...] }</c> 或 <c>{ "games": { "games": [...] } }</c>。</param>
    /// <param name="puuid">被查询玩家 puuid——SGP 载荷里没有"本人"的概念，必须按 puuid 定位参与者。</param>
    /// <remarks>
    /// 与 LCU 列表解析的四处差异（都是载荷形状决定的，不是新规则）：
    /// <list type="number">
    ///   <item><b>参与者不是第 0 个</b>：SGP 返回 10 人，必须按 <c>puuid</c>（忽略大小写）匹配；
    ///         匹配不到的对局整条跳过，而不是退回 <c>participants[0]</c>——那会把对手的战绩当成本人的。</item>
    ///   <item><b><c>stats</c> 可能不存在</b>：此时统计字段直接挂在参与者对象上。</item>
    ///   <item><b><c>perk0</c> 有第二来源</b>：缺失时读
    ///         <c>perks.styles[0].selections[0].perk</c>。</item>
    ///   <item><b>分路多两级依据</b>：<c>teamPosition</c> 与 <c>selectedPosition</c>，见
    ///         <see cref="MatchPositionResolver.Resolve"/>。</item>
    /// </list>
    /// 另：每局可能被包在 <c>{"json": {...}}</c> 里，这里自动拆包。
    /// </remarks>
    public static IReadOnlyList<MatchSummary> ParseSgpHistory(JsonElement sgpResponse, string puuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(puuid);

        JsonElement? games = ReadSgpGames(sgpResponse);
        if (games is null)
        {
            return [];
        }

        var result = new List<MatchSummary>(games.Value.GetArrayLength());
        foreach (JsonElement entry in games.Value.EnumerateArray())
        {
            // SGP 的 SUMMARY 每项常被包一层 {"json": {...}}，两种形状都要吃。
            JsonElement game = JsonRead.Property(entry, "json") ?? entry;
            if (ParseSgpSummary(game, puuid) is { } summary)
            {
                result.Add(summary);
            }
        }

        return result;
    }

    private static JsonElement? ReadSgpGames(JsonElement response)
    {
        JsonElement? games = JsonRead.Property(response, "games");
        if (games is null)
        {
            return null;
        }

        return games.Value.ValueKind == JsonValueKind.Array
            ? games
            : JsonRead.Array(games.Value, "games");
    }

    private static MatchSummary? ParseSgpSummary(JsonElement game, string puuid)
    {
        JsonElement? participants = JsonRead.Array(game, "participants");
        if (participants is null)
        {
            return null;
        }

        JsonElement? participant = null;
        foreach (JsonElement candidate in participants.Value.EnumerateArray())
        {
            string? candidatePuuid = JsonRead.String(candidate, "puuid");
            if (!string.IsNullOrEmpty(candidatePuuid)
                && string.Equals(candidatePuuid, puuid, StringComparison.OrdinalIgnoreCase))
            {
                participant = candidate;
                break;
            }
        }

        if (participant is null)
        {
            return null;
        }

        JsonElement participantValue = participant.Value;
        JsonElement stats = JsonRead.Property(participantValue, "stats") ?? participantValue;
        JsonElement timeline = JsonRead.Property(participantValue, "timeline") ?? default;

        int queueId = JsonRead.Int(game, "queueId");
        int[] itemIds = ReadItemIds(stats);
        int spell1Id = JsonRead.Int(participantValue, "spell1Id");
        int spell2Id = JsonRead.Int(participantValue, "spell2Id");

        // roleBoundItem 在 SGP 里可能挂在 stats，也可能直接挂在参与者上
        int? roleBoundItem = JsonRead.NullableInt(stats, "roleBoundItem")
            ?? JsonRead.NullableInt(participantValue, "roleBoundItem");

        MatchPosition? position = MatchPositionResolver.Resolve(
            queueId,
            roleBoundItem,
            itemIds,
            spell1Id,
            spell2Id,
            JsonRead.String(timeline, "lane"),
            JsonRead.String(timeline, "role"),
            JsonRead.String(participantValue, "teamPosition"),
            JsonRead.String(participantValue, "selectedPosition"));

        // 基石符文：stats.perk0 缺失时读 perks.styles[0].selections[0].perk
        int runeId = JsonRead.NullableInt(stats, "perk0") ?? ReadSgpPerk(participantValue);

        return BuildSummary(
            JsonRead.Long(game, "gameId"),
            queueId,
            JsonRead.Long(game, "gameCreation"),
            JsonRead.Int(game, "gameDuration"),
            participantValue,
            stats,
            itemIds,
            runeId,
            roleBoundItem,
            position);
    }

    private static int ReadSgpPerk(JsonElement participant) =>
        JsonRead.ElementAt(
            JsonRead.Property(
                JsonRead.ElementAt(JsonRead.Array(JsonRead.Property(participant, "perks") ?? default, "styles"), 0)
                    ?? default,
                "selections"),
            0) is { } selection
            ? JsonRead.Int(selection, "perk")
            : 0;

    /// <summary>
    /// 解析单局详情（10 人 + 两队），对局不存在或无参与者时返回 <c>null</c>。
    /// </summary>
    /// <param name="game">详情载荷，或列表里任意一局的完整 game 对象（两者结构兼容）。</param>
    /// <param name="queriedPuuid">被查询玩家 puuid，用于填充 <see cref="MatchDetail.QueriedPlayerWin"/>。</param>
    public static MatchDetail? ParseDetail(JsonElement game, string? queriedPuuid = null)
    {
        JsonElement? participants = JsonRead.Array(game, "participants");
        if (participants is null || participants.Value.GetArrayLength() == 0)
        {
            return null;
        }

        Dictionary<int, JsonElement> identities = ReadIdentities(game);

        var bluePlayers = new List<MatchDetailPlayer>();
        var redPlayers = new List<MatchDetailPlayer>();
        bool? queriedPlayerWin = null;

        foreach (JsonElement participant in participants.Value.EnumerateArray())
        {
            MatchDetailPlayer player = ParseDetailPlayer(participant, identities);
            if (player.TeamId == BlueTeamId)
            {
                bluePlayers.Add(player);
            }
            else
            {
                redPlayers.Add(player);
            }

            if (queriedPlayerWin is null
                && !string.IsNullOrEmpty(queriedPuuid)
                && string.Equals(player.Puuid, queriedPuuid, StringComparison.Ordinal))
            {
                queriedPlayerWin = player.Win;
            }
        }

        int queueId = JsonRead.Int(game, "queueId");
        int mapId = JsonRead.Int(game, "mapId");
        MatchQueueInfo queue = MatchQueueCatalog.Resolve(queueId);

        return new MatchDetail
        {
            GameId = JsonRead.Long(game, "gameId"),
            QueueId = queueId,
            MapId = mapId,
            QueueName = queue.Name,
            MapName = ResolveDetailMapName(mapId, queue),
            GameCreation = JsonRead.Long(game, "gameCreation"),
            DurationSeconds = JsonRead.Int(game, "gameDuration"),
            Blue = BuildTeam(game, BlueTeamId, bluePlayers),
            Red = BuildTeam(game, RedTeamId, redPlayers),
            QueriedPlayerWin = queriedPlayerWin,
        };
    }

    /// <summary>
    /// 详情页地图名：优先按 <c>mapId</c>（详情载荷才稳定带），查不到再回落到队列的惯常地图。
    /// </summary>
    private static string ResolveDetailMapName(int mapId, MatchQueueInfo queue)
    {
        string byMapId = MatchQueueCatalog.ResolveMapName(mapId);
        return byMapId == MatchQueueCatalog.UnknownMapName ? queue.MapName : byMapId;
    }

    /// <summary>
    /// 名次：<c>subteamPlacement</c> 只在斗魂竞技场这类有多名次的模式才有意义，
    /// 缺失或为 0 时一律 <c>null</c>（0 是载荷里的占位值，不是"第 0 名"）。
    /// </summary>
    private static int? ReadPlacement(JsonElement stats) =>
        JsonRead.NullableInt(stats, "subteamPlacement") is { } placement && placement > 0 ? placement : null;

    private static int[] ReadItemIds(JsonElement stats)
    {
        var itemIds = new int[ItemSlotCount];
        for (int slot = 0; slot < ItemSlotCount; slot++)
        {
            itemIds[slot] = JsonRead.Int(stats, $"item{slot}");
        }

        return itemIds;
    }

    /// <summary><c>participantId</c> → <c>player</c> 身份段。</summary>
    private static Dictionary<int, JsonElement> ReadIdentities(JsonElement game)
    {
        var identities = new Dictionary<int, JsonElement>();
        if (JsonRead.Array(game, "participantIdentities") is not { } array)
        {
            return identities;
        }

        foreach (JsonElement identity in array.EnumerateArray())
        {
            int participantId = JsonRead.Int(identity, "participantId");
            if (participantId > 0 && JsonRead.Property(identity, "player") is { } player)
            {
                identities[participantId] = player;
            }
        }

        return identities;
    }

    private static MatchDetailPlayer ParseDetailPlayer(
        JsonElement participant,
        IReadOnlyDictionary<int, JsonElement> identities)
    {
        JsonElement stats = JsonRead.Property(participant, "stats") ?? default;
        int participantId = JsonRead.Int(participant, "participantId");

        string name = "未知";
        string puuid = string.Empty;
        long summonerId = 0;

        if (identities.TryGetValue(participantId, out JsonElement player))
        {
            string? gameName = JsonRead.String(player, "gameName");
            string? summonerName = JsonRead.String(player, "summonerName");
            string baseName = !string.IsNullOrEmpty(gameName)
                ? gameName
                : !string.IsNullOrEmpty(summonerName) ? summonerName : name;

            string? tagLine = JsonRead.String(player, "tagLine");
            name = string.IsNullOrEmpty(tagLine) ? baseName : $"{baseName}#{tagLine}";
            puuid = JsonRead.String(player, "puuid") ?? string.Empty;
            summonerId = JsonRead.Long(player, "summonerId");
        }

        return new MatchDetailPlayer
        {
            ParticipantId = participantId,
            TeamId = JsonRead.Int(participant, "teamId"),
            ChampionId = JsonRead.Int(participant, "championId"),
            Spell1Id = JsonRead.Int(participant, "spell1Id"),
            Spell2Id = JsonRead.Int(participant, "spell2Id"),
            Name = name,
            Puuid = puuid,
            SummonerId = summonerId,
            ChampionLevel = JsonRead.Int(stats, "champLevel"),
            Kills = JsonRead.Int(stats, "kills"),
            Deaths = JsonRead.Int(stats, "deaths"),
            Assists = JsonRead.Int(stats, "assists"),
            CreepScore = JsonRead.Int(stats, "totalMinionsKilled") + JsonRead.Int(stats, "neutralMinionsKilled"),
            Gold = JsonRead.Int(stats, "goldEarned"),
            DamageToChampions = JsonRead.Int(stats, "totalDamageDealtToChampions"),
            ItemIds = ReadItemIds(stats),
            RuneId = JsonRead.Int(stats, "perk0"),
            Win = JsonRead.Bool(stats, "win"),
            AugmentIds = MatchAugments.Extract(stats, participant),
        };
    }

    /// <summary>
    /// 组装一支队伍。胜利判定优先取队员的 <c>stats.win</c>（列表与详情两处口径一致），
    /// 该队无队员时回落到 <c>teams[].win</c> 的字符串（LCU 里是 "Win" / "Fail"）。
    /// </summary>
    private static MatchDetailTeam BuildTeam(
        JsonElement game,
        int teamId,
        IReadOnlyList<MatchDetailPlayer> players)
    {
        JsonElement? raw = FindTeam(game, teamId);

        bool win = players.Count > 0
            ? players[0].Win
            : string.Equals(JsonRead.String(raw ?? default, "win"), "Win", StringComparison.OrdinalIgnoreCase);

        return new MatchDetailTeam
        {
            TeamId = teamId,
            Players = players,
            Kills = players.Sum(player => player.Kills),
            Win = win,
            TowerKills = JsonRead.Int(raw ?? default, "towerKills"),
            InhibitorKills = JsonRead.Int(raw ?? default, "inhibitorKills"),
            BaronKills = JsonRead.Int(raw ?? default, "baronKills"),
            DragonKills = JsonRead.Int(raw ?? default, "dragonKills"),
            RiftHeraldKills = JsonRead.Int(raw ?? default, "riftHeraldKills"),
        };
    }

    private static JsonElement? FindTeam(JsonElement game, int teamId)
    {
        if (JsonRead.Array(game, "teams") is not { } teams)
        {
            return null;
        }

        foreach (JsonElement team in teams.EnumerateArray())
        {
            if (JsonRead.Int(team, "teamId") == teamId)
            {
                return team;
            }
        }

        return null;
    }
}
