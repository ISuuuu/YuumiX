namespace YuumiX.Core.Features.SavedPlayers.Models;

/// <summary>
/// 一位"最近同队"的玩家，对应原项目 <c>match_parser.rs</c> 的 <c>RecentTeammate</c>。
///
/// 与原实现的一处差异：原 <c>icon</c> 是拼好的 <c>/lol-game-data/assets/v1/profile-icons/{id}.jpg</c>
/// 路径字符串，这里改存 <see cref="ProfileIconId"/>，图标由 <c>LcuImage</c> 按 ID 解析（ADR-11）。
/// 理由同 <c>MatchSummary</c>：模型不该携带资源路径，否则解析器被迫依赖资源表。
/// </summary>
public sealed record RecentTeammate
{
    /// <summary>显示名；有 tagLine 时为 <c>gameName#tagLine</c>。</summary>
    public required string Name { get; init; }

    /// <summary>玩家 puuid，也是聚合与标记查询的键。</summary>
    public required string Puuid { get; init; }

    /// <summary>头像 ID。</summary>
    public required int ProfileIconId { get; init; }

    /// <summary>
    /// 同队次数。**包含重开局**：重开也是一次"同队"，但胜负不计（见 <see cref="Wins"/> / <see cref="Losses"/>），
    /// 这是原实现的既定口径，界面上"3 次同队 0 胜 0 负"就是这个含义。
    /// </summary>
    public required int Total { get; init; }

    /// <summary>同队且获胜的次数（重开局不计）。</summary>
    public required int Wins { get; init; }

    /// <summary>同队且失利的次数（重开局不计）。</summary>
    public required int Losses { get; init; }

    /// <summary>最近一次同队的开局时间，Unix 毫秒。</summary>
    public required long LastPlayTime { get; init; }

    /// <summary>用户为这位玩家打的标签（"路人集"里的备注）；未标记时为 <c>null</c>。</summary>
    public string? Tag { get; init; }

    /// <summary>胜率（%）。总场次为 0 时返回 <c>null</c>，避免渲染成 0%。</summary>
    public int? WinRate => Wins + Losses == 0 ? null : (int)Math.Round(100.0 * Wins / (Wins + Losses));
}

/// <summary>最近队友查询结果，对应原项目 <c>RecentTeammatesResponse</c>。</summary>
public sealed record RecentTeammatesResult
{
    /// <summary>被查询玩家 puuid（回显，便于界面校验结果归属）。</summary>
    public required string Puuid { get; init; }

    /// <summary>同队玩家，已按"有标签优先、其次同队次数降序"排序并截断到 5 人。</summary>
    public required IReadOnlyList<RecentTeammate> Summoners { get; init; }
}
