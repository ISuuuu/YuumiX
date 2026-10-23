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
