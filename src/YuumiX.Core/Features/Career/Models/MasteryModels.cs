namespace YuumiX.Core.Features.Career.Models;

/// <summary>
/// 英雄熟练度与赛段里程碑单项，对应原版 <c>ChampionMasteryInfo</c>。
/// </summary>
public sealed record ChampionMasteryItem(
    int ChampionId,
    string ChampionName,
    string ChampionIconUrl,
    int ChampionLevel,
    long ChampionPoints,
    long ChampionPointsSinceLastLevel,
    long ChampionPointsUntilNextLevel,
    int ChampionSeasonMilestone,
    IReadOnlyList<string> MilestoneGrades,
    bool ChestGranted,
    bool EligibleForChest,
    int TokensEarned,
    int MarkRequiredForNextLevel,
    long LastPlayTime)
{
    /// <summary>升级进度百分比 (0 ~ 100)。</summary>
    public int ProgressPercentage
    {
        get
        {
            if (ChampionPointsUntilNextLevel <= 0)
            {
                return 100;
            }

            long totalInLevel = ChampionPointsSinceLastLevel + ChampionPointsUntilNextLevel;
            if (totalInLevel <= 0)
            {
                return 0;
            }

            int pct = (int)Math.Round((double)ChampionPointsSinceLastLevel / totalInLevel * 100);
            return Math.Clamp(pct, 0, 100);
        }
    }
}

/// <summary>
/// 英雄熟练度看板数据汇总：顶部 4 指标卡片 + 全英雄熟练度列表。
/// </summary>
public sealed record MasteryOverview(
    IReadOnlyList<ChampionMasteryItem> Champions,
    int TotalChampions,
    long TotalPoints,
    ChampionMasteryItem? MaxLevelHero,
    int EligibleChestCount,
    int GrantedChestCount)
{
    public static readonly MasteryOverview Empty = new(
        [],
        0,
        0,
        null,
        0,
        0);

    /// <summary>
    /// 与 docs/pic/生涯-熟练度.png 截图完全对齐的样例数据（105 位英雄、总分 677,046、迅捷斥候 Lv.38）。
    /// </summary>
    public static readonly MasteryOverview Sample = CreateSample();

    private static MasteryOverview CreateSample()
    {
        var teemo = new ChampionMasteryItem(
            17, "迅捷斥候", "/lol-game-data/assets/v1/champion-icons/17.png",
            38, 392593, 9152, 2007,
            9, ["B+", "A"], false, true, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var malzahar = new ChampionMasteryItem(
            90, "虚空先知", "/lol-game-data/assets/v1/champion-icons/90.png",
            7, 26110, 4500, 5490,
            0, [], false, true, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var brand = new ChampionMasteryItem(
            63, "复仇焰魂", "/lol-game-data/assets/v1/champion-icons/63.png",
            7, 25762, 4200, 5838,
            1, ["B+", "B"], false, true, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var morde = new ChampionMasteryItem(
            82, "铁铠冥魂", "/lol-game-data/assets/v1/champion-icons/82.png",
            6, 17045, 4900, 4555,
            0, [], false, true, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var tristana = new ChampionMasteryItem(
            18, "麦林炮手", "/lol-game-data/assets/v1/champion-icons/18.png",
            5, 15436, 3200, 6164,
            4, ["A"], false, true, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var akali = new ChampionMasteryItem(
            84, "离群之刺", "/lol-game-data/assets/v1/champion-icons/84.png",
            5, 13848, 7400, 7752,
            0, [], false, true, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var xayah = new ChampionMasteryItem(
            498, "逆羽", "/lol-game-data/assets/v1/champion-icons/498.png",
            4, 11791, 8800, 809,
            0, [], false, true, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var braum = new ChampionMasteryItem(
            201, "不破之誓", "/lol-game-data/assets/v1/champion-icons/201.png",
            4, 10706, 7100, 1894,
            0, [], false, true, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var sivir = new ChampionMasteryItem(
            15, "战争女神", "/lol-game-data/assets/v1/champion-icons/15.png",
            4, 10209, 6400, 2391,
            0, [], false, true, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var jhin = new ChampionMasteryItem(
            202, "戏命师", "/lol-game-data/assets/v1/champion-icons/202.png",
            4, 8881, 4400, 3719,
            0, [], false, true, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var jinx = new ChampionMasteryItem(
            222, "暴走萝莉", "/lol-game-data/assets/v1/champion-icons/222.png",
            3, 8134, 3200, 4466,
            0, [], false, true, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var draven = new ChampionMasteryItem(
            119, "荣耀行刑官", "/lol-game-data/assets/v1/champion-icons/119.png",
            3, 7121, 1700, 5479,
            0, [], false, true, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var list = new List<ChampionMasteryItem>
        {
            teemo, malzahar, brand, morde,
            tristana, akali, xayah, braum,
            sivir, jhin, jinx, draven,
        };

        return new MasteryOverview(
            list,
            105,
            677046,
            teemo,
            105,
            0);
    }
}
