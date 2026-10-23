using System.Text.Json.Serialization;

namespace YuumiX.Core.Features.Tools.Models;

/// <summary>
/// 英雄皮肤展示条目。
/// </summary>
public sealed class ChampionSkinItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("loadScreenPath")]
    public string LoadScreenPath { get; set; } = string.Empty;

    [JsonPropertyName("splashPath")]
    public string SplashPath { get; set; } = string.Empty;

    [JsonPropertyName("isBase")]
    public bool IsBase { get; set; }
}

/// <summary>
/// 挑战勋章条目（对应 /lol-challenges/v1/challenges/local-player 列表中的项）。
/// </summary>
public sealed class ChallengeItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("currentLevel")]
    public string CurrentLevel { get; set; } = "NONE";

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("percentile")]
    public double Percentile { get; set; }

    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("currentThreshold")]
    public double CurrentThreshold { get; set; }

    [JsonPropertyName("nextThreshold")]
    public double NextThreshold { get; set; }

    [JsonPropertyName("selected")]
    public bool Selected { get; set; }
}

/// <summary>
/// 挑战与称号配置装配负载。
/// </summary>
public sealed class ChallengePreferences
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("challengeIds")]
    public List<long> ChallengeIds { get; set; } = [];

    [JsonPropertyName("bannerAccent")]
    public string? BannerAccent { get; set; }
}
