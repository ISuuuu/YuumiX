using Xunit;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Tests;

public sealed class SearchHistoryStoreTests : IDisposable
{
    private readonly string _tempFile;

    public SearchHistoryStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"yuumi_test_history_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            try { File.Delete(_tempFile); } catch { }
        }
    }

    [Fact]
    public async Task AddAsync_PrependsAndDeduplicates()
    {
        var store = new SearchHistoryStore(_tempFile);

        await store.AddAsync("PlayerA#123");
        await store.AddAsync("PlayerB#456");
        await store.AddAsync("playera#123"); // case-insensitive deduplication, should move to front

        var list = await store.GetHistoryAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal("playera#123", list[0]);
        Assert.Equal("PlayerB#456", list[1]);
    }

    [Fact]
    public async Task RemoveAsync_RemovesItem()
    {
        var store = new SearchHistoryStore(_tempFile);

        await store.AddAsync("PlayerA#123");
        await store.AddAsync("PlayerB#456");
        await store.RemoveAsync("PlayerA#123");

        var list = await store.GetHistoryAsync();

        Assert.Single(list);
        Assert.Equal("PlayerB#456", list[0]);
    }

    [Fact]
    public async Task MaxCount_LimitsTo20Items()
    {
        var store = new SearchHistoryStore(_tempFile);

        for (int i = 0; i < 25; i++)
        {
            await store.AddAsync($"Player{i}#1");
        }

        var list = await store.GetHistoryAsync();

        Assert.Equal(SearchHistoryStore.MaxHistoryCount, list.Count);
        Assert.Equal("Player24#1", list[0]);
    }
}
