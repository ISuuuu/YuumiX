using System.Text.Json.Serialization;

namespace YuumiX.Core.Features.Opgg.Models;

/// <summary>
/// 归一化后的英雄梯度榜条目（供 UI 呈现）。
/// </summary>
public sealed record TierListItem
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public double? WinRate { get; init; }
    public double? PickRate { get; init; }
    public double? BanRate { get; init; }
    public double? Kda { get; init; }
    public string? Tier { get; init; }
    public int? Rank { get; init; }
    public string? Position { get; init; }
    public IReadOnlyList<int> Counters { get; init; } = [];
}

public sealed record OpggMeta
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

public sealed record OpggTierPayload
{
    [JsonPropertyName("meta")]
    public OpggMeta? Meta { get; init; }

    [JsonPropertyName("data")]
    public IReadOnlyList<OpggChampionEntry>? Data { get; init; }
}

public sealed record OpggChampionEntry
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("positions")]
    public IReadOnlyList<OpggPositionEntry>? Positions { get; init; }

    [JsonPropertyName("average_stats")]
    public OpggStats? AverageStats { get; init; }
}

public sealed record OpggPositionEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("stats")]
    public OpggStats? Stats { get; init; }

    [JsonPropertyName("counters")]
    public IReadOnlyList<OpggCounterEntry>? Counters { get; init; }
}

public sealed record OpggCounterEntry
{
    [JsonPropertyName("champion_id")]
    public int ChampionId { get; init; }
}

public sealed record OpggTierData
{
    [JsonPropertyName("tier")]
    [JsonConverter(typeof(NumberOrStringConverter))]
    public string? Tier { get; init; }

    [JsonPropertyName("rank")]
    public int? Rank { get; init; }
}

public sealed record OpggStats
{
    [JsonPropertyName("win_rate")]
    public double? WinRateSnake { get; init; }

    [JsonPropertyName("winRate")]
    public double? WinRateCamel { get; init; }

    [JsonIgnore]
    public double? WinRate => WinRateSnake ?? WinRateCamel;

    [JsonPropertyName("pick_rate")]
    public double? PickRateSnake { get; init; }

    [JsonPropertyName("pickRate")]
    public double? PickRateCamel { get; init; }

    [JsonIgnore]
    public double? PickRate => PickRateSnake ?? PickRateCamel;

    [JsonPropertyName("ban_rate")]
    public double? BanRateSnake { get; init; }

    [JsonPropertyName("banRate")]
    public double? BanRateCamel { get; init; }

    [JsonIgnore]
    public double? BanRate => BanRateSnake ?? BanRateCamel;

    [JsonPropertyName("kda")]
    public double? Kda { get; init; }

    [JsonPropertyName("tier")]
    [JsonConverter(typeof(NumberOrStringConverter))]
    public string? Tier { get; init; }

    [JsonPropertyName("rank")]
    public int? Rank { get; init; }

    [JsonPropertyName("tier_data")]
    public OpggTierData? TierData { get; init; }
}
