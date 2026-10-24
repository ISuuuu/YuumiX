using Xunit;
using YuumiX.Core.Features.Opgg;
using YuumiX.Core.Persistence;
using YuumiX.Core.Runtime;

namespace YuumiX.Core.Tests;

public sealed class OpggCacheTests : IDisposable
{
    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private readonly string _tempDir;
    private readonly RuntimePaths _paths;
    private readonly FakeClock _timeProvider;

    public OpggCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "YuumiX_OpggCacheTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = RuntimePaths.Detect(executableDirectory: _tempDir, roamingAppData: _tempDir);
        _timeProvider = new FakeClock(DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp dir cleanup errors
        }
    }

    [Fact]
    public void Fnv1a64_MatchesRustAlgorithm()
    {
        // 验证空串初始哈希
        Assert.Equal(0xcbf29ce484222325UL, OpggHttpClient.ComputeFnv1a64(""));

        // 验证标准文件名格式: 16位小写十六进制 + .json
        var fileName = OpggHttpClient.GetCacheFileName("kr_ranked_emerald_plus_null_MID");
        Assert.EndsWith(".json", fileName);
        Assert.Equal(21, fileName.Length); // 16 hex + 5 for ".json"
    }

    [Fact]
    public async Task MemoryCache_Hits_And_ExpiresAfter10Minutes()
    {
        var cfg = new AppConfig();
        using var client = new OpggHttpClient(() => cfg, _paths, timeProvider: _timeProvider);

        var key = "test_cache_key";
        var payload = """{"hello":"world"}""";

        client.PutCached(key, payload);

        // 立即读取：命中内存缓存
        var hit = await client.GetCachedAsync(key);
        Assert.Equal(payload, hit);

        // 推进时间 9 分钟：依然命中
        _timeProvider.Advance(TimeSpan.FromMinutes(9));
        var hit2 = await client.GetCachedAsync(key);
        Assert.Equal(payload, hit2);

        // 再推进 2 分钟（总计 11 分钟 > 10 分钟 TTL）：内存过期
        // 但由于同时写入了磁盘缓存，此处会从磁盘缓存读取并回填内存
        // 先删除磁盘缓存以单纯验证内存 TTL 过期
        client.ClearDiskCache();

        _timeProvider.Advance(TimeSpan.FromMinutes(2));
        var expired = await client.GetCachedAsync(key);
        Assert.Null(expired);
    }

    [Fact]
    public async Task MemoryCache_EvictsOldest_WhenExceeding100Entries()
    {
        var cfg = new AppConfig();
        using var client = new OpggHttpClient(() => cfg, _paths, timeProvider: _timeProvider);

        // 连续插入 100 条
        for (int i = 0; i < 100; i++)
        {
            _timeProvider.Advance(TimeSpan.FromSeconds(1));
            client.PutCached($"key_{i}", $"data_{i}");
        }

        // 插入第 101 条
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        client.PutCached("key_100", "data_100");

        // key_0 作为最老条目应被从内存淘汰
        client.ClearDiskCache(); // 排除磁盘干扰
        var key0 = await client.GetCachedAsync("key_0");
        var key100 = await client.GetCachedAsync("key_100");

        Assert.Null(key0);
        Assert.Equal("data_100", key100);
    }

    [Fact]
    public async Task DiskCache_BackfillsMemory_And_DeletesWhenExpired()
    {
        var cfg = new AppConfig();
        using var client = new OpggHttpClient(() => cfg, _paths, timeProvider: _timeProvider);

        var key = "disk_test_key";
        var payload = """{"disk":"data"}""";

        client.PutCached(key, payload);

        // 确保写磁盘已完成
        var fileName = OpggHttpClient.GetCacheFileName(key);
        var filePath = Path.Combine(client.DiskCacheDirectory, fileName);
        for (int i = 0; i < 20 && !File.Exists(filePath); i++)
        {
            await Task.Delay(50);
        }
        Assert.True(File.Exists(filePath));

        // 清理内存缓存
        client.ClearMemoryCache();

        // 从磁盘读取并回填内存
        var fromDisk = await client.GetCachedAsync(key);
        Assert.Equal(payload, fromDisk);

        // 修改文件时间为 25 小时前（过期）
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddHours(-25));
        client.ClearMemoryCache();

        // 再次读取：发现已过期，物理删除文件并返回 null
        var expired = await client.GetCachedAsync(key);
        Assert.Null(expired);
        Assert.False(File.Exists(filePath));
    }
}
