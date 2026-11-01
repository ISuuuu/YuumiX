using Xunit;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.Core.Tests;

/// <summary>
/// 保留对局快照磁盘存取：round-trip 保真、损坏/缺失/版本不识别降级为无快照、清空删文件。
/// </summary>
public sealed class GameInfoSnapshotStoreTests
{
    [Fact]
    public void MissingFile_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var store = new GameInfoSnapshotStore(temp.File("gameinfo_snapshot.json"));

        Assert.Null(store.Read());
    }

    [Fact]
    public void CorruptFile_ReturnsNull()
    {
        using var temp = new TempDirectory();
        string path = temp.File("gameinfo_snapshot.json");
        File.WriteAllText(path, "{ 这不是合法 JSON ");
        var store = new GameInfoSnapshotStore(path);

        Assert.Null(store.Read());
    }

    [Fact]
    public void FutureSchemaVersion_ReturnsNull()
    {
        using var temp = new TempDirectory();
        string path = temp.File("gameinfo_snapshot.json");
        File.WriteAllText(path, """{"Version":999,"SavedAtMs":0,"Snapshot":{"GameId":1}}""");
        var store = new GameInfoSnapshotStore(path);

        Assert.Null(store.Read());
    }

    [Fact]
    public void WriteThenRead_PreservesKeyFields()
    {
        using var temp = new TempDirectory();
        string path = temp.File("gameinfo_snapshot.json");
        var store = new GameInfoSnapshotStore(path);
        var sample = BuildSample();

        store.Write(sample);
        var restored = store.Read();

        Assert.NotNull(restored);
        Assert.Equal(sample.GameId, restored!.GameId);
        Assert.Equal(sample.TeamCount, restored.TeamCount);
        Assert.Equal(sample.LoadedCount, restored.LoadedCount);

        // PremadePlayerLike 仅基元字段，记录值相等可整体比对
        Assert.Equal(sample.MyTeam, restored.MyTeam);
        Assert.Equal(sample.TheirTeam, restored.TheirTeam);
        Assert.Equal(sample.PremadeColorsMy, restored.PremadeColorsMy);
        Assert.Equal(sample.PremadeColorsTheir, restored.PremadeColorsTheir);

        var expectedMe = sample.PlayerData["p-me"];
        var actualMe = restored.PlayerData["p-me"];
        Assert.Equal(expectedMe.Info, actualMe.Info);
        Assert.Equal(expectedMe.ChampionId, actualMe.ChampionId);
        Assert.Equal(expectedMe.WinRate, actualMe.WinRate);
        Assert.Equal(expectedMe.AvgKda, actualMe.AvgKda);
        Assert.Equal(expectedMe.WinCount, actualMe.WinCount);
        Assert.Equal(expectedMe.LossesCount, actualMe.LossesCount);
        Assert.Equal(expectedMe.Matches.Count, actualMe.Matches.Count);
        Assert.Equal(expectedMe.Matches[0].GameId, actualMe.Matches[0].GameId);
        Assert.Equal(expectedMe.Matches[0].Position, actualMe.Matches[0].Position);
        Assert.Equal(expectedMe.Solo!.Tier, actualMe.Solo!.Tier);
        Assert.Equal(expectedMe.Solo.LeaguePoints, actualMe.Solo.LeaguePoints);
        Assert.Equal(expectedMe.Masteries![0].ChampionPoints, actualMe.Masteries![0].ChampionPoints);
        Assert.Equal(expectedMe.Streak, actualMe.Streak);
        Assert.Equal(
            expectedMe.Positions!.Counts[MatchPosition.Jungle],
            actualMe.Positions!.Counts[MatchPosition.Jungle]);
        Assert.Equal(expectedMe.Positions.PrimaryPosition, actualMe.Positions.PrimaryPosition);
        Assert.Equal(expectedMe.TopChampions![0].ChampionId, actualMe.TopChampions![0].ChampionId);
        Assert.True(actualMe.MasteryFetched);
        Assert.False(actualMe.Loading);
    }

    [Fact]
    public void WriteNull_DeletesExistingFile()
    {
        using var temp = new TempDirectory();
        string path = temp.File("gameinfo_snapshot.json");
        var store = new GameInfoSnapshotStore(path);

        store.Write(BuildSample());
        Assert.True(File.Exists(path));

        store.Write(null);
        Assert.False(File.Exists(path), "Write(null) 应删除磁盘快照（对应 TFT / 全新对局清空）");
    }

    private static ReservedSnapshot BuildSample()
    {
        var profile = new SummonerProfile
        {
            AccountId = 1,
            DisplayName = "展示名",
            GameName = "召唤师A",
            TagLine = "CN1",
            Puuid = "p-me",
            SummonerId = 100,
            SummonerLevel = 300,
            ProfileIconId = 29,
            PercentCompleteForNextLevel = 0,
            XpSinceLastLevel = 0,
            XpUntilNextLevel = 0,
        };

        var match = new MatchSummary
        {
            GameId = 9001,
            QueueId = 420,
            QueueName = "排位单双排",
            MapName = "召唤师峡谷",
            GameCreation = 1_700_000_000_000,
            DurationSeconds = 1800,
            Win = true,
            Remake = false,
            ChampionId = 64,
            Spell1Id = 4,
            Spell2Id = 14,
            ChampionLevel = 18,
            Kills = 5,
            Deaths = 2,
            Assists = 8,
            ItemIds = [1, 2, 3, 4, 5, 6, 7],
            RuneId = 100,
            CreepScore = 200,
            Gold = 15000,
            DamageToChampions = 25000,
            TotalHeal = 100,
            AugmentIds = [],
            Position = MatchPosition.Jungle,
        };

        var player = new GameInfoPlayer
        {
            Info = profile,
            Matches = [match],
            Solo = new RankedQueueEntry
            {
                QueueType = "RANKED_SOLO_5x5",
                Tier = "GOLD",
                Division = "II",
                LeaguePoints = 37,
                Wins = 30,
                Losses = 20,
            },
            Loading = false,
            ChampionId = 64,
            WinRate = 60.0,
            AvgKda = 2.5,
            WinCount = 6,
            LossesCount = 4,
            Masteries = [new PlayerChampionMastery(64, 7, 123456, "S+")],
            MasteryFetched = true,
            Streak = new StreakInfo("win", 3),
            Positions = new AggregatedPositions(
                new Dictionary<MatchPosition, int> { [MatchPosition.Jungle] = 6, [MatchPosition.Middle] = 4 },
                new Dictionary<MatchPosition, int> { [MatchPosition.Jungle] = 60, [MatchPosition.Middle] = 40 },
                10,
                MatchPosition.Jungle,
                60,
                MatchPosition.Middle,
                40,
                [new PositionCountEntry(MatchPosition.Jungle, "打野", 6, 60, PremadeColors.GetColor(0).Border)]),
            TopChampions = [new AggregatedChampion(
                64, "盲僧", 6, 4, 2, 66, 30, 15, 40, "3.0",
                MatchPosition.Jungle,
                new Dictionary<MatchPosition, int> { [MatchPosition.Jungle] = 6 })],
        };

        return new ReservedSnapshot
        {
            MyTeam =
            [
                new PremadePlayerLike { SummonerId = 100, CellId = 0, Puuid = "p-me", GameName = "召唤师A", TagLine = "CN1", ChampionId = 64, TeamParticipantId = "1", PartyId = "uuid-1" },
                new PremadePlayerLike { SummonerId = 101, CellId = 1, Puuid = "p-a", GameName = "召唤师B", TagLine = "CN1", ChampionId = 22 },
            ],
            TheirTeam =
            [
                new PremadePlayerLike { SummonerId = 201, CellId = 5, Puuid = "p-e", GameName = "召唤师E", IsEnemy = true, ChampionId = 1 },
            ],
            PlayerData = new Dictionary<string, GameInfoPlayer>
            {
                ["p-me"] = player,
            },
            PremadeColorsMy = new Dictionary<string, int> { ["p-me"] = 0 },
            PremadeColorsTheir = new Dictionary<string, int> { ["p-e"] = 1 },
            GameId = 555,
            TeamCount = 10,
            LoadedCount = 10,
        };
    }
}
