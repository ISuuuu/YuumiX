namespace YuumiX.Core.Features.GameInfo.Models;

/// <summary>
/// 宿命检测结果，对应原 <c>PlayerFateInfo</c>。
/// </summary>
public sealed record PlayerFateInfo
{
    /// <summary>关系标识："ally"（曾友） | "enemy"（曾敌） | null</summary>
    public string? FateFlag { get; init; }

    /// <summary>最近一场共同对局中目标玩家使用的英雄名称</summary>
    public string? RecentlyChampionName { get; init; }

    /// <summary>最近一场共同对局的 GameId</summary>
    public long? GameId { get; init; }

    /// <summary>最近一场共同对局的创建时间（毫秒戳）</summary>
    public long? GameCreation { get; init; }

    /// <summary>历史同队次数</summary>
    public int AllyCount { get; init; }

    /// <summary>历史对手次数</summary>
    public int EnemyCount { get; init; }

    public static readonly PlayerFateInfo Empty = new();
}
