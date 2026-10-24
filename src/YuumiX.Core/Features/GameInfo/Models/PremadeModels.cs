namespace YuumiX.Core.Features.GameInfo.Models;

/// <summary>
/// 组队/选人/对局快照中的玩家信息。
/// </summary>
public sealed record PremadePlayerLike
{
    public long SummonerId { get; init; }
    public int CellId { get; init; }
    public bool IsEnemy { get; init; }
    public string? TeamParticipantId { get; init; }
    public string? PartyId { get; init; }
    public int ChampionId { get; init; }
    public int ChampionPickIntent { get; init; }
    public string? DisplayName { get; init; }
    public string? SummonerName { get; init; }
    public string? GameName { get; init; }
    public string? TagLine { get; init; }
    public int ProfileIconId { get; init; }
    public string? Puuid { get; init; }
    public bool Bot { get; init; }
    public bool IsBot { get; init; }

    /// <summary>自定义对局中的人机（电脑）标识：LCU 把人机放在 myTeam 里并标记 isHumanoid:true。</summary>
    public bool IsHumanoid { get; init; }

    public int BotChampionId { get; init; }
    public string? BotName { get; init; }
}

/// <summary>
/// 预组队成员信息。
/// </summary>
public sealed record PremadeMember(long SummonerId, string DisplayName, int ChampionId);

/// <summary>
/// 预组队分组。
/// </summary>
public sealed record PremadeGroup(int ColorIdx, IReadOnlyList<PremadeMember> Members);

/// <summary>
/// 组队高亮配色（边框、背景、小圆点，8位 ARGB 字符串）。
/// </summary>
public sealed record PremadeColor(string Border, string Bg, string Dot);

public static class PremadeColors
{
    public static readonly IReadOnlyList<PremadeColor> Palette =
    [
        new PremadeColor("#B3F59E0B", "#38F59E0B", "#FFF59E0B"), // 0 暖橙
        new PremadeColor("#B3EC4899", "#38EC4899", "#FFEC4899"), // 1 柔粉
        new PremadeColor("#B33B82F6", "#383B82F6", "#FF3B82F6"), // 2 蔚蓝
        new PremadeColor("#B310B981", "#3810B981", "#FF10B981"), // 3 翡翠
        new PremadeColor("#B3A855F7", "#38A855F7", "#FFA855F7"), // 4 幽紫
    ];

    public static PremadeColor GetColor(int colorIdx)
    {
        if (colorIdx < 0)
        {
            return new PremadeColor("#00000000", "#00000000", "#00000000");
        }

        return Palette[colorIdx % Palette.Count];
    }
}
