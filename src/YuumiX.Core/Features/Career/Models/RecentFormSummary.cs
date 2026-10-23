using System.Globalization;
using YuumiX.Core.Features.Match.Models;

namespace YuumiX.Core.Features.Career.Models;

/// <summary>单个英雄的近期出场统计。</summary>
public sealed record RecentChampionStat
{
    public required int ChampionId { get; init; }

    public required int Games { get; init; }

    public required int Wins { get; init; }

    /// <summary>胜率文本（整数百分比，与队列胜率同一口径）。</summary>
    public string WinRateText => Games == 0
        ? "--"
        : ((Wins * 100.0) / Games).ToString("F0", CultureInfo.InvariantCulture) + "%";
}

/// <summary>单个分路的近期出场占比。</summary>
public sealed record RecentPositionStat
{
    public required MatchPosition Position { get; init; }

    public required int Games { get; init; }

    /// <summary>占"可判定分路对局"的百分比（0–100）。</summary>
    public required int Percentage { get; init; }
}

/// <summary>
/// 近期表现的聚合结果，对应原 <c>statsSummary</c> + <c>computeAggregatedChampions</c> +
/// <c>computeAggregatedPositions</c> 三个前端 computed 的合并体。
///
/// <b>为什么放在 Core</b>：这三块口径（KDA 怎么算、重开局算不算、分路占比的分母是什么）
/// 属于业务规则而不是排版。留在界面层的结果是：以后想在"对局信息"页复用同一套统计，
/// 只能复制一份，然后两份口径慢慢漂移。
///
/// <b>与旧版的一处有意差异</b>：重开局（<see cref="MatchSummary.Remake"/>）不计入胜负与
/// 英雄/分路统计。旧版 <c>statsSummary</c> 直接按 <c>win</c> 计数，于是 3 分钟重开会被算成
/// 一场胜负，与 <see cref="MatchSummary"/> 契约里"重开局不参与胜率统计"的约定相反。
/// 代价是与旧版做视觉比对时胜负场次可能差 1–2 场，不得当作回归失败。
/// </summary>
public sealed record RecentFormSummary
{
    /// <summary>参与统计的场次（已排除重开局）。</summary>
    public required int Games { get; init; }

    public required int Wins { get; init; }

    public required int Losses { get; init; }

    public required int Kills { get; init; }

    public required int Deaths { get; init; }

    public required int Assists { get; init; }

    /// <summary>出场最多的英雄（最多 <see cref="DefaultTopChampionCount"/> 个）。</summary>
    public required IReadOnlyList<RecentChampionStat> TopChampions { get; init; }

    /// <summary>分路分布，按场次降序；只含分路可判定的对局。</summary>
    public required IReadOnlyList<RecentPositionStat> Positions { get; init; }

    /// <summary>空结果（没有任何可统计的对局）。</summary>
    public static RecentFormSummary Empty { get; } = new()
    {
        Games = 0,
        Wins = 0,
        Losses = 0,
        Kills = 0,
        Deaths = 0,
        Assists = 0,
        TopChampions = [],
        Positions = [],
    };

    /// <summary>英雄展示上限，与原 <c>aggregatedTopChamps</c> 的 6 保持一致。</summary>
    public const int DefaultTopChampionCount = 6;

    public bool IsEmpty => Games == 0;

    /// <summary>
    /// 总 KDA 文本：零死亡写 <c>Perfect</c>，其余保留一位小数（与 <see cref="MatchSummary.KdaText"/>
    /// 的两位小数不同——这里是"最近 20 场合计"，一位小数已经足够表达趋势）。
    /// </summary>
    public string KdaText => Deaths == 0
        ? "Perfect"
        : ((Kills + Assists) / (double)Deaths).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>分路可判定的对局数（大乱斗、斗魂等娱乐模式不计入分母）。</summary>
    public int PositionGames => Positions.Count == 0 ? 0 : Positions.Sum(position => position.Games);

    /// <summary>
    /// 按对局列表计算近期表现。
    /// </summary>
    /// <param name="matches">对局列表（通常为最近 20 场）。</param>
    /// <param name="topChampionCount">英雄展示上限。</param>
    public static RecentFormSummary Compute(
        IReadOnlyList<MatchSummary> matches,
        int topChampionCount = DefaultTopChampionCount)
    {
        ArgumentNullException.ThrowIfNull(matches);

        var played = matches.Where(match => !match.Remake).ToArray();
        if (played.Length == 0)
        {
            return Empty;
        }

        int wins = 0;
        int losses = 0;
        int kills = 0;
        int deaths = 0;
        int assists = 0;

        var champions = new Dictionary<int, (int Games, int Wins)>();
        var positions = new Dictionary<MatchPosition, int>();

        foreach (MatchSummary match in played)
        {
            if (match.Win)
            {
                wins++;
            }
            else
            {
                losses++;
            }

            kills += match.Kills;
            deaths += match.Deaths;
            assists += match.Assists;

            (int Games, int Wins) champion = champions.GetValueOrDefault(match.ChampionId);
            champions[match.ChampionId] = (champion.Games + 1, champion.Wins + (match.Win ? 1 : 0));

            if (match.Position is { } position)
            {
                positions[position] = positions.GetValueOrDefault(position) + 1;
            }
        }

        // 场次降序；同场次按英雄 ID 升序，保证输出稳定（否则字典迭代顺序会让界面图标乱跳）
        var topChampions = champions
            .OrderByDescending(pair => pair.Value.Games)
            .ThenBy(pair => pair.Key)
            .Take(Math.Max(0, topChampionCount))
            .Select(pair => new RecentChampionStat
            {
                ChampionId = pair.Key,
                Games = pair.Value.Games,
                Wins = pair.Value.Wins,
            })
            .ToArray();

        int positionGames = positions.Values.Sum();
        var positionStats = positions
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Select(pair => new RecentPositionStat
            {
                Position = pair.Key,
                Games = pair.Value,
                Percentage = (int)Math.Round(pair.Value * 100.0 / positionGames, MidpointRounding.AwayFromZero),
            })
            .ToArray();

        return new RecentFormSummary
        {
            Games = played.Length,
            Wins = wins,
            Losses = losses,
            Kills = kills,
            Deaths = deaths,
            Assists = assists,
            TopChampions = topChampions,
            Positions = positionStats,
        };
    }
}
