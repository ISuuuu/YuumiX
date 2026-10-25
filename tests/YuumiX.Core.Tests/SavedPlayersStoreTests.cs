using YuumiX.Core.Features.SavedPlayers;
using YuumiX.Core.Features.SavedPlayers.Models;
using Xunit;

namespace YuumiX.Core.Tests;

public sealed class SavedPlayersStoreTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly SavedPlayersStore _store;

    public SavedPlayersStoreTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"yuumix_test_{Guid.NewGuid():N}.db");
        _store = new SavedPlayersStore(_tempDbPath);
    }

    [Fact]
    public async Task EmptyDatabase_ReturnsZeroCount()
    {
        var result = await _store.QueryPlayersAsync();
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SaveTag_UpdatesTagSuccessfully()
    {
        // 初始为空
        var initial = await _store.QueryPlayersAsync();
        Assert.Equal(0, initial.TotalCount);

        // 手动插入一条记录并更新 tag
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_tempDbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO saved_players (puuid, self_puuid, region, rso_platform_id, tag, summoner_name, profile_icon_id, tag_line, champion_id, update_at, last_met_at)
            VALUES ('p1', 'self1', '', '', NULL, '测试玩家', 10, '1234', 157, 1000, 1000);
        ";
        await cmd.ExecuteNonQueryAsync();

        var query = await _store.QueryPlayersAsync();
        Assert.Equal(1, query.TotalCount);
        Assert.Equal("测试玩家", query.Items[0].SummonerName);
        Assert.Null(query.Items[0].Tag);

        // 保存 tag
        await _store.SaveTagAsync("p1", "self1", "绝活亚索");
        var updated = await _store.QueryPlayersAsync();
        Assert.Equal("绝活亚索", updated.Items[0].Tag);

        // 删除
        await _store.DeletePlayerAsync("p1", "self1");
        var deleted = await _store.QueryPlayersAsync();
        Assert.Equal(0, deleted.TotalCount);
    }

    [Fact]
    public async Task RecordEncounters_InsertsAndUpdatesPlayersAndGames()
    {
        var players = new List<EncounterPlayerEntry>
        {
            new() { Puuid = "p1", SummonerName = "队友1", ProfileIconId = 1, TagLine = "111", ChampionId = 157 },
            new() { Puuid = "p2", SummonerName = "对手1", ProfileIconId = 2, TagLine = "222", ChampionId = 238 },
            new() { Puuid = "self1", SummonerName = "自己", ProfileIconId = 3, TagLine = "000", ChampionId = 1 }, // 应该被跳过
        };

        // 第一次记录
        var count1 = await _store.RecordEncountersAsync(players, "self1", 10001, "420");
        Assert.Equal(2, count1);

        var query1 = await _store.QueryPlayersAsync("self1");
        Assert.Equal(2, query1.TotalCount);
        Assert.All(query1.Items, p => Assert.Equal(1, p.EncounterCount));

        // 给 p1 加个 tag
        await _store.SaveTagAsync("p1", "self1", "高手");

        // 第二次相遇，p1 更换英雄，tag 应保留
        var players2 = new List<EncounterPlayerEntry>
        {
            new() { Puuid = "p1", SummonerName = "队友1_新名", ProfileIconId = 10, TagLine = "111", ChampionId = 99 },
        };
        var count2 = await _store.RecordEncountersAsync(players2, "self1", 10002, "450");
        Assert.Equal(1, count2);

        var query2 = await _store.QueryPlayersAsync("self1");
        var p1 = query2.Items.First(p => p.Puuid == "p1");
        Assert.Equal("队友1_新名", p1.SummonerName);
        Assert.Equal(99, p1.ChampionId);
        Assert.Equal("高手", p1.Tag); // tag 保持不变
        Assert.Equal(2, p1.EncounterCount); // 相遇次数变为 2
        Assert.Equal("450", p1.LastQueueType);

        // 检查相遇对局记录
        var games = await _store.QueryEncounteredGamesAsync("p1", "self1");
        Assert.Equal(2, games.TotalCount);
        Assert.Equal(10002, games.Items[0].GameId);
        Assert.Equal(10001, games.Items[1].GameId);
    }

    [Fact]
    public async Task RecordEncounters_SameGameRecordedTwice_KeepsSingleEncounter()
    {
        var players = new List<EncounterPlayerEntry>
        {
            new() { Puuid = "p1", SummonerName = "队友1", ProfileIconId = 1, TagLine = "111", ChampionId = 157 },
        };

        var first = await _store.RecordEncountersAsync(players, "self1", 20001, "420");
        // 断线重连会让同一 gameId 走两遍记录流程，第二次不得再落一行
        var second = await _store.RecordEncountersAsync(players, "self1", 20001, "420");

        Assert.Equal(1, first);
        Assert.Equal(0, second);

        var query = await _store.QueryPlayersAsync("self1");
        Assert.Equal(1, query.Items[0].EncounterCount);

        var games = await _store.QueryEncounteredGamesAsync("p1", "self1");
        Assert.Equal(1, games.TotalCount);
    }

    [Fact]
    public async Task QueryPlayers_SearchWithNameAndTagLineCombined()
    {
        var players = new List<EncounterPlayerEntry>
        {
            new() { Puuid = "p1", SummonerName = "张三", ProfileIconId = 1, TagLine = "CN1", ChampionId = 1 },
            new() { Puuid = "p2", SummonerName = "李四", ProfileIconId = 2, TagLine = "CN2", ChampionId = 2 },
        };
        await _store.RecordEncountersAsync(players, "self1", 10001, "420");

        // 纯名字检索
        var r1 = await _store.QueryPlayersAsync(search: "张三");
        Assert.Equal(1, r1.TotalCount);

        // 纯 TagLine 检索
        var r2 = await _store.QueryPlayersAsync(search: "CN2");
        Assert.Equal(1, r2.TotalCount);

        // 组合检索 张三#CN1
        var r3 = await _store.QueryPlayersAsync(search: "张三#CN1");
        Assert.Equal(1, r3.TotalCount);
        Assert.Equal("p1", r3.Items[0].Puuid);
    }

    [Fact]
    public async Task ExportAndImportTaggedPlayers_Roundtrip()
    {
        var players = new List<EncounterPlayerEntry>
        {
            new() { Puuid = "p1", SummonerName = "张三", ProfileIconId = 1, TagLine = "CN1", ChampionId = 1 },
            new() { Puuid = "p2", SummonerName = "李四", ProfileIconId = 2, TagLine = "CN2", ChampionId = 2 },
        };
        await _store.RecordEncountersAsync(players, "self1", 10001, "420");

        // 只有 p1 带 tag
        await _store.SaveTagAsync("p1", "self1", "大腿");

        // 导出
        var json = await _store.ExportTaggedPlayersJsonAsync("self1");
        Assert.Contains("大腿", json);
        Assert.Contains("张三", json);
        Assert.DoesNotContain("李四", json);

        // 清空数据库并导入
        await _store.DeletePlayerAsync("p1", "self1");
        await _store.DeletePlayerAsync("p2", "self1");
        Assert.Equal(0, (await _store.QueryPlayersAsync("self1")).TotalCount);

        // 从 json 导入
        var imported = await _store.ImportTaggedPlayersJsonAsync(json, "self1");
        Assert.Equal(1, imported);

        var query = await _store.QueryPlayersAsync("self1");
        Assert.Equal(1, query.TotalCount);
        Assert.Equal("大腿", query.Items[0].Tag);
        Assert.Equal("张三", query.Items[0].SummonerName);
    }

    [Fact]
    public async Task BackfillIdentities_ScansAndUpdatesCorrectly()
    {
        // 确保初始化
        await _store.QueryPlayersAsync();

        // 插入一条缺少 tagLine 且 icon 为 0 的记录
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_tempDbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO saved_players (puuid, self_puuid, region, rso_platform_id, tag, summoner_name, profile_icon_id, tag_line, champion_id, update_at, last_met_at)
            VALUES ('p_need_backfill', 'self1', '', '', NULL, '老名字', 0, NULL, 157, 1000, 1000);
        ";
        await cmd.ExecuteNonQueryAsync();

        var pending = await _store.GetPendingBackfillPlayersAsync();
        Assert.Single(pending);
        Assert.Equal("p_need_backfill", pending[0].Puuid);

        // 回填
        var updates = new[]
        {
            ("p_need_backfill", "self1", "9999", "新召唤师名", 501),
        };
        var updatedCount = await _store.SaveBackfilledIdentitiesAsync(updates);
        Assert.Equal(1, updatedCount);

        // 再次扫描，应为空
        var pendingAfter = await _store.GetPendingBackfillPlayersAsync();
        Assert.Empty(pendingAfter);

        var check = await _store.QueryPlayersAsync("self1");
        var item = check.Items.First(p => p.Puuid == "p_need_backfill");
        Assert.Equal("9999", item.TagLine);
        Assert.Equal(501, item.ProfileIconId);
    }

    [Fact]
    public async Task GetLatestSelfPuuid_MultiAccount_ReturnsMostRecentActiveSelf()
    {
        // 初始为空库
        var emptyLatest = await _store.GetLatestSelfPuuidAsync();
        Assert.Null(emptyLatest);

        // 账号 self_A 在 1000 时间记录相遇
        var playersA = new List<EncounterPlayerEntry>
        {
            new() { Puuid = "pA", SummonerName = "玩家A", ProfileIconId = 1, TagLine = "A", ChampionId = 1 },
        };
        await _store.RecordEncountersAsync(playersA, "self_A", 1001, "420");

        // 此时最新应为 self_A
        var latestAfterA = await _store.GetLatestSelfPuuidAsync();
        Assert.Equal("self_A", latestAfterA);

        // 账号 self_B 在之后记录相遇
        var playersB = new List<EncounterPlayerEntry>
        {
            new() { Puuid = "pB", SummonerName = "玩家B", ProfileIconId = 2, TagLine = "B", ChampionId = 2 },
        };
        await _store.RecordEncountersAsync(playersB, "self_B", 1002, "420");

        // 此时最新应为 self_B
        var latestAfterB = await _store.GetLatestSelfPuuidAsync();
        Assert.Equal("self_B", latestAfterB);

        // 各自查询时不混杂
        var queryA = await _store.QueryPlayersAsync("self_A");
        Assert.Equal(1, queryA.TotalCount);
        Assert.Equal("玩家A", queryA.Items[0].SummonerName);

        var queryB = await _store.QueryPlayersAsync("self_B");
        Assert.Equal(1, queryB.TotalCount);
        Assert.Equal("玩家B", queryB.Items[0].SummonerName);

        // 检查所有账号列表
        var allSelfs = await _store.GetAllSelfPuuidsAsync();
        Assert.Equal(2, allSelfs.Count);
        Assert.Equal("self_B", allSelfs[0]); // 最近活跃排在第一位
        Assert.Equal("self_A", allSelfs[1]);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            if (File.Exists(_tempDbPath))
            {
                File.Delete(_tempDbPath);
            }
        }
        catch
        {
            // 忽略文件占用
        }
    }
}
