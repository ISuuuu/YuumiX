using System.Text.Json;
using System.Text.Json.Serialization;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Upload;

/// <summary>上传载荷根（对应旧 upload.rs 的 <c>UploadPayload</c>，JSON 键名 camelCase）。</summary>
public sealed record UploadPayload(MatchInfo MatchInfo, IReadOnlyList<ParticipantInfo> Participants);

/// <summary>对局元信息（camelCase 键名，对齐 serde rename_all）。</summary>
public sealed record MatchInfo(
    long MatchId,
    string GameMode,
    string GameType,
    int QueueId,
    string GameCreation,
    long GameDuration,
    string GameVersion,
    IReadOnlyList<ParticipantInfo> Participants);

/// <summary>
/// 参战者信息。字段严格对齐旧实现的 Python 载荷形态：
/// 普通字段 camelCase，召唤师技能与海克斯强化是 PascalCase 特例（serde rename）。
/// </summary>
public sealed record ParticipantInfo(
    string SummonerName,
    string Puuid,
    int TeamId,
    int ChampionId,
    string ChampionName,
    [property: JsonPropertyName("SummonerSpell1Id")] int SummonerSpell1Id,
    [property: JsonPropertyName("SummonerSpell2Id")] int SummonerSpell2Id,
    [property: JsonPropertyName("HexTech0")] int HexTech0,
    [property: JsonPropertyName("HexTech1")] int HexTech1,
    [property: JsonPropertyName("HexTech2")] int HexTech2,
    [property: JsonPropertyName("HexTech3")] int HexTech3,
    [property: JsonPropertyName("HexTech4")] int HexTech4,
    bool Win,
    int Kills,
    int Deaths,
    int Assists,
    int TotalDamageDealtToChampions,
    int PhysicalDamageDealtToChampions,
    int MagicDamageDealtToChampions,
    int TrueDamageDealtToChampions,
    int TotalDamageDealt,
    int PhysicalDamageDealt,
    int MagicDamageDealt,
    int TrueDamageDealt,
    int TotalDamageTaken,
    int PhysicalDamageTaken,
    int MagicalDamageTaken,
    int TotalHeal,
    int GoldEarned,
    int GoldSpent,
    int TotalMinionsKilled,
    int NeutralMinionsKilled,
    int Item0,
    int Item1,
    int Item2,
    int Item3,
    int Item4,
    int Item5,
    int Item6,
    int RoleBoundItem,
    int VisionScore,
    int WardsPlaced,
    int WardsKilled,
    int VisionWardsBoughtInGame,
    int ChampLevel,
    int DoubleKills,
    int TripleKills,
    int QuadraKills,
    int PentaKills,
    int LargestMultiKill,
    int LargestKillingSpree,
    int KillingSprees,
    bool MostKills,
    bool MostAssists,
    bool MostDamageDealt,
    bool MostDamageTaken,
    bool MostGoldEarned,
    bool MostTurretKills,
    bool MostHealingDone,
    int TurretKills,
    int InhibitorKills,
    bool FirstBloodKill,
    bool FirstBloodAssist,
    bool FirstTowerKill,
    bool FirstTowerAssist,
    int DamageDealtToTurrets,
    int DamageDealtToObjectives,
    int PerkPrimaryStyle,
    int PerkSubStyle);

/// <summary>
/// 把 LCU 对局详情原始 JSON（<c>/lol-match-history/v1/games/{id}</c>）转换成上传载荷。
/// 纯函数：字段映射规则是上传契约里最容易漂移的部分（内层/外层参与者排序、海克斯提取去重），
/// 必须可单测。
/// </summary>
public static class UploadPayloadBuilder
{
    /// <summary>
    /// 构建上传载荷。
    /// </summary>
    /// <param name="gameDetail">LCU 对局详情 JSON 根元素。</param>
    /// <param name="currentSummonerPuuid">当前登录账号 puuid（命中的参与者排内层，其余排外层）。</param>
    /// <param name="championNameResolver">英雄 ID → 名称（缺省返回 "Unknown"）。</param>
    public static UploadPayload? FromGameDetail(
        JsonElement gameDetail,
        string? currentSummonerPuuid,
        Func<int, string>? championNameResolver = null)
    {
        if (gameDetail.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        long matchId = JsonRead.Long(gameDetail, "gameId", 0);
        long gameCreationMs = JsonRead.Long(gameDetail, "gameCreation", 0);
        string gameCreation = gameCreationMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(gameCreationMs)
                .ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss")
            : DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss");

        var matchInfo = new MatchInfo(
            matchId,
            JsonRead.String(gameDetail, "gameMode") ?? string.Empty,
            JsonRead.String(gameDetail, "gameType") ?? string.Empty,
            JsonRead.Int(gameDetail, "queueId", 0),
            gameCreation,
            JsonRead.Long(gameDetail, "gameDuration", 0),
            JsonRead.String(gameDetail, "gameVersion") ?? string.Empty,
            []);

        var identities = new Dictionary<int, JsonElement>();
        if (JsonRead.Array(gameDetail, "participantIdentities") is { } idents)
        {
            foreach (var ident in idents.EnumerateArray())
            {
                int pid = JsonRead.Int(ident, "participantId", 0);
                if (ident.TryGetProperty("player", out var player) && pid != 0)
                {
                    identities[pid] = player;
                }
            }
        }

        var all = new List<ParticipantInfo>();
        if (JsonRead.Array(gameDetail, "participants") is { } participants)
        {
            foreach (var p in participants.EnumerateArray())
            {
                int pid = JsonRead.Int(p, "participantId", 0);
                JsonElement? player = identities.TryGetValue(pid, out var identPlayer) ? identPlayer : null;
                all.Add(BuildParticipant(p, player, championNameResolver));
            }
        }

        // 内/外层拆分（对齐旧 build_upload_payload）：当前账号排内层且只有一位，
        // 找不到当前账号时内层取首位
        var inner = new List<ParticipantInfo>();
        var outer = new List<ParticipantInfo>();
        bool found = false;
        if (!string.IsNullOrEmpty(currentSummonerPuuid))
        {
            foreach (var p in all)
            {
                if (!found && p.Puuid == currentSummonerPuuid)
                {
                    inner.Add(p);
                    found = true;
                }
                else
                {
                    outer.Add(p);
                }
            }

            if (!found && outer.Count > 0)
            {
                inner.Add(outer[0]);
                outer.RemoveAt(0);
            }
        }
        else if (all.Count > 0)
        {
            inner.Add(all[0]);
            outer.AddRange(all.Skip(1));
        }

        return new UploadPayload(
            matchInfo with { Participants = inner },
            outer);
    }

    private static ParticipantInfo BuildParticipant(
        JsonElement p,
        JsonElement? player,
        Func<int, string>? championNameResolver)
    {
        string summonerName = "Unknown";
        string puuid = string.Empty;
        if (player is { } pl)
        {
            string name = FirstNonEmpty(JsonRead.String(pl, "gameName"), JsonRead.String(pl, "summonerName")) ?? "Unknown";
            string? tag = JsonRead.String(pl, "tagLine");
            summonerName = tag is not null ? $"{name}#{tag}" : name;
            puuid = JsonRead.String(pl, "puuid") ?? string.Empty;
        }

        int championId = JsonRead.Int(p, "championId", 0);
        string championName = championNameResolver?.Invoke(championId) ?? "Unknown";

        JsonElement? stats = p.TryGetProperty("stats", out var s) ? s : null;
        var hextech = ExtractHextechIds(stats, p);

        return new ParticipantInfo(
            summonerName,
            puuid,
            JsonRead.Int(p, "teamId", 0),
            championId,
            championName,
            JsonRead.Int(p, "spell1Id", 0),
            JsonRead.Int(p, "spell2Id", 0),
            hextech[0], hextech[1], hextech[2], hextech[3], hextech[4],
            SBool(stats, "win"),
            SInt(stats, "kills"),
            SInt(stats, "deaths"),
            SInt(stats, "assists"),
            SInt(stats, "totalDamageDealtToChampions"),
            SInt(stats, "physicalDamageDealtToChampions"),
            SInt(stats, "magicDamageDealtToChampions"),
            SInt(stats, "trueDamageDealtToChampions"),
            SInt(stats, "totalDamageDealt"),
            SInt(stats, "physicalDamageDealt"),
            SInt(stats, "magicDamageDealt"),
            SInt(stats, "trueDamageDealt"),
            SInt(stats, "totalDamageTaken"),
            SInt(stats, "physicalDamageTaken"),
            SInt(stats, "magicalDamageTaken"),
            SInt(stats, "totalHeal"),
            SInt(stats, "goldEarned"),
            SInt(stats, "goldSpent"),
            SInt(stats, "totalMinionsKilled"),
            SInt(stats, "neutralMinionsKilled"),
            SInt(stats, "item0"), SInt(stats, "item1"), SInt(stats, "item2"),
            SInt(stats, "item3"), SInt(stats, "item4"), SInt(stats, "item5"),
            SInt(stats, "item6"),
            SInt(stats, "roleBoundItem"),
            SInt(stats, "visionScore"),
            SInt(stats, "wardsPlaced"),
            SInt(stats, "wardsKilled"),
            SInt(stats, "visionWardsBoughtInGame"),
            SInt(stats, "champLevel"),
            SInt(stats, "doubleKills"),
            SInt(stats, "tripleKills"),
            SInt(stats, "quadraKills"),
            SInt(stats, "pentaKills"),
            SInt(stats, "largestMultiKill"),
            SInt(stats, "largestKillingSpree"),
            SInt(stats, "killingSprees"),
            SBool(stats, "mostKills"),
            SBool(stats, "mostAssists"),
            SBool(stats, "mostDamageDealt"),
            SBool(stats, "mostDamageTaken"),
            SBool(stats, "mostGoldEarned"),
            SBool(stats, "mostTurretKills"),
            SBool(stats, "mostHealingDone"),
            SInt(stats, "turretKills"),
            SInt(stats, "inhibitorKills"),
            SBool(stats, "firstBloodKill"),
            SBool(stats, "firstBloodAssist"),
            SBool(stats, "firstTowerKill"),
            SBool(stats, "firstTowerAssist"),
            SInt(stats, "damageDealtToTurrets"),
            SInt(stats, "damageDealtToObjectives"),
            SInt(stats, "perkPrimaryStyle"),
            SInt(stats, "perkSubStyle"));
    }

    /// <summary>海克斯强化提取（对齐旧 extract_hextech_ids）：stats 层与 participant 层各取
    /// <c>augments</c> 数组与 <c>playerAugment1..5</c>，按出现顺序去重，最多 5 个。</summary>
    public static int[] ExtractHextechIds(JsonElement? stats, JsonElement participant)
    {
        var ordered = new List<int>();
        var seen = new HashSet<int>();

        void Collect(JsonElement source)
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (JsonRead.Array(source, "augments") is { } augments)
            {
                foreach (var item in augments.EnumerateArray())
                {
                    int id = item.ValueKind == JsonValueKind.Number ? item.GetInt32() : 0;
                    if (id != 0 && seen.Add(id))
                    {
                        ordered.Add(id);
                    }
                }
            }

            for (var i = 1; i <= 5; i++)
            {
                int id = JsonRead.Int(source, $"playerAugment{i}", 0);
                if (id != 0 && seen.Add(id))
                {
                    ordered.Add(id);
                }
            }
        }

        Collect(stats ?? default);
        Collect(participant);

        var result = new int[5];
        for (var i = 0; i < Math.Min(5, ordered.Count); i++)
        {
            result[i] = ordered[i];
        }

        return result;
    }

    private static int SInt(JsonElement? source, string name) =>
        source is { } s && s.ValueKind == JsonValueKind.Object ? JsonRead.Int(s, name, 0) : 0;

    private static bool SBool(JsonElement? source, string name) =>
        source is { } s && s.ValueKind == JsonValueKind.Object && JsonRead.Bool(s, name, false);

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrEmpty(first) ? first : second;
}
