using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.Core.Features.GameInfo.Models;

/// <summary>
/// 对局信息中的单个玩家透视数据（对应旧版 <c>PlayerData</c>）。
/// </summary>
public sealed record GameInfoPlayer
{
    /// <summary>召唤师基础档案</summary>
    public SummonerProfile? Info { get; init; }

    /// <summary>战绩列表（首屏 10 局，后台最多扩充至 50 场）</summary>
    public IReadOnlyList<MatchSummary> Matches { get; init; } = [];

    /// <summary>单双排段位成绩</summary>
    public RankedQueueEntry? Solo { get; init; }

    /// <summary>灵活组排段位成绩</summary>
    public RankedQueueEntry? Flex { get; init; }

    /// <summary>加载占位标记</summary>
    public bool Loading { get; init; }

    /// <summary>战绩是否隐藏（无战绩或接口受限）</summary>
    public bool MatchHistoryHidden { get; init; }

    /// <summary>生涯是否私密</summary>
    public bool IsProfilePrivate { get; init; }

    /// <summary>当前选用/锁定英雄 ID</summary>
    public int ChampionId { get; init; }

    /// <summary>近期平均 KDA</summary>
    public double? AvgKda { get; init; }

    /// <summary>近期胜率（0 ~ 100）</summary>
    public double? WinRate { get; init; }

    /// <summary>近期胜场数</summary>
    public int? WinCount { get; init; }

    /// <summary>近期负场数</summary>
    public int? LossesCount { get; init; }

    /// <summary>宿命标记："ally" | "enemy" | null</summary>
    public string? FateFlag { get; init; }

    /// <summary>宿命对局中最近使用的英雄名称</summary>
    public string? RecentlyChampionName { get; init; }

    /// <summary>宿命对局的创建时间戳（毫秒）</summary>
    public long? FateGameCreation { get; init; }

    /// <summary>宿命是否为最近一场（默认 true）</summary>
    public bool FateIsLastGame { get; init; } = true;

    /// <summary>宿命共同作为友方次数</summary>
    public int FateAllyCount { get; init; }

    /// <summary>宿命作为敌方次数</summary>
    public int FateEnemyCount { get; init; }

    /// <summary>英雄熟练度列表</summary>
    public IReadOnlyList<PlayerChampionMastery>? Masteries { get; init; }

    /// <summary>是否已拉取熟练度</summary>
    public bool MasteryFetched { get; init; }

    /// <summary>近期连胜/连负</summary>
    public StreakInfo? Streak { get; init; }

    /// <summary>近期分路分布</summary>
    public AggregatedPositions? Positions { get; init; }

    /// <summary>近期常用英雄（前 5 位）</summary>
    public IReadOnlyList<AggregatedChampion>? TopChampions { get; init; }

    /// <summary>优先单双排，无则灵活排的段位</summary>
    public RankedQueueEntry? PrimaryRank => Solo?.HasRank == true ? Solo : (Flex?.HasRank == true ? Flex : Solo ?? Flex);

    /// <summary>显示名</summary>
    public string DisplayName => Info?.DisplayName ?? Info?.GameName ?? "玩家";
}
