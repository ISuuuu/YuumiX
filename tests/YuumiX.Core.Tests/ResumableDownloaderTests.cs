using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;
using YuumiX.Core.Persistence;
using YuumiX.Core.Runtime;
using YuumiX.Core.Updates;

namespace YuumiX.Core.Tests;

/// <summary>
/// 更新包断点续传的验收。
///
/// 核心不变量（任何一条被破坏都会在用户侧表现为「更新后打不开」或「进度白下」）：
/// <list type="number">
///   <item>续传请求必须带 <c>Range</c> 与 <c>If-Range</c>，且只接受起点吻合的 206；</item>
///   <item>服务端忽略 Range（返回 200）时必须截断重写，绝不把新包接在旧字节后面；</item>
///   <item>中断后残包必须保留，完整后残包与元数据必须消失；</item>
///   <item>残包长度进入过期清理判定，新版本的断点不能被清理掉。</item>
/// </list>
/// </summary>
public sealed class ResumableDownloaderTests
{
    private const string Url = "https://github.com/ISuuuu/YuumiX/releases/download/v1.0.0/YuumiX_portable.zip";

    private static readonly byte[] Payload = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

    // ── 基本下载 ────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_WritesFileAndClearsArtifacts()
    {
        using var dir = new TempDirectory();
        string target = dir.File("YuumiX-1.0.0.zip");
        var handler = new StubHandler((_, _, _) => Task.FromResult(Full(Payload)));
        var progress = new CollectingProgress();

        await new ResumableDownloader(new HttpClient(handler), logger: null, maxAttempts: 1, retryDelay: TimeSpan.Zero)
            .DownloadAsync(Url, target, progress);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(ResumableDownloader.GetPartPath(target)));
        Assert.False(File.Exists(ResumableDownloader.GetMetaPath(target)));

        // 首次下载本就不该带 Range
        Assert.Null(handler.Requests[0].Range);
        Assert.Equal(Payload.Length, progress.Last?.Downloaded);
        Assert.Equal(100.0, progress.Last?.Percent);
    }

    // ── 断点续传 ────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_ResumesFromExistingPart()
    {
        using var dir = new TempDirectory();
        string target = dir.File("YuumiX-1.0.0.zip");
        WritePartial(target, Payload[..6], etag: "\"v1\"", total: Payload.Length);

        var handler = new StubHandler((_, _, _) => Task.FromResult(
            Partial(Payload[6..], from: 6, total: Payload.Length, etag: "\"v1\"")));
        var progress = new CollectingProgress();

        await new ResumableDownloader(new HttpClient(handler), logger: null, maxAttempts: 1, retryDelay: TimeSpan.Zero)
            .DownloadAsync(Url, target, progress);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(target));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(6, request.Range?.Ranges.Single().From);
        Assert.Equal("\"v1\"", request.IfRange);

        // 进度按绝对字节数上报：续传时起点不为 0，否则界面会从 0% 重新开始
        Assert.Equal((long)Payload.Length, progress.Last?.Downloaded);
        Assert.False(File.Exists(ResumableDownloader.GetMetaPath(target)));
    }

    [Fact]
    public async Task DownloadAsync_RestartsWhenServerIgnoresRange()
    {
        using var dir = new TempDirectory();
        string target = dir.File("YuumiX-1.0.0.zip");
        // 残包内容与真实包不同：一旦实现错误地做了「追加」，断言就会失败
        WritePartial(target, [0xEE, 0xEE, 0xEE, 0xEE], etag: "\"old\"", total: Payload.Length);

        // 200 全量：等同于服务端判定 If-Range 校验失败（资产已变）
        var handler = new StubHandler((_, _, _) => Task.FromResult(Full(Payload, etag: "\"new\"")));

        await new ResumableDownloader(new HttpClient(handler), logger: null, maxAttempts: 1, retryDelay: TimeSpan.Zero)
            .DownloadAsync(Url, target);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(target));
        Assert.Equal(4, Assert.Single(handler.Requests).Range?.Ranges.Single().From);
    }

    [Fact]
    public async Task DownloadAsync_RetriesByResumingAfterTruncatedTransfer()
    {
        using var dir = new TempDirectory();
        string target = dir.File("YuumiX-1.0.0.zip");

        // 第一次：声明 16 字节却只给了 6 字节（连接被掐断的典型形态）
        // 第二次：必须从 6 续传，补上剩余 10 字节
        var handler = new StubHandler((request, call, _) =>
        {
            if (call == 1)
            {
                return Task.FromResult(Truncated(Payload[..6], declaredLength: Payload.Length, etag: "\"v1\""));
            }

            Assert.Equal(6, request.Headers.Range?.Ranges.Single().From);
            return Task.FromResult(Partial(Payload[6..], from: 6, total: Payload.Length, etag: "\"v1\""));
        });

        await new ResumableDownloader(new HttpClient(handler), logger: null, maxAttempts: 3, retryDelay: TimeSpan.Zero)
            .DownloadAsync(Url, target);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task DownloadAsync_KeepsPartWhenAllAttemptsFail()
    {
        using var dir = new TempDirectory();
        string target = dir.File("YuumiX-1.0.0.zip");
        var handler = new StubHandler((_, _, _) => Task.FromResult(
            Truncated(Payload[..6], declaredLength: Payload.Length, etag: "\"v1\"")));

        var error = await Assert.ThrowsAsync<IOException>(() =>
            new ResumableDownloader(new HttpClient(handler), logger: null, maxAttempts: 1, retryDelay: TimeSpan.Zero)
                .DownloadAsync(Url, target));

        // 断点必须留住：这是「下次点继续下载」的前提
        Assert.True(File.Exists(ResumableDownloader.GetPartPath(target)));
        Assert.Equal(6, new FileInfo(ResumableDownloader.GetPartPath(target)).Length);
        Assert.Contains("断点", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
    }

    // ── 断点可信度 ──────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_DiscardsPartWithoutCheckpoint()
    {
        using var dir = new TempDirectory();
        string target = dir.File("YuumiX-1.0.0.zip");
        File.WriteAllBytes(ResumableDownloader.GetPartPath(target), [0xEE, 0xEE, 0xEE, 0xEE]);

        var handler = new StubHandler((_, _, _) => Task.FromResult(Full(Payload)));

        await new ResumableDownloader(new HttpClient(handler), logger: null, maxAttempts: 1, retryDelay: TimeSpan.Zero)
            .DownloadAsync(Url, target);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(target));
        // 没有元数据就无法确认残包来源，只能重下
        Assert.Null(Assert.Single(handler.Requests).Range);
    }

    [Fact]
    public async Task DownloadAsync_DiscardsPartForDifferentUrl()
    {
        using var dir = new TempDirectory();
        string target = dir.File("YuumiX-1.0.0.zip");
        WritePartial(target, Payload[..6], etag: "\"v1\"", total: Payload.Length,
            url: "https://github.com/ISuuuu/YuumiX/releases/download/v0.9.0/YuumiX_portable.zip");

        var handler = new StubHandler((_, _, _) => Task.FromResult(Full(Payload)));

        await new ResumableDownloader(new HttpClient(handler), logger: null, maxAttempts: 1, retryDelay: TimeSpan.Zero)
            .DownloadAsync(Url, target);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(target));
        Assert.Null(Assert.Single(handler.Requests).Range);
    }

    [Fact]
    public async Task DownloadAsync_DiscardsPartWithoutStrongValidator()
    {
        using var dir = new TempDirectory();
        string target = dir.File("YuumiX-1.0.0.zip");
        // 弱 ETag 不能用于 If-Range（RFC 9110），没有时间戳就只能放弃断点
        WritePartial(target, Payload[..6], etag: "W/\"v1\"", total: Payload.Length);

        var handler = new StubHandler((_, _, _) => Task.FromResult(Full(Payload)));

        await new ResumableDownloader(new HttpClient(handler), logger: null, maxAttempts: 1, retryDelay: TimeSpan.Zero)
            .DownloadAsync(Url, target);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(target));
        Assert.Null(Assert.Single(handler.Requests).Range);
    }

    [Fact]
    public async Task DownloadAsync_RestartsWhenServerRejectsRange()
    {
        using var dir = new TempDirectory();
        string target = dir.File("YuumiX-1.0.0.zip");
        // 残包短于记录总长（能续），但服务端已换了更短的资产 → 区间越界 416
        WritePartial(target, Payload[..6], etag: "\"v1\"", total: Payload.Length);

        var handler = new StubHandler((request, call, _) => Task.FromResult(
            call == 1
                ? new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                : Full(Payload)));

        await new ResumableDownloader(new HttpClient(handler), logger: null, maxAttempts: 3, retryDelay: TimeSpan.Zero)
            .DownloadAsync(Url, target);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task DownloadAsync_DoesNotRetryOnNotFound()
    {
        using var dir = new TempDirectory();
        string target = dir.File("YuumiX-1.0.0.zip");
        var handler = new StubHandler((_, _, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new ResumableDownloader(new HttpClient(handler), logger: null, maxAttempts: 3, retryDelay: TimeSpan.Zero)
                .DownloadAsync(Url, target));

        // 资产不存在属于确定性失败，重试只会拖慢报错
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DownloadAsync_RetriesOnServerError()
    {
        using var dir = new TempDirectory();
        string target = dir.File("YuumiX-1.0.0.zip");
        var handler = new StubHandler((_, call, _) => Task.FromResult(
            call < 3
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Full(Payload)));

        await new ResumableDownloader(new HttpClient(handler), logger: null, maxAttempts: 3, retryDelay: TimeSpan.Zero)
            .DownloadAsync(Url, target);

        Assert.Equal(3, handler.Calls);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task DownloadAsync_CompletesWhenPartAlreadyHasFullLength()
    {
        using var dir = new TempDirectory();
        string target = dir.File("YuumiX-1.0.0.zip");
        // 下完了但改名被中断（进程被杀）：残包与记录总长一致
        WritePartial(target, Payload, etag: "\"v1\"", total: Payload.Length);

        var handler = new StubHandler((_, _, _) => throw new InvalidOperationException("不应发起请求"));

        await new ResumableDownloader(new HttpClient(handler), logger: null, maxAttempts: 1, retryDelay: TimeSpan.Zero)
            .DownloadAsync(Url, target);

        Assert.Equal(0, handler.Calls);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(ResumableDownloader.GetPartPath(target)));
        Assert.False(File.Exists(ResumableDownloader.GetMetaPath(target)));
    }

    // ── 缓存清理与命名 ──────────────────────────────────────────

    [Theory]
    [InlineData("YuumiX-0.3.0.zip", "0.3.0")]
    [InlineData("YuumiX-0.3.0-setup.exe", "0.3.0-setup")]
    [InlineData("YuumiX-0.3.0.zip.part", "0.3.0")]
    [InlineData("YuumiX-0.3.0.zip.part.json", "0.3.0")]
    public void TryParseDownloadFileName_HandlesAllArtifacts(string fileName, string expected)
    {
        Assert.True(UpdateService.TryParseDownloadFileName(fileName, out string versionText));
        Assert.Equal(expected, versionText);
    }

    [Theory]
    [InlineData("other-0.3.0.zip")]
    [InlineData("YuumiX-.zip")]
    [InlineData("")]
    public void TryParseDownloadFileName_RejectsUnknownNames(string fileName)
    {
        Assert.False(UpdateService.TryParseDownloadFileName(fileName, out _));
    }

    [Fact]
    public void CleanupDownloadCache_KeepsNewerBreakpointAndDropsOldOnes()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();
        var paths = RuntimePaths.Detect(exeDir.Path, appData.Path);
        string dir = Path.Combine(paths.DataDirectory, "downloads");
        Directory.CreateDirectory(dir);

        // 远高于当前版本 → 必须保留（用户下次点「继续下载」要用）
        string newerPart = Path.Combine(dir, "YuumiX-999.0.0.zip.part");
        string newerMeta = newerPart + ".json";
        File.WriteAllBytes(newerPart, [1, 2, 3]);
        File.WriteAllText(newerMeta, "{}");

        // 远低于当前版本 → 必须清掉（否则旧版本的半截包永远占着几百 MB）
        string olderPart = Path.Combine(dir, "YuumiX-0.0.1.zip.part");
        File.WriteAllBytes(olderPart, [1, 2, 3]);

        // 认不出命名的文件一律不动
        string unknown = Path.Combine(dir, "manual-backup.zip");
        File.WriteAllBytes(unknown, [1]);

        var service = new UpdateService(paths, static () => new AppConfig());
        service.CleanupDownloadCache();

        Assert.True(File.Exists(newerPart));
        Assert.True(File.Exists(newerMeta));
        Assert.False(File.Exists(olderPart));
        Assert.True(File.Exists(unknown));
    }

    // ── 构造辅助 ────────────────────────────────────────────────

    private static void WritePartial(string target, byte[] bytes, string? etag, long total, string? url = null)
    {
        File.WriteAllBytes(ResumableDownloader.GetPartPath(target), bytes);
        File.WriteAllText(
            ResumableDownloader.GetMetaPath(target),
            JsonSerializer.Serialize(new
            {
                url = url ?? Url,
                etag,
                lastModified = (string?)null,
                totalBytes = total,
            }));
    }

    private static HttpResponseMessage Full(byte[] bytes, string? etag = "\"v1\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    private static HttpResponseMessage Partial(byte[] bytes, long from, long total, string? etag = "\"v1\"")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentRange = new ContentRangeHeaderValue(from, from + bytes.Length - 1, total);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    /// <summary>声明长度大于实际字节数，模拟「连接被掐断但流正常结束」这种最隐蔽的截断。</summary>
    private static HttpResponseMessage Truncated(byte[] bytes, long declaredLength, string? etag = "\"v1\"")
    {
        var content = new StreamContent(new MemoryStream(bytes));
        content.Headers.ContentLength = declaredLength;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    /// <summary>同步收集进度：不用 <see cref="Progress{T}"/>，避免测试受同步上下文调度影响。</summary>
    private sealed class CollectingProgress : IProgress<DownloadProgress>
    {
        public DownloadProgress? Last { get; private set; }

        public void Report(DownloadProgress value) => Last = value;
    }

    /// <summary>按调用次数编程响应，并记录每次请求（用于断言 Range / If-Range）。</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        private int _calls;
        private readonly List<RecordedRequest> _requests = [];

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<RecordedRequest> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _calls);
            lock (_requests)
            {
                _requests.Add(new RecordedRequest(
                    request.Headers.Range,
                    request.Headers.TryGetValues("If-Range", out var values) ? values.FirstOrDefault() : null));
            }

            return responder(request, call, cancellationToken);
        }
    }

    private sealed record RecordedRequest(RangeHeaderValue? Range, string? IfRange);
}
