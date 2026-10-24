using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner.Models;
using Xunit;

namespace YuumiX.Core.Tests;

public class GameInfoPureFunctionsTests
{
    private static MatchSummary CreateMatch(
        bool win = true,
        bool remake = false,
        int kills = 5,
        int deaths = 2,
        int assists = 10,
        int championId = 1,
        MatchPosition? position = MatchPosition.Top,
        int queueId = 420)
    {
        return new MatchSummary
        {
            GameId = 10001,
            QueueId = queueId,
            QueueName = "排位赛",
            MapName = "召唤师峡谷",
            GameCreation = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DurationSeconds = 1800,
            Win = win,
            Remake = remake,
            ChampionId = championId,
            Spell1Id = 4,
            Spell2Id = 12,
            ChampionLevel = 18,
            Kills = kills,
            Deaths = deaths,
            Assists = assists,
            ItemIds = [0, 0, 0, 0, 0, 0, 0],
            RuneId = 8000,
            CreepScore = 200,
            Gold = 12000,
            DamageToChampions = 25000,
            TotalHeal = 3000,
            AugmentIds = [],
            Position = position,
        };
    }

    [Fact]
    public void MatchStatsCalculator_RemakeDoesNotCountInWinRateOrStreak()
    {
        var matches = new List<MatchSummary>
        {
            CreateMatch(win: true, remake: false),
            CreateMatch(win: false, remake: true), // 重开负局，不应计入负场或连胜阻断
            CreateMatch(win: true, remake: false),
        };

        var stats = MatchStatsCalculator.ComputeMatchStats(matches);

        Assert.Equal(2, stats.WinCount);
        Assert.Equal(0, stats.LossesCount);
        Assert.Equal(100, stats.WinRate);
        Assert.NotNull(stats.Streak);
        Assert.Equal("win", stats.Streak.Type);
        Assert.Equal(2, stats.Streak.Count);
    }

    [Fact]
    public void MatchStatsCalculator_ZeroDeathsUsesDenominatorOneForAvgKda()
    {
        var matches = new List<MatchSummary>
        {
            CreateMatch(kills: 10, deaths: 0, assists: 5),
        };

        var stats = MatchStatsCalculator.ComputeMatchStats(matches);

        // (10 + 5) / 1 = 15.0
        Assert.Equal(15.0, stats.AvgKda);
        Assert.Single(stats.TopChampions);
        Assert.Equal("Perfect", stats.TopChampions[0].Kda);
    }

    [Fact]
    public void MatchStatsCalculator_NonLanedQueueIgnoredForPositions()
    {
        var matches = new List<MatchSummary>
        {
            CreateMatch(position: MatchPosition.Top, queueId: 450), // ARAM
        };

        var positions = MatchStatsCalculator.ComputeAggregatedPositions(matches);
        Assert.Null(positions);
    }

    [Fact]
    public void MatchStatsCalculator_AggregatesPositionsAndTopChampionsCorrectly()
    {
        var matches = new List<MatchSummary>
        {
            CreateMatch(championId: 10, position: MatchPosition.Top, win: true),
            CreateMatch(championId: 10, position: MatchPosition.Top, win: true),
            CreateMatch(championId: 20, position: MatchPosition.Middle, win: false),
        };

        var stats = MatchStatsCalculator.ComputeMatchStats(matches);

        Assert.NotNull(stats.Positions);
        Assert.Equal(3, stats.Positions.TotalValid);
        Assert.Equal(MatchPosition.Top, stats.Positions.PrimaryPosition);
        Assert.Equal(67, stats.Positions.PrimaryPercentage); // 2/3 ≈ 67%
        Assert.Equal(MatchPosition.Middle, stats.Positions.SecondaryPosition);
        Assert.Equal(33, stats.Positions.SecondaryPercentage); // 1/3 ≈ 33%

        Assert.Equal(2, stats.TopChampions.Count);
        Assert.Equal(10, stats.TopChampions[0].ChampionId);
        Assert.Equal(2, stats.TopChampions[0].Games);
        Assert.Equal(100, stats.TopChampions[0].WinRate);
    }

    [Fact]
    public void PremadeAnalyzer_SingleMemberGetsMinusOne_MultiMemberGetsColorIdx()
    {
        var team = new List<PremadePlayerLike>
        {
            new() { Puuid = "p1", TeamParticipantId = "100", CellId = 0 },
            new() { Puuid = "p2", TeamParticipantId = "100", CellId = 1 },
            new() { Puuid = "p3", TeamParticipantId = "200", CellId = 2 }, // solo
            new() { Puuid = "p4", TeamParticipantId = "0", CellId = 3 },   // no group
        };

        var colors = PremadeAnalyzer.ComputePremadeColors(team);

        Assert.True(colors.ContainsKey("p1"));
        Assert.Equal(0, colors["p1"]);
        Assert.Equal(0, colors["p2"]);
        Assert.Equal(-1, colors["p3"]);
        Assert.False(colors.ContainsKey("p4"));

        Assert.True(PremadeAnalyzer.HasPremadeGroup(colors));
    }

    [Fact]
    public void PremadeAnalyzer_BuildPremadeGroupsOrdersByColor()
    {
        var team = new List<PremadePlayerLike>
        {
            new() { Puuid = "p1", SummonerId = 101, CellId = 0, ChampionId = 157, DisplayName = "Yasuo" },
            new() { Puuid = "p2", SummonerId = 102, CellId = 1, ChampionId = 64, DisplayName = "LeeSin" },
        };
        var colors = new Dictionary<string, int> { ["p1"] = 0, ["p2"] = 0 };
        var groups = PremadeAnalyzer.BuildPremadeGroups(team, colors, null);

        Assert.Single(groups);
        Assert.Equal(0, groups[0].ColorIdx);
        Assert.Equal(2, groups[0].Members.Count);
        Assert.Equal("Yasuo", groups[0].Members[0].DisplayName);
        Assert.Equal("LeeSin", groups[0].Members[1].DisplayName);
    }

    [Fact]
    public void PlayerIdentity_IsRealSummonerName_FiltersBotsAndPlaceholders()
    {
        Assert.False(PlayerIdentity.IsRealSummonerName("玩家1"));
        Assert.False(PlayerIdentity.IsRealSummonerName("玩家 5"));
        Assert.False(PlayerIdentity.IsRealSummonerName("召唤师10"));
        Assert.False(PlayerIdentity.IsRealSummonerName("Player 3"));
        Assert.False(PlayerIdentity.IsRealSummonerName("黑暗之女 电脑"));
        Assert.False(PlayerIdentity.IsRealSummonerName("Annie Bot"));
        Assert.False(PlayerIdentity.IsRealSummonerName("电脑 1"));
        Assert.False(PlayerIdentity.IsRealSummonerName("game_character_displayname_Ahri"));
        Assert.False(PlayerIdentity.IsRealSummonerName("RealName", isBot: true));

        // 拥有 Riot ID tag 的玩家即使叫提莫也是真实玩家
        Assert.True(PlayerIdentity.IsRealSummonerName("提莫#0001"));
        Assert.True(PlayerIdentity.IsRealSummonerName("Faker#KR1"));
        Assert.True(PlayerIdentity.IsRealSummonerName("NormalPlayer"));
    }

    [Fact]
    public void PlayerIdentity_IsIdentityCompatible_RejectsAnonymousInheritingRealIdentity()
    {
        var realPlayer = new GameInfoPlayer
        {
            Info = new SummonerProfile
            {
                AccountId = 1,
                SummonerId = 1001,
                SummonerLevel = 30,
                PercentCompleteForNextLevel = 0,
                XpSinceLastLevel = 0,
                XpUntilNextLevel = 0,
                Puuid = "real-puuid",
                DisplayName = "RealPlayer",
                GameName = "RealPlayer",
                TagLine = "NA1",
                ProfileIconId = 1,
            },
        };

        // 匿名 incoming（无 puuid，sid=0）不应继承实名数据
        Assert.False(PlayerIdentity.IsIdentityCompatible(realPlayer, incomingPuuid: null, incomingSummonerId: 0, incomingCellId: 0));

        // 相同 puuid 兼容
        Assert.True(PlayerIdentity.IsIdentityCompatible(realPlayer, incomingPuuid: "real-puuid", incomingSummonerId: 1001, incomingCellId: 0));

        // 冲突 puuid 不兼容
        Assert.False(PlayerIdentity.IsIdentityCompatible(realPlayer, incomingPuuid: "other-puuid", incomingSummonerId: 1001, incomingCellId: 0));
    }

    [Fact]
    public void ChampionResolver_PrioritizesInProgressPickOverLocked()
    {
        var player = new PremadePlayerLike
        {
            CellId = 0,
            Puuid = "my-puuid",
            ChampionId = 157, // 旧英雄
        };

        var actions = new List<List<ChampSelectActionLike>>
        {
            new()
            {
                new ChampSelectActionLike(ActorCellId: 0, ChampionId: 64, Completed: false, IsInProgress: true, Type: "pick"),
            },
        };

        var session = new ChampSelectSessionLike(
            Actions: actions,
            MyTeam: [new ChampSelectMemberLike(CellId: 0, ChampionId: 157, Puuid: "my-puuid")]);

        int resolved = ChampionResolver.ResolvePlayerChampionId(player, session);
        Assert.Equal(64, resolved); // 悬停进行中的 64 (盲僧) 优先于 157
    }

    [Fact]
    public void ChampionResolver_RedSideCellIdFiveToNine_NotMisidentifiedAsEnemy()
    {
        // 红方选人时我方真实 cellId 为 5..9
        var player = new PremadePlayerLike
        {
            CellId = 6,
            Puuid = "red-ally-puuid",
            IsEnemy = false,
        };

        var session = new ChampSelectSessionLike(
            Actions:
            [
                [new ChampSelectActionLike(ActorCellId: 6, ChampionId: 81, Completed: true, IsInProgress: false, Type: "pick")]
            ],
            MyTeam: [new ChampSelectMemberLike(CellId: 6, ChampionId: 81, Puuid: "red-ally-puuid")],
            TheirTeam: [new ChampSelectMemberLike(CellId: 1, ChampionId: 22, Puuid: "enemy-puuid")]);

        int resolved = ChampionResolver.ResolvePlayerChampionId(player, session);
        Assert.Equal(81, resolved);
    }

    [Fact]
    public void TeamBuilder_ResolveTeamSide_CorrectlyIdentifiesBlueAndRed()
    {
        var teamOne = new List<PremadePlayerLike>
        {
            new() { Puuid = "p1", SummonerId = 101 },
        };
        var teamTwo = new List<PremadePlayerLike>
        {
            new() { Puuid = "p2", SummonerId = 102 },
        };

        var ctxBlue = new TeamBuildContext
        {
            CurrentSummonerPuuid = "p1",
            CurrentSummonerId = 101,
        };

        var (allyBlue, enemyBlue, sideBlue) = TeamBuilder.ResolveTeamSide(teamOne, teamTwo, ctxBlue);
        Assert.Equal("blue", sideBlue);
        Assert.Equal(teamOne, allyBlue);
        Assert.Equal(teamTwo, enemyBlue);

        var ctxRed = new TeamBuildContext
        {
            CurrentSummonerPuuid = "p2",
            CurrentSummonerId = 102,
        };

        var (allyRed, enemyRed, sideRed) = TeamBuilder.ResolveTeamSide(teamOne, teamTwo, ctxRed);
        Assert.Equal("red", sideRed);
        Assert.Equal(teamTwo, allyRed);
        Assert.Equal(teamOne, enemyRed);
    }

    [Fact]
    public void TeamBuilder_BuildGameflowTeams_AssignsStableCellIdsBasedOnSide()
    {
        var teamA = new List<PremadePlayerLike> { new() { DisplayName = "A" } };
        var teamB = new List<PremadePlayerLike> { new() { DisplayName = "B" } };

        // 我方为红方：我方 offset=5 (5..9)，敌方 offset=0 (0..4)
        var (myRed, theirRed) = TeamBuilder.BuildGameflowTeams(teamA, teamB, "red", new TeamBuildContext());
        Assert.Equal(5, myRed[0].CellId);
        Assert.False(myRed[0].IsEnemy);
        Assert.Equal(0, theirRed[0].CellId);
        Assert.True(theirRed[0].IsEnemy);

        // 我方为蓝方：我方 offset=0 (0..4)，敌方 offset=5 (5..9)
        var (myBlue, theirBlue) = TeamBuilder.BuildGameflowTeams(teamA, teamB, "blue", new TeamBuildContext());
        Assert.Equal(0, myBlue[0].CellId);
        Assert.False(myBlue[0].IsEnemy);
        Assert.Equal(5, theirBlue[0].CellId);
        Assert.True(theirBlue[0].IsEnemy);
    }

    [Theory]
    [InlineData("玩家1", true)]
    [InlineData("召唤师10", true)]
    [InlineData("Player 5", true)]
    [InlineData("未知", true)]
    [InlineData("电脑", true)]
    [InlineData("电脑 1", true)]
    [InlineData("狂暴电脑 bot", true)]
    [InlineData("TheShy", false)]
    [InlineData("提莫#1234", false)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void PlayerIdentity_IsPlaceholderName_DetectsCorrectly(string? name, bool expected)
    {
        Assert.Equal(expected, PlayerIdentity.IsPlaceholderName(name));
    }

    [Fact]
    public void PlayerIdentity_GetSearchName_FormatsCorrectly()
    {
        var player = new PremadePlayerLike
        {
            GameName = "Uzi",
            TagLine = "RNG",
        };

        var searchName = PlayerIdentity.GetSearchName(player, null);
        Assert.Equal("Uzi#RNG", searchName);

        var placeholderPlayer = new PremadePlayerLike
        {
            DisplayName = "玩家1",
        };
        Assert.Equal(string.Empty, PlayerIdentity.GetSearchName(placeholderPlayer, null));

        var alreadyTagged = new PremadePlayerLike
        {
            DisplayName = "Faker#T1",
        };
        Assert.Equal("Faker#T1", PlayerIdentity.GetSearchName(alreadyTagged, null));
    }

    [Fact]
    public void PlayerIdentity_GetDisplayName_StripsTagAndFallsBack()
    {
        var player = new PremadePlayerLike
        {
            GameName = "Faker#KR1",
        };
        Assert.Equal("Faker", PlayerIdentity.GetDisplayName(player, null, 0));

        var empty = new PremadePlayerLike();
        Assert.Equal("玩家3", PlayerIdentity.GetDisplayName(empty, null, 2));
    }
}
