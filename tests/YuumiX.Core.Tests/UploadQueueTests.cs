using System.Text.Json;
using YuumiX.Core.Persistence;
using YuumiX.Core.Upload;
using Xunit;

namespace YuumiX.Core.Tests;

/// <summary>
/// 上传队列与本地暂存的验收（对应旧 upload.rs 的 pending_uploads 与 UploadQueue）。
///
/// 只测不依赖网络的部分：暂存表的落盘/累加/擦除/上限，批量 URL 推导，
/// 以及重试上限守卫——这几处错了的表现是"战绩悄悄丢掉"，线上很难发现。
/// </summary>
public sealed class UploadQueueTests
{
    // ── 批量上传 URL 推导 ────────────────────────────────────────

    [Theory]
    [InlineData("https://example.com", "https://example.com/api/lol/upload-batch")]
    [InlineData("https://example.com/", "https://example.com/api/lol/upload-batch")]
    [InlineData("https://example.com/api/lol/upload", "https://example.com/api/lol/upload-batch")]
    [InlineData("https://example.com/api/lol/upload/", "https://example.com/api/lol/upload-batch")]
    [InlineData("example.com", "http://example.com/api/lol/upload-batch")]
    public void BuildBatchUploadUrl_FollowsOldRules(string input, string expected)
    {
        Assert.Equal(expected, UploadUrlBuilder.BuildBatchUploadUrl(input));
    }

    // ── 暂存表 ──────────────────────────────────────────────────

    [Fact]
    public void PendingStore_AddsUpdatesAndRemoves()
    {
        using var store = new PendingUploadStore(NextDatabasePath());

        store.AddOrUpdate(Payload(1001), "第一次失败");

        PendingUploadItem only = Assert.Single(store.Load());
        Assert.Equal(1001, only.GameId);
        Assert.Equal(1, only.RetryCount);
        Assert.Equal("第一次失败", only.LastError);
        Assert.Equal(1001, only.Payload.MatchInfo.MatchId);

        // 同一局再次失败：次数累加而不是插第二行（旧 add_or_update_pending_upload）
        store.AddOrUpdate(Payload(1001), "第二次失败");
        PendingUploadItem again = Assert.Single(store.Load());
        Assert.Equal(2, again.RetryCount);
        Assert.Equal("第二次失败", again.LastError);

        store.Remove(1001);
        Assert.Empty(store.Load());
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void PendingStore_KeepsOnlyNewestRetentionLimit()
    {
        using var store = new PendingUploadStore(NextDatabasePath());

        for (long id = 1; id <= PendingUploadStore.RetentionLimit + 5; id++)
        {
            store.AddOrUpdate(Payload(id), "失败");
        }

        Assert.Equal(PendingUploadStore.RetentionLimit, store.Count());
        Assert.DoesNotContain(store.Load(), item => item.GameId == 1);
    }

    [Fact]
    public void PendingStore_RoundTripsPayloadWithCamelCaseKeys()
    {
        using var store = new PendingUploadStore(NextDatabasePath());

        store.AddOrUpdate(Payload(2002), "失败");
        PendingUploadItem item = Assert.Single(store.Load());

        // 落盘用的序列化选项必须与 PostAsJsonAsync 一致，否则重发时字段名会漂移
        string json = JsonSerializer.Serialize(item.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"matchId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"gameMode\"", json, StringComparison.Ordinal);
    }

    // ── 重试上限守卫 ────────────────────────────────────────────

    [Fact]
    public void TriggerPendingRetry_SkipsRecordsOverRetryLimit()
    {
        using var store = new PendingUploadStore(NextDatabasePath());
        UploadPayload payload = Payload(3003);

        // 累到上限：服务端永久拒绝的死信不该每局结束都空传一次
        for (int i = 0; i < PendingUploadStore.MaxRetryCount; i++)
        {
            store.AddOrUpdate(payload, "HTTP 400");
        }

        Assert.Equal(PendingUploadStore.MaxRetryCount, Assert.Single(store.Load()).RetryCount);

        var config = new AppConfig();
        var uploadService = new UploadService(() => null, () => config);
        using var queue = new UploadQueueService(
            uploadService,
            store,
            () => config,
            () => Task.FromResult<string?>(null));

        Assert.Equal(0, queue.TriggerPendingRetry());
    }

    private static UploadPayload Payload(long gameId) => new(
        new MatchInfo(
            gameId,
            "CLASSIC",
            "MATCHED_GAME",
            420,
            "2026-10-24T22:00:00",
            1800,
            "16.1.1",
            []),
        []);

    private static string NextDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"yuumix-upload-{Guid.NewGuid():N}.db");
}
