namespace YuumiX.Core.Features.GameInfo.Models;

/// <summary>
/// 路人集精简标记（对局信息页徽章用），对应旧版 <c>saved_players.rs</c> 的 <c>SavedPlayerMarker</c>。
/// </summary>
public sealed record SavedPlayerMarker
{
    /// <summary>自定义标记（无则为 null）</summary>
    public string? Tag { get; init; }

    /// <summary>相遇次数</summary>
    public int EncounterCount { get; init; }

    /// <summary>最近相遇对局 ID</summary>
    public long? LastEncounteredGameId { get; init; }

    /// <summary>最近相遇时间（秒或毫秒时间戳，展示前需归一化）</summary>
    public long? LastMetAt { get; init; }

    /// <summary>全部相遇对局 ID（去重）</summary>
    public IReadOnlyList<long> EncounteredGameIds { get; init; } = [];
}
