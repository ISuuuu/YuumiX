using Xunit;
using YuumiX.Core.Assets;

namespace YuumiX.Core.Tests;

/// <summary>
/// 磁盘缓存：命名、TTL、原子写入、旧格式兼容。
/// 全部在临时目录上跑，不触碰真实的 <c>%APPDATA%/Yuumi</c>。
/// </summary>
public sealed class AssetCacheTests
{
    private const string AssetPath = "/lol-game-data/assets/v1/champion-icons/54.png";

    /// <summary>
    /// 哈希值必须与 Rust 的 <c>stable_hash</c> 一致，否则两版共用的缓存目录会各写一份，
    /// 迁移期来回切换就退化成「每次重新下载全站图标」。
    /// 期望值由独立实现（Python）算出，不是从被测代码反推。
    /// </summary>
    [Fact]
    public void Hash_MatchesRustStableHash()
    {
        Assert.Equal("321e773076628ac7", AssetCache.Hash(AssetPath));
        Assert.Equal("f4edbd47948b7d16", AssetCache.Hash("/fe/lol-loot/assets/loot_item.png"));
    }

    [Fact]
    public void Store_WritesFileWithContentTypeExtension()
    {
        using var directory = new TempDirectory();
        var cache = new AssetCache(directory.Path);

        cache.Store(AssetPath, "payload"u8, "image/png");

        var path = cache.TryGetPath(AssetPath);
        Assert.NotNull(path);
        Assert.EndsWith(".png", path, StringComparison.Ordinal);
        Assert.Equal("payload", File.ReadAllText(path));
    }

    [Fact]
    public void Store_UsesJpegExtensionForJpeg()
    {
        using var directory = new TempDirectory();
        var cache = new AssetCache(directory.Path);

        cache.Store(AssetPath, "jpg"u8, "image/jpeg");

        Assert.EndsWith(".jpg", cache.TryGetPath(AssetPath), StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetPath_Missing_ReturnsNull()
    {
        using var directory = new TempDirectory();
        var cache = new AssetCache(directory.Path);

        Assert.Null(cache.TryGetPath(AssetPath));
    }

    /// <summary>过期条目必须被物理删除——只返回 null 会让目录无限膨胀。</summary>
    [Fact]
    public void TryGetPath_ExpiredEntryIsDeleted()
    {
        using var directory = new TempDirectory();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cache = new AssetCache(directory.Path, clock);

        cache.Store(AssetPath, "old"u8, "image/png");
        var path = cache.TryGetPath(AssetPath);
        Assert.NotNull(path);

        clock.Advance(AssetCache.Ttl + TimeSpan.FromMinutes(1));

        Assert.Null(cache.TryGetPath(AssetPath));
        Assert.False(File.Exists(path));
    }

    /// <summary>旧版本写入的是裸哈希文件名，共用缓存目录时必须能读到。</summary>
    [Fact]
    public void TryGetPath_ReadsLegacyFileWithoutExtension()
    {
        using var directory = new TempDirectory();
        Directory.CreateDirectory(directory.Path);

        var legacyPath = Path.Combine(directory.Path, AssetCache.Hash(AssetPath));
        File.WriteAllText(legacyPath, "legacy");

        var cache = new AssetCache(directory.Path);

        Assert.Equal(legacyPath, cache.TryGetPath(AssetPath));
    }

    /// <summary>新旧两种形态并存时，优先取带扩展名的（本工程的写入形态）。</summary>
    [Fact]
    public void TryGetPath_PrefersFileWithExtension()
    {
        using var directory = new TempDirectory();
        var cache = new AssetCache(directory.Path);

        cache.Store(AssetPath, "new"u8, "image/png");
        File.WriteAllText(Path.Combine(directory.Path, AssetCache.Hash(AssetPath)), "legacy");

        var path = cache.TryGetPath(AssetPath);

        Assert.EndsWith(".png", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Store_OverwritesExistingEntry()
    {
        using var directory = new TempDirectory();
        var cache = new AssetCache(directory.Path);

        cache.Store(AssetPath, "first"u8, "image/png");
        cache.Store(AssetPath, "second"u8, "image/png");

        var path = cache.TryGetPath(AssetPath);
        Assert.Equal("second", File.ReadAllText(path!));
    }

    [Fact]
    public void Store_DoesNotLeaveTemporaryFiles()
    {
        using var directory = new TempDirectory();
        var cache = new AssetCache(directory.Path);

        cache.Store(AssetPath, "payload"u8, "image/png");

        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void Clear_RemovesEntriesButKeepsDirectory()
    {
        using var directory = new TempDirectory();
        var cache = new AssetCache(directory.Path);

        cache.Store(AssetPath, "a"u8, "image/png");
        cache.Store("/lol-game-data/assets/v1/profile-icons/29.jpg", "b"u8, "image/jpeg");

        cache.Clear();

        Assert.True(Directory.Exists(directory.Path));
        Assert.Empty(Directory.GetFiles(directory.Path));
        Assert.Null(cache.TryGetPath(AssetPath));
    }

    /// <summary>设置页的清除反馈依赖返回的统计数：2 个文件 → 2，空目录 → 0。</summary>
    [Fact]
    public void Clear_ReturnsRemovedFileCount()
    {
        using var directory = new TempDirectory();
        var cache = new AssetCache(directory.Path);

        Assert.Equal(0, cache.Clear());

        cache.Store(AssetPath, "a"u8, "image/png");
        cache.Store("/lol-game-data/assets/v1/profile-icons/29.jpg", "b"u8, "image/jpeg");

        Assert.Equal(2, cache.Clear());
        Assert.Equal(0, cache.Clear());
    }

    [Fact]
    public void Constructor_RejectsEmptyDirectory()
    {
        Assert.Throws<ArgumentException>(() => new AssetCache(" "));
        Assert.Throws<ArgumentException>(() => new AssetCache(string.Empty));
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
