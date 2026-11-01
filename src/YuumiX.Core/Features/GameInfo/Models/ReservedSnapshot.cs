namespace YuumiX.Core.Features.GameInfo.Models;

/// <summary>
/// 保留对局快照：双方队伍、玩家数据、组队色与归属信息
/// （对应旧版 localStorage 的 <c>yuumi_last_gameflow_* / yuumi_last_game_*</c> 键组）。
/// </summary>
public sealed record ReservedSnapshot
{
    public IReadOnlyList<PremadePlayerLike> MyTeam { get; init; } = [];

    public IReadOnlyList<PremadePlayerLike> TheirTeam { get; init; } = [];

    public IReadOnlyDictionary<string, GameInfoPlayer> PlayerData { get; init; } =
        new Dictionary<string, GameInfoPlayer>();

    public IReadOnlyDictionary<string, int> PremadeColorsMy { get; init; } = new Dictionary<string, int>();

    public IReadOnlyDictionary<string, int> PremadeColorsTheir { get; init; } = new Dictionary<string, int>();

    /// <summary>快照归属的对局 gameId；直连进游戏且未解析到 gameId 时为 <c>null</c>。</summary>
    public long? GameId { get; init; }

    /// <summary>写入时双方队伍总人数（防 InProgress 精简会话毒化完整快照）。</summary>
    public int TeamCount { get; init; }

    /// <summary>写入时已加载（非 loading 占位）的玩家数。</summary>
    public int LoadedCount { get; init; }
}
