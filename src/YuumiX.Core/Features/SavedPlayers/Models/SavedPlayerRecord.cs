namespace YuumiX.Core.Features.SavedPlayers.Models;

/// <summary>
/// 路人集保存的玩家信息，对应原项目 SavedPlayerDto。
/// </summary>
public sealed record SavedPlayerRecord
{
    public required string Puuid { get; init; }
    public required string SelfPuuid { get; init; }
    public string Region { get; init; } = string.Empty;
    public string RsoPlatformId { get; init; } = string.Empty;
    public string? Tag { get; init; }
    public required string SummonerName { get; init; }
    public int ProfileIconId { get; init; }
    public string? TagLine { get; init; }
    public int ChampionId { get; init; }
    public long UpdateAt { get; init; }
    public long? LastMetAt { get; init; }
    public string? LastQueueType { get; init; }
    public int EncounterCount { get; init; } = 1;
}

/// <summary>
/// 相遇对局历史记录，对应原项目 EncounteredGameDto。
/// </summary>
public sealed record EncounteredGameRecord
{
    public long Id { get; init; }
    public long GameId { get; init; }
    public required string Puuid { get; init; }
    public required string SelfPuuid { get; init; }
    public string Region { get; init; } = string.Empty;
    public string RsoPlatformId { get; init; } = string.Empty;
    public string QueueType { get; init; } = string.Empty;
    public long UpdateAt { get; init; }
}

/// <summary>
/// 对局结束时传入的单个玩家相遇信息。
/// </summary>
public sealed record EncounterPlayerEntry
{
    public required string Puuid { get; init; }
    public required string SummonerName { get; init; }
    public int ProfileIconId { get; init; }
    public string? TagLine { get; init; }
    public int ChampionId { get; init; }
}

/// <summary>
/// 分页结果包装。
/// </summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, long TotalCount);
