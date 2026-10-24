using System.Text.Json.Serialization;

namespace YuumiX.Core.Features.Opgg.Models;

public sealed record OpggBuildPayload
{
    [JsonPropertyName("meta")]
    public OpggMeta? Meta { get; init; }

    [JsonPropertyName("data")]
    public OpggBuildData? Data { get; init; }
}

public sealed record OpggBuildData
{
    [JsonPropertyName("summary")]
    public OpggBuildSummary? Summary { get; init; }

    [JsonPropertyName("counters")]
    public IReadOnlyList<OpggCounter>? Counters { get; init; }

    [JsonPropertyName("runes")]
    public IReadOnlyList<OpggRunePreset> Runes { get; init; } = [];

    [JsonPropertyName("skills")]
    public IReadOnlyList<OpggSkillOrder>? Skills { get; init; }

    [JsonPropertyName("skill_masteries")]
    public IReadOnlyList<OpggSkillMastery>? SkillMasteries { get; init; }

    [JsonPropertyName("summoner_spells")]
    public IReadOnlyList<OpggIdWinRate> SummonerSpells { get; init; } = [];

    [JsonPropertyName("starter_items")]
    public IReadOnlyList<OpggIdWinRate>? StarterItems { get; init; }

    [JsonPropertyName("core_items")]
    public IReadOnlyList<OpggIdWinRate>? CoreItems { get; init; }

    [JsonPropertyName("boots")]
    public IReadOnlyList<OpggIdWinRate>? Boots { get; init; }

    [JsonPropertyName("last_items")]
    public IReadOnlyList<OpggIdWinRate> LastItems { get; init; } = [];
}

public sealed record OpggBuildSummary
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("positions")]
    public IReadOnlyList<OpggPositionEntry>? Positions { get; init; }

    [JsonPropertyName("average_stats")]
    public OpggStats? AverageStats { get; init; }
}

public sealed record OpggCounter
{
    [JsonPropertyName("champion_id")]
    public int ChampionId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("play")]
    public int Play { get; init; }

    [JsonPropertyName("win")]
    public int Win { get; init; }

    [JsonIgnore]
    public double Rate => Play > 0 ? (double)Win / Play : 0;
}

public sealed record OpggRunePreset
{
    [JsonPropertyName("primary_page_id")]
    public int PrimaryPageId { get; init; }

    [JsonPropertyName("secondary_page_id")]
    public int SecondaryPageId { get; init; }

    [JsonPropertyName("primary_rune_ids")]
    public IReadOnlyList<int> PrimaryRuneIds { get; init; } = [];

    [JsonPropertyName("secondary_rune_ids")]
    public IReadOnlyList<int> SecondaryRuneIds { get; init; } = [];

    [JsonPropertyName("stat_mod_ids")]
    public IReadOnlyList<int> StatModIds { get; init; } = [];

    [JsonPropertyName("win")]
    public int Win { get; init; }

    [JsonPropertyName("play")]
    public int Play { get; init; }

    [JsonPropertyName("pick_rate")]
    public double? PickRate { get; init; }
}

public sealed record OpggIdWinRate
{
    [JsonPropertyName("ids")]
    public IReadOnlyList<int> Ids { get; init; } = [];

    [JsonPropertyName("win")]
    public int Win { get; init; }

    [JsonPropertyName("play")]
    public int Play { get; init; }

    [JsonPropertyName("pick_rate")]
    public double? PickRate { get; init; }
}

public sealed record OpggSkillOrder
{
    [JsonPropertyName("order")]
    public IReadOnlyList<string> Order { get; init; } = [];

    [JsonPropertyName("win")]
    public int Win { get; init; }

    [JsonPropertyName("play")]
    public int Play { get; init; }

    [JsonPropertyName("pick_rate")]
    public double? PickRate { get; init; }
}

public sealed record OpggSkillMastery
{
    [JsonPropertyName("ids")]
    public IReadOnlyList<string> Ids { get; init; } = [];
}
