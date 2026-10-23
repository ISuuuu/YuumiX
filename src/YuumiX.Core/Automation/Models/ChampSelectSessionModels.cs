using System.Text.Json.Serialization;

namespace YuumiX.Core.Automation.Models;

/// <summary>
/// 英雄选择阶段完整会话数据（对应 LCU /lol-champ-select/v1/session）。
/// </summary>
public sealed class ChampSelectSession
{
    [JsonPropertyName("actions")]
    public List<List<ChampSelectAction>> Actions { get; set; } = [];

    [JsonPropertyName("localPlayerCellId")]
    public int LocalPlayerCellId { get; set; }

    [JsonPropertyName("myTeam")]
    public List<ChampSelectPlayer> MyTeam { get; set; } = [];

    [JsonPropertyName("bans")]
    public ChampSelectBans Bans { get; set; } = new();

    [JsonPropertyName("pickOrderSwaps")]
    public List<ChampSelectSwap> PickOrderSwaps { get; set; } = [];

    [JsonPropertyName("trades")]
    public List<ChampSelectTrade> Trades { get; set; } = [];

    [JsonPropertyName("timer")]
    public ChampSelectTimer? Timer { get; set; }

    [JsonPropertyName("benchEnabled")]
    public bool BenchEnabled { get; set; }

    [JsonPropertyName("queueId")]
    public int? QueueId { get; set; }
}

public sealed class ChampSelectAction
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("actorCellId")]
    public int ActorCellId { get; set; }

    [JsonPropertyName("championId")]
    public int ChampionId { get; set; }

    [JsonPropertyName("completed")]
    public bool Completed { get; set; }

    [JsonPropertyName("isInProgress")]
    public bool IsInProgress { get; set; }

    [JsonPropertyName("type")]
    public string ActionType { get; set; } = string.Empty;
}

public sealed class ChampSelectPlayer
{
    [JsonPropertyName("cellId")]
    public int CellId { get; set; }

    [JsonPropertyName("championId")]
    public int ChampionId { get; set; }

    [JsonPropertyName("championPickIntent")]
    public int ChampionPickIntent { get; set; }

    [JsonPropertyName("assignedPosition")]
    public string AssignedPosition { get; set; } = string.Empty;

    [JsonPropertyName("team")]
    public int Team { get; set; }
}

public sealed class ChampSelectBans
{
    [JsonPropertyName("myTeamBans")]
    public List<int> MyTeamBans { get; set; } = [];

    [JsonPropertyName("theirTeamBans")]
    public List<int> TheirTeamBans { get; set; } = [];
}

public sealed class ChampSelectSwap
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;
}

public sealed class ChampSelectTrade
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;
}

public sealed class ChampSelectTimer
{
    [JsonPropertyName("adjustedTimeLeftInPhase")]
    public ulong? AdjustedTimeLeftInPhase { get; set; }

    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("internalNowInEpochMs")]
    public ulong? InternalNowInEpochMs { get; set; }
}
