using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace YuumiX.Core.Features.GameInfo.Models;

/// <summary>
/// 游戏客户端内网端口（https://127.0.0.1:2999/liveclientdata/playerlist）返回的玩家数据。
/// </summary>
public sealed record LiveClientPlayer
{
    [JsonPropertyName("summonerName")]
    public string? SummonerName { get; init; }

    [JsonPropertyName("riotId")]
    public string? RiotId { get; init; }

    [JsonPropertyName("riotIdGameName")]
    public string? RiotIdGameName { get; init; }

    [JsonPropertyName("riotIdTagLine")]
    public string? RiotIdTagLine { get; init; }

    [JsonPropertyName("championName")]
    public string? ChampionName { get; init; }

    [JsonPropertyName("team")]
    public string? Team { get; init; } // "ORDER"（蓝方） | "CHAOS"（红方）

    [JsonPropertyName("rawChampionName")]
    public string? RawChampionName { get; init; }

    [JsonPropertyName("isBot")]
    public bool IsBot { get; init; }
}

/// <summary>
/// Live Client 2999 端口返回的特殊英文别名/特例映射表（对应旧版 <c>LIVECLIENT_SPECIAL_ALIAS_MAP</c>）。
/// </summary>
public static class LiveClientSpecialAliases
{
    public static readonly FrozenDictionary<string, int> Map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["wukong"] = 62,
        ["monkeyking"] = 62,
        ["drmundo"] = 36,
        ["fiddlesticks"] = 9,
        ["twistedfate"] = 4,
        ["xinzhao"] = 5,
        ["missfortune"] = 21,
        ["jarvaniv"] = 59,
        ["masteryi"] = 11,
        ["tahmkench"] = 223,
        ["aurelionsol"] = 136,
        ["kogmaw"] = 96,
        ["reksai"] = 421,
        ["ksante"] = 897,
        ["leblanc"] = 7,
        ["nunu"] = 20,
        ["nunuiwillump"] = 20,
        ["renataglasc"] = 888,
        ["belveth"] = 200,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
}
