using System.Text.Json;
using YuumiX.Core.Features.SavedPlayers.Models;

namespace YuumiX.Core.Parsers;

/// <summary>某局里一位队友的在场记录。</summary>
/// <param name="Name">显示名（<c>gameName#tagLine</c> 形态）。</param>
/// <param name="Puuid">玩家 puuid。</param>
/// <param name="ProfileIconId">头像 ID。</param>
/// <param name="Win">该玩家所在队伍的胜负。</param>
public sealed record TeammatePresence(string Name, string Puuid, int ProfileIconId, bool Win);

/// <summary>单局中与目标玩家同队的人。</summary>
/// <param name="Remake">该局是否重开（<c>teamEarlySurrendered</c>），重开局不计胜负。</param>
/// <param name="GameCreation">开局时间，Unix 毫秒。</param>
/// <param name="Teammates">同队玩家，不含目标玩家本人。</param>
public sealed record GameTeammates(bool Remake, long GameCreation, IReadOnlyList<TeammatePresence> Teammates);

/// <summary>
/// 最近队友：把 N 局详情里的同队玩家聚合成"跟谁玩得最多"。
///
/// 对应原项目 <c>match_parser.rs</c> 的 <c>fetch_game_teammates</c>（解析）与
/// <c>get_recent_teammates</c>（聚合）。原实现把两者塞在一个 <c>tauri::command</c> 里，
/// 中间夹着 HTTP fan-out 与信号量限流；这里只保留纯逻辑，取数与并发留给服务层——
/// 解析规则否则无法单测，只能靠起客户端手点。
///
/// <b>与原实现的四处差异</b>：
/// <list type="number">
///   <item>头像从 URL 改为 <see cref="TeammatePresence.ProfileIconId"/>（ADR-11）；</item>
///   <item><c>LastPlayTime</c> 取各局的**最大值**。原实现取"首次插入时的 <c>gameCreation</c>"，
///         实际结果取决于入参 <c>game_ids</c> 的顺序（不是按时间排序就是错的）；</item>
///   <item>标签由调用方查库后传入，本类型不碰持久化；</item>
///   <item>同队次数相同时按名字升序，保证输出稳定（原实现依赖 <c>HashMap</c> 迭代顺序，每次可能不同）。</item>
/// </list>
/// </summary>
public static class RecentTeammateParser
{
    /// <summary>返回的最大人数（原实现 <c>truncate(5)</c>）。</summary>
    public const int MaxCount = 5;

    /// <summary>匿名玩家的 puuid 占位值，这类记录直接丢弃。</summary>
    private const string EmptyPuuid = "00000000-0000-0000-0000-000000000000";

    /// <summary>斗魂竞技场的队列 ID——该模式没有 <c>teamId</c>，同队关系按 <c>subteamPlacement</c> 判定。</summary>
    private const int ArenaQueueId = 1700;

    /// <summary>
    /// 从单局详情里取出与目标玩家同队的人。
    /// </summary>
    /// <param name="game">详情载荷（<c>/lol-match-history/v1/games/{gameId}</c>）。</param>
    /// <param name="targetPuuid">目标玩家 puuid。</param>
    /// <returns>
    /// 同队记录；缺 <c>participants</c> / <c>participantIdentities</c>、目标玩家不在本局、
    /// 或目标参与者缺 <c>stats</c> 时返回 <c>null</c>（该局直接跳过，不产生"空队友"记录）。
    /// </returns>
    public static GameTeammates? ParseGame(JsonElement game, string targetPuuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPuuid);

        JsonElement? participants = JsonRead.Array(game, "participants");
        JsonElement? identities = JsonRead.Array(game, "participantIdentities");
        if (participants is null || identities is null)
        {
            return null;
        }

        int? targetParticipantId = FindParticipantId(identities.Value, targetPuuid);
        if (targetParticipantId is null)
        {
            return null;
        }

        int queueId = JsonRead.Int(game, "queueId");

        bool remake = false;
        int? targetTeam = null;

        foreach (JsonElement participant in participants.Value.EnumerateArray())
        {
            if (JsonRead.Int(participant, "participantId") != targetParticipantId.Value)
            {
                continue;
            }

            JsonElement? stats = JsonRead.Property(participant, "stats");
            if (stats is null)
            {
                break;
            }

            remake = JsonRead.Bool(stats.Value, "teamEarlySurrendered");
            targetTeam = queueId == ArenaQueueId
                ? JsonRead.NullableInt(stats.Value, "subteamPlacement")
                : JsonRead.NullableInt(participant, "teamId");
            break;
        }

        if (targetTeam is null)
        {
            return null;
        }

        var teammates = new List<TeammatePresence>();
        foreach (JsonElement participant in participants.Value.EnumerateArray())
        {
            int participantId = JsonRead.Int(participant, "participantId");
            if (participantId == targetParticipantId.Value)
            {
                continue;
            }

            int? team = queueId == ArenaQueueId
                ? JsonRead.NullableInt(JsonRead.Property(participant, "stats") ?? default, "subteamPlacement")
                : JsonRead.NullableInt(participant, "teamId");

            if (team != targetTeam)
            {
                continue;
            }

            bool win = JsonRead.Bool(JsonRead.Property(participant, "stats") ?? default, "win");
            if (FindIdentity(identities.Value, participantId) is { } player)
            {
                TeammatePresence? presence = BuildPresence(player, win);
                if (presence is not null)
                {
                    teammates.Add(presence);
                }
            }
        }

        return new GameTeammates(remake, JsonRead.Long(game, "gameCreation"), teammates);
    }

    /// <summary>
    /// 聚合多局的同队记录。
    /// </summary>
    /// <param name="puuid">被查询玩家 puuid（结果回显用）。</param>
    /// <param name="games">各局同队记录，顺序无关。</param>
    /// <param name="tags">已标记玩家的 puuid → 标签；未标记玩家不出现在字典里。</param>
    public static RecentTeammatesResult Aggregate(
        string puuid,
        IEnumerable<GameTeammates> games,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(puuid);
        ArgumentNullException.ThrowIfNull(games);

        var byPuuid = new Dictionary<string, Accumulator>(StringComparer.Ordinal);

        foreach (GameTeammates game in games)
        {
            foreach (TeammatePresence presence in game.Teammates)
            {
                if (!byPuuid.TryGetValue(presence.Puuid, out Accumulator? entry))
                {
                    entry = new Accumulator(presence, game.GameCreation, tags);
                    byPuuid[presence.Puuid] = entry;
                }

                entry.Total++;

                // 重开局只计"同队次数"，不计胜负——否则 3 分钟一局的胜场会污染胜率
                if (!game.Remake)
                {
                    if (presence.Win)
                    {
                        entry.Wins++;
                    }
                    else
                    {
                        entry.Losses++;
                    }
                }

                entry.LastPlayTime = Math.Max(entry.LastPlayTime, game.GameCreation);
            }
        }

        IEnumerable<RecentTeammate> ordered = byPuuid.Values
            .Select(entry => entry.ToRecord())
            .OrderByDescending(teammate => teammate.Tag is not null)
            .ThenByDescending(teammate => teammate.Total)
            .ThenBy(teammate => teammate.Name, StringComparer.Ordinal)
            .Take(MaxCount);

        return new RecentTeammatesResult
        {
            Puuid = puuid,
            Summoners = [.. ordered],
        };
    }

    private static int? FindParticipantId(JsonElement identities, string targetPuuid)
    {
        foreach (JsonElement identity in identities.EnumerateArray())
        {
            if (JsonRead.Property(identity, "player") is not { } player)
            {
                continue;
            }

            if (string.Equals(JsonRead.String(player, "puuid"), targetPuuid, StringComparison.Ordinal))
            {
                return JsonRead.NullableInt(identity, "participantId");
            }
        }

        return null;
    }

    private static JsonElement? FindIdentity(JsonElement identities, int participantId)
    {
        foreach (JsonElement identity in identities.EnumerateArray())
        {
            if (JsonRead.Int(identity, "participantId") != participantId)
            {
                continue;
            }

            return JsonRead.Property(identity, "player");
        }

        return null;
    }

    /// <summary>
    /// 名字取 <c>gameName</c> → <c>summonerName</c> → <c>displayName</c>，再拼 <c>#tagLine</c>。
    /// puuid 为空或为全零占位值时返回 <c>null</c>（该玩家无法被追踪，收进来只会占位置）。
    /// </summary>
    private static TeammatePresence? BuildPresence(JsonElement player, bool win)
    {
        string puuid = JsonRead.String(player, "puuid") ?? string.Empty;
        if (puuid.Length == 0 || puuid == EmptyPuuid)
        {
            return null;
        }

        string gameName = JsonRead.String(player, "gameName") ?? string.Empty;
        string summonerName = JsonRead.String(player, "summonerName") ?? string.Empty;
        string displayName = JsonRead.String(player, "displayName") ?? string.Empty;

        string name = gameName.Length > 0
            ? gameName
            : summonerName.Length > 0 ? summonerName : displayName;

        string tagLine = JsonRead.String(player, "tagLine") ?? string.Empty;
        if (tagLine.Length > 0 && name.Length > 0)
        {
            name = $"{name}#{tagLine}";
        }

        return new TeammatePresence(name, puuid, JsonRead.Int(player, "profileIcon"), win);
    }

    /// <summary>聚合期间的累计器（对外只暴露不可变记录）。</summary>
    private sealed class Accumulator(TeammatePresence first, long lastPlayTime, IReadOnlyDictionary<string, string>? tags)
    {
        public int Total { get; set; }

        public int Wins { get; set; }

        public int Losses { get; set; }

        public long LastPlayTime { get; set; } = lastPlayTime;

        public RecentTeammate ToRecord() => new()
        {
            Name = first.Name,
            Puuid = first.Puuid,
            ProfileIconId = first.ProfileIconId,
            Total = Total,
            Wins = Wins,
            Losses = Losses,
            LastPlayTime = LastPlayTime,
            Tag = tags is not null && tags.TryGetValue(first.Puuid, out string? tag) ? tag : null,
        };
    }
}
