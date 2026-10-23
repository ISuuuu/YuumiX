using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.Core.Features.GameInfo.Models;

public static class GameInfoSampleFactory
{
    public static GameInfoState CreateSample()
    {
        var myTeam = new List<PremadePlayerLike>
        {
            new() { CellId = 0, Puuid = "p0", SummonerId = 1000, ChampionId = 157, DisplayName = "疾风剑豪#国服", TeamParticipantId = "100" },
            new() { CellId = 1, Puuid = "p1", SummonerId = 1001, ChampionId = 64, DisplayName = "瞎子王#001", TeamParticipantId = "100" },
            new() { CellId = 2, Puuid = "p2", SummonerId = 1002, ChampionId = 103, DisplayName = "九尾小狐狸#艾欧", TeamParticipantId = "0" },
            new() { CellId = 3, Puuid = "p3", SummonerId = 1003, ChampionId = 222, DisplayName = "萝莉飞弹#皮城", TeamParticipantId = "0" },
            new() { CellId = 4, Puuid = "p4", SummonerId = 1004, ChampionId = 412, DisplayName = "魂锁典狱长#暗影", TeamParticipantId = "0" },
        };

        var theirTeam = new List<PremadePlayerLike>
        {
            new() { CellId = 5, Puuid = "p5", SummonerId = 2000, ChampionId = 54, DisplayName = "巨石强森#熔岩", TeamParticipantId = "200", IsEnemy = true },
            new() { CellId = 6, Puuid = "p6", SummonerId = 2001, ChampionId = 59, DisplayName = "德玛西亚皇子#嘉文", TeamParticipantId = "200", IsEnemy = true },
            new() { CellId = 7, Puuid = "p7", SummonerId = 2002, ChampionId = 238, DisplayName = "影流之刃#劫", TeamParticipantId = "200", IsEnemy = true },
            new() { CellId = 8, Puuid = "p8", SummonerId = 2003, ChampionId = 145, DisplayName = "虚空之女#卡莎", TeamParticipantId = "0", IsEnemy = true },
            new() { CellId = 9, Puuid = "p9", SummonerId = 2004, ChampionId = 111, DisplayName = "深海泰坦#诺提", TeamParticipantId = "0", IsEnemy = true },
        };

        var playerData = new Dictionary<string, GameInfoPlayer>();

        void AddPlayer(
            int cellId,
            string puuid,
            long summonerId,
            int champId,
            string name,
            string tier,
            string div,
            int lp,
            int winCount,
            int lossCount,
            double kda,
            StreakInfo? streak = null,
            string? fate = null)
        {
            var matches = CreateMatches(champId, winCount, lossCount);
            var player = new GameInfoPlayer
            {
                Info = new SummonerProfile
                {
                    AccountId = summonerId,
                    SummonerId = summonerId,
                    SummonerLevel = 150,
                    PercentCompleteForNextLevel = 50,
                    XpSinceLastLevel = 1000,
                    XpUntilNextLevel = 2000,
                    Puuid = puuid,
                    DisplayName = name,
                    GameName = name.Split('#')[0],
                    TagLine = name.Contains('#') ? name.Split('#')[1] : "001",
                    ProfileIconId = 1,
                },
                ChampionId = champId,
                Solo = new RankedQueueEntry
                {
                    QueueType = RankedQueueTypes.SoloDuo,
                    Tier = tier,
                    Division = div,
                    LeaguePoints = lp,
                    Wins = winCount * 5,
                    Losses = lossCount * 5,
                },
                Matches = matches,
                WinCount = winCount,
                LossesCount = lossCount,
                WinRate = Math.Round((double)winCount / (winCount + lossCount) * 100),
                AvgKda = kda,
                Streak = streak,
                FateFlag = fate,
                Masteries = [new PlayerChampionMastery(champId, 7, 125000)],
                MasteryFetched = true,
            };

            playerData[cellId.ToString()] = player;
            playerData[puuid] = player;
            playerData[summonerId.ToString()] = player;
        }

        AddPlayer(0, "p0", 1000, 157, "疾风剑豪#国服", "MASTER", "I", 188, 7, 3, 3.8, new StreakInfo("win", 3));
        AddPlayer(1, "p1", 1001, 64, "瞎子王#001", "DIAMOND", "I", 65, 6, 4, 3.2);
        AddPlayer(2, "p2", 1002, 103, "九尾小狐狸#艾欧", "DIAMOND", "II", 42, 5, 5, 2.8, fate: "ally");
        AddPlayer(3, "p3", 1003, 222, "萝莉飞弹#皮城", "EMERALD", "I", 89, 4, 6, 2.1);
        AddPlayer(4, "p4", 1004, 412, "魂锁典狱长#暗影", "EMERALD", "II", 20, 6, 4, 3.5);

        AddPlayer(5, "p5", 2000, 54, "巨石强森#熔岩", "DIAMOND", "III", 15, 5, 5, 2.5);
        AddPlayer(6, "p6", 2001, 59, "德玛西亚皇子#嘉文", "DIAMOND", "IV", 78, 6, 4, 3.0);
        AddPlayer(7, "p7", 2002, 238, "影流之刃#劫", "MASTER", "I", 240, 8, 2, 4.5, new StreakInfo("win", 4), fate: "enemy");
        AddPlayer(8, "p8", 2003, 145, "虚空之女#卡莎", "DIAMOND", "I", 50, 6, 4, 3.1);
        AddPlayer(9, "p9", 2004, 111, "深海泰坦#诺提", "EMERALD", "I", 33, 5, 5, 2.4);

        var colorsMy = new Dictionary<string, int>
        {
            ["p0"] = 0, ["p1"] = 0, ["0"] = 0, ["1"] = 0, ["1000"] = 0, ["1001"] = 0,
            ["p2"] = -1, ["p3"] = -1, ["p4"] = -1,
        };

        var colorsTheir = new Dictionary<string, int>
        {
            ["p5"] = 1, ["p6"] = 1, ["p7"] = 1, ["5"] = 1, ["6"] = 1, ["7"] = 1, ["2000"] = 1, ["2001"] = 1, ["2002"] = 1,
            ["p8"] = -1, ["p9"] = -1,
        };

        return new GameInfoState
        {
            Phase = GamePhase.InProgress,
            MapSide = "blue",
            ShouldShowContent = true,
            MyTeam = myTeam,
            TheirTeam = theirTeam,
            PlayerData = playerData,
            PremadeColorsMy = colorsMy,
            PremadeColorsTheir = colorsTheir,
            CurrentSummonerId = 1000,
            CurrentSummonerPuuid = "p0",
            ViewColumns = 10,
        };
    }

    private static IReadOnlyList<MatchSummary> CreateMatches(int championId, int winCount, int lossCount)
    {
        var list = new List<MatchSummary>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (int i = 0; i < winCount; i++)
        {
            list.Add(new MatchSummary
            {
                GameId = 100000 + i,
                QueueId = 420,
                QueueName = "排位赛",
                MapName = "召唤师峡谷",
                GameCreation = now - (i + 1) * 3600_000,
                DurationSeconds = 1800 + i * 60,
                Win = true,
                Remake = false,
                ChampionId = championId,
                Spell1Id = 4,
                Spell2Id = 12,
                ChampionLevel = 16,
                Kills = 8 + i,
                Deaths = 2 + (i % 2),
                Assists = 6 + i,
                ItemIds = [3031, 3072, 3006, 0, 0, 0, 3340],
                RuneId = 8000,
                CreepScore = 220,
                Gold = 13500,
                DamageToChampions = 28000,
                TotalHeal = 2000,
                AugmentIds = [],
            });
        }

        for (int i = 0; i < lossCount; i++)
        {
            list.Add(new MatchSummary
            {
                GameId = 200000 + i,
                QueueId = 420,
                QueueName = "排位赛",
                MapName = "召唤师峡谷",
                GameCreation = now - (winCount + i + 1) * 3600_000,
                DurationSeconds = 1500,
                Win = false,
                Remake = false,
                ChampionId = championId,
                Spell1Id = 4,
                Spell2Id = 12,
                ChampionLevel = 13,
                Kills = 3,
                Deaths = 6 + i,
                Assists = 4,
                ItemIds = [3031, 3006, 0, 0, 0, 0, 3340],
                RuneId = 8000,
                CreepScore = 150,
                Gold = 8500,
                DamageToChampions = 14000,
                TotalHeal = 1000,
                AugmentIds = [],
            });
        }

        return list;
    }
}
