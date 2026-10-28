using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Updates;

/// <summary>
/// 更新包断点元数据（文件名 <c>&lt;目标文件&gt;.part.json</c>）。
///
/// 为什么要落盘而不是只放内存：断点最有价值的场景恰恰是「进程已经不在了」——
/// 用户关掉应用、机器重启、下载失败后退出再进来，只有磁盘上的记录能回答
/// 「这半截包是哪个地址下的、能不能接着下」。
/// </summary>
/// <param name="Url">断点对应的下载地址。地址变了说明换了版本或换了资产，断点必须作废。</param>
/// <param name="ETag">服务端强校验器，用于 <c>If-Range</c>；弱 ETag 不能用于区间续传。</param>
/// <param name="LastModified"><c>If-Range</c> 的退路，ETag 缺失或不强时使用。</param>
/// <param name="TotalBytes">服务端声明的完整长度；0 表示未知（此时无法校验完整性）。</param>
internal sealed record DownloadCheckpoint(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("etag")] string? ETag,
    [property: JsonPropertyName("lastModified")] string? LastModified,
    [property: JsonPropertyName("totalBytes")] long TotalBytes);

/// <summary>单次传输结果：服务端声明的总长，以及本次是否已读到流末尾且长度吻合。</summary>
internal readonly record struct TransferResult(long Total, bool Complete);

/// <summary>服务端对区间的响应与本地断点对不上（416，或 <c>Content-Range</c> 起点不同）。</summary>
internal sealed class RangeRestartRequiredException(string message) : Exception(message);

/// <summary>
/// 支持断点续传与自动重试的 HTTP 文件下载器。
///
/// 解决的问题（旧实现是一次性流式拷贝）：任何一次中断——用户关掉设置页、切网络、
/// 代理抖动、应用退出——都会让已经下到的 200MB 归零，重来一次。
///
/// 四条设计要点：
/// <list type="number">
///   <item><b>残包落盘</b>：数据写 <c>&lt;目标&gt;.part</c>，元数据写 <c>&lt;目标&gt;.part.json</c>。
///         只有校验通过的完整包才会被改名为最终文件，因此「半个包」永远不会以正式文件名出现。</item>
///   <item><b>区间请求</b>：断点处发 <c>Range: bytes=N-</c>，并带 <c>If-Range</c> 强校验器。
///         校验器不一致时服务端返回 200 全量，本地自动退回从头写——绝不在新包后面接旧字节。</item>
///   <item><b>空闲超时而非总时长</b>：大包下载动辄数分钟，任何「总超时」都会误杀正常下载。
///         这里只用「连续 N 秒没有收到任何字节」判定断线，收到数据就重新计时。</item>
///   <item><b>中断即重试</b>：单次调用内部最多重试 <see cref="DefaultMaxAttempts"/> 次，
///         每次重试都从最新的 <c>.part</c> 长度续传，因此抖动几乎无感；
///         重试耗尽才把异常抛给调用方，断点保留，用户再点一次即从断点继续。</item>
/// </list>
/// </summary>
public sealed class ResumableDownloader
{
    /// <summary>残包文件后缀。</summary>
    public const string PartSuffix = ".part";

    /// <summary>断点元数据后缀（拼在 <see cref="PartSuffix"/> 之后）。</summary>
    public const string MetaSuffix = ".part.json";

    /// <summary>单次下载的最大尝试次数（1 次首发 + 4 次续传重试）。</summary>
    internal const int DefaultMaxAttempts = 5;

    /// <summary>首次重试延迟，之后按 2 的幂递增。</summary>
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>重试延迟上限，避免长时间失败时把用户按在「下载中」状态里干等。</summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 空闲超时：连续 60 秒收不到字节即视为断线。
    ///
    /// 刻意不用「总时长」：300MB 包在窄带上正常下载就要十几分钟，
    /// 总超时必然误杀；反过来只盯「有没有进展」既能识别卡死，也不会误伤慢速链路。
    /// </summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>进度上报节流间隔：80KB 一块、几百 MB 的包会上报上万次，UI 排队渲染反而拖慢下载。</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(120);

    private const int CopyBufferSize = 81920;

    private static readonly JsonSerializerOptions MetaJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _client;
    private readonly ILogger? _logger;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;

    public ResumableDownloader(HttpClient client, ILogger? logger = null)
        : this(client, logger, DefaultMaxAttempts, DefaultRetryDelay)
    {
    }

    /// <summary>供单测注入重试策略：默认 5 次 / 1 秒起，测试里压到 1 次尝试或 0 延迟。</summary>
    internal ResumableDownloader(HttpClient client, ILogger? logger, int maxAttempts, TimeSpan retryDelay)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _logger = logger;
        _maxAttempts = Math.Max(1, maxAttempts);
        _retryDelay = retryDelay < TimeSpan.Zero ? TimeSpan.Zero : retryDelay;
    }

    /// <summary>残包路径。</summary>
    public static string GetPartPath(string targetPath) => targetPath + PartSuffix;

    /// <summary>断点元数据路径。</summary>
    public static string GetMetaPath(string targetPath) => targetPath + MetaSuffix;

    /// <summary>
    /// 当前可用于续传的字节数（无残包时为 0）。
    ///
    /// 只看文件长度，不校验元数据：它的用途是给按钮文案一个「要不要显示继续下载」的提示，
    /// 真正的可续传判定在 <see cref="DownloadAsync"/> 内部完成——那里校验不过会自动从头下，
    /// 不会把来源不明的字节拼进更新包。
    /// </summary>
    public static long GetResumableBytes(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return 0;
        }

        try
        {
            var info = new FileInfo(GetPartPath(targetPath));
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 下载 <paramref name="url"/> 到 <paramref name="targetPath"/>，支持断点续传与自动重试。
    ///
    /// 中断语义：
    /// <list type="bullet">
    ///   <item>用户取消（<paramref name="token"/> 触发）→ 立即抛 <see cref="OperationCanceledException"/>，
    ///         断点<b>保留</b>；</item>
    ///   <item>网络抖动 / 服务端提前断开 → 内部续传重试，用户无感；</item>
    ///   <item>重试耗尽 → 抛 <see cref="IOException"/>，断点同样<b>保留</b>，
    ///         提示里带已保留的体量，下次点「继续下载」接着下。</item>
    /// </list>
    /// </summary>
    /// <param name="url">下载地址（调用方负责白名单校验）。</param>
    /// <param name="targetPath">最终文件路径；残包与元数据落在它同目录下。</param>
    /// <param name="progress">进度回调，按绝对字节数上报（续传时起点不为 0）。</param>
    /// <param name="token">取消令牌。</param>
    public async Task DownloadAsync(
        string url,
        string targetPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string partPath = GetPartPath(targetPath);
        string metaPath = GetMetaPath(targetPath);

        // 换了版本 / 换了资产：残包属于另一个文件，留着只会在续传时污染新包。
        if (ReadCheckpoint(metaPath) is { } existing
            && !string.Equals(existing.Url, url, StringComparison.Ordinal))
        {
            _logger?.LogInformation("更新包下载地址已变化，丢弃旧断点");
            TryDelete(partPath, metaPath);
        }

        // 残包已满长：上次其实下完了，只是没来得及改名就被杀（或改名时目标被占用）。
        // 这种情况直接完成，既省一次请求，也避免「满长残包 + Range 越界」被服务端判 416 后白重下。
        if (TryCompleteFromPart(partPath, metaPath, targetPath))
        {
            return;
        }

        Exception? lastError = null;

        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();

            // 每轮都重新读元数据：上一轮传输已经把最新的 ETag / 总长写回磁盘，
            // 用内存里的旧值去发 If-Range 会让服务端判定校验失败、退回全量下载。
            var checkpoint = ReadCheckpoint(metaPath);
            long offset = PrepareOffset(partPath, metaPath, checkpoint);

            try
            {
                var result = await TransferAsync(url, partPath, metaPath, offset, checkpoint, progress, token)
                    .ConfigureAwait(false);

                long length = FileLength(partPath);
                if (result.Complete && (result.Total <= 0 || length == result.Total))
                {
                    // 只有长度吻合的完整包才配拥有正式文件名。
                    File.Move(partPath, targetPath, overwrite: true);
                    TryDelete(metaPath);
                    _logger?.LogInformation("更新包下载完成：{Path}（{Bytes} 字节）", targetPath, length);
                    return;
                }

                lastError = new IOException(
                    $"更新包下载不完整（{length}/{Describe(result.Total)} 字节）");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 用户主动取消：绝不删断点，下次「继续下载」从当前位置接上。
                _logger?.LogInformation("更新包下载已取消，已保留 {Bytes} 字节断点", FileLength(partPath));
                throw;
            }
            catch (RangeRestartRequiredException ex)
            {
                // 服务端拒绝区间（416）或返回的起点与断点不符：断点已失效，清掉重来。
                _logger?.LogWarning(ex, "更新包断点失效，改为从头下载");
                TryDelete(partPath, metaPath);
                lastError = ex;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // 空闲超时（非用户取消）、连接被重置、磁盘暂时不可写等：
                // 下一轮会从最新断点续传，因此这里只记录、不丢弃。
                lastError = ex;
                _logger?.LogWarning(
                    ex, "更新包下载第 {Attempt}/{MaxAttempts} 次中断，将从断点续传", attempt, _maxAttempts);
            }

            if (attempt < _maxAttempts && _retryDelay > TimeSpan.Zero)
            {
                await Task.Delay(RetryDelayFor(attempt), token).ConfigureAwait(false);
            }
        }

        long kept = FileLength(partPath);
        throw new IOException(
            $"下载失败：{lastError?.Message ?? "未知原因"}；"
            + $"已保留 {kept / 1024.0 / 1024.0:F1} MB 断点，可稍后继续下载",
            lastError);
    }

    /// <summary>
    /// 残包长度已等于记录总长时，直接完成改名。
    ///
    /// 数据是顺序追加写出来的，长度吻合即代表内容完整；这里唯一要防的是「记录总长本身不可信」，
    /// 因此只接受带强校验器的断点——与 <see cref="PrepareOffset"/> 的保守程度保持一致。
    /// </summary>
    private bool TryCompleteFromPart(string partPath, string metaPath, string targetPath)
    {
        if (ReadCheckpoint(metaPath) is not { TotalBytes: > 0 } checkpoint
            || BuildIfRange(checkpoint) is null
            || FileLength(partPath) != checkpoint.TotalBytes)
        {
            return false;
        }

        try
        {
            File.Move(partPath, targetPath, overwrite: true);
            TryDelete(metaPath);
            _logger?.LogInformation("残包长度已与记录一致，直接完成：{Path}", targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目标被占用（例如旧版本进程还在跑）：退回正常续传路径，
            // 那里会用 Range 换一次 416 并重下，最终仍能给出明确错误。
            _logger?.LogWarning(ex, "残包已完整但改名失败，转走续传流程");
            return false;
        }
    }

    /// <summary>
    /// 决定本次从哪个字节开始写。
    ///
    /// 判定链条刻意保守——宁可重下，也不能把来源不明的字节拼进更新包（拼接错误在
    /// 安装阶段才暴露，那时应用已经退出，用户看到的只是「更新后打不开」）：
    /// 没有元数据、没有强校验器、长度超过记录总长，三种情况一律截断重来。
    /// </summary>
    private long PrepareOffset(string partPath, string metaPath, DownloadCheckpoint? checkpoint)
    {
        long length = FileLength(partPath);
        if (length <= 0)
        {
            return 0;
        }

        if (checkpoint is null)
        {
            _logger?.LogInformation("发现没有断点记录的 {Bytes} 字节残包，丢弃重下", length);
            TryDelete(partPath);
            return 0;
        }

        if (BuildIfRange(checkpoint) is null)
        {
            // 无强校验器时，服务端若对「另一个文件」也返回 206，本地就会把两个版本拼在一起。
            // 这种静默损坏的代价远高于重下，直接放弃断点。
            _logger?.LogInformation("断点缺少强校验器（ETag/Last-Modified），无法安全续传，丢弃重下");
            TryDelete(partPath, metaPath);
            return 0;
        }

        if (checkpoint.TotalBytes > 0 && length > checkpoint.TotalBytes)
        {
            _logger?.LogInformation(
                "残包长度 {Length} 超过记录总长 {Total}，丢弃重下", length, checkpoint.TotalBytes);
            TryDelete(partPath);
            return 0;
        }

        _logger?.LogInformation("检测到断点，从 {Bytes} 字节处续传", length);
        return length;
    }

    /// <summary>
    /// 执行一次传输（可能只是续传的后半段）。
    ///
    /// 状态码处理：
    /// <list type="bullet">
    ///   <item>206：校验 <c>Content-Range</c> 起点与本地断点一致，总长取 <c>Content-Range.Length</c>；</item>
    ///   <item>200：服务端忽略了 Range（<c>If-Range</c> 校验失败，说明资产已变），
    ///         本地退回 0 并截断残包，从头写；</item>
    ///   <item>416：本地残包比远端还长，抛出 <see cref="RangeRestartRequiredException"/> 让上层清理。</item>
    /// </list>
    /// </summary>
    private async Task<TransferResult> TransferAsync(
        string url,
        string partPath,
        string metaPath,
        long offset,
        DownloadCheckpoint? checkpoint,
        IProgress<DownloadProgress>? progress,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            if (BuildIfRange(checkpoint) is { } ifRange)
            {
                request.Headers.TryAddWithoutValidation("If-Range", ifRange);
            }
        }

        // 空闲计时覆盖「等响应头 + 收数据」两段：连上却一直不给字节，同样属于断线。
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(IdleTimeout);

        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            throw new RangeRestartRequiredException("服务端拒绝 Range 请求（416），断点已失效");
        }

        response.EnsureSuccessStatusCode();

        long start = offset;
        long total;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != offset)
            {
                throw new RangeRestartRequiredException(
                    $"服务端返回的区间起点 {range?.From?.ToString(CultureInfo.InvariantCulture) ?? "缺失"} "
                    + $"与本地断点 {offset} 不一致");
            }

            // Content-Range 里的 Length 是完整长度；缺省时用「已下载 + 本次响应长度」兜底。
            total = range?.Length ?? (offset + (response.Content.Headers.ContentLength ?? 0));
        }
        else
        {
            // 200：全量响应。若本地本来有断点，说明校验器已变，必须从头写。
            start = 0;
            total = response.Content.Headers.ContentLength ?? 0;
        }

        // 元数据先落盘再写数据：即使这一轮中途被杀，下一轮也能凭它判断能否续、
        // 以及用什么校验器发 If-Range。
        WriteCheckpoint(
            metaPath,
            new DownloadCheckpoint(
                url,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture),
                total));

        long downloaded = start;
        var sinceReport = Stopwatch.StartNew();
        var buffer = new byte[CopyBufferSize];

        await using var source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
        await using var target = new FileStream(
            partPath,
            start > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            useAsync: true);

        while (true)
        {
            // 收到字节就重新计时；读不到字节超过 IdleTimeout 即取消本次传输并走上层重试。
            idle.CancelAfter(IdleTimeout);
            int read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            downloaded += read;

            if (sinceReport.Elapsed >= ProgressInterval)
            {
                sinceReport.Restart();
                progress?.Report(CreateProgress(downloaded, total));
            }
        }

        await target.FlushAsync(token).ConfigureAwait(false);

        // 收尾必报一次：节流可能吞掉最后几 MB 的进度，界面上会停在 99%。
        progress?.Report(CreateProgress(downloaded, total));

        return new TransferResult(total, Complete: total <= 0 || downloaded == total);
    }

    private static DownloadProgress CreateProgress(long downloaded, long total)
        => new(
            downloaded,
            total > 0 ? total : null,
            total > 0 ? Math.Min(100.0, downloaded * 100.0 / total) : null);

    /// <summary>
    /// <c>If-Range</c> 只接受强校验器：强 ETag，或 <c>Last-Modified</c>。
    ///
    /// 弱 ETag（<c>W/"..."</c>）按 RFC 9110 不得用于区间续传，服务端会忽略它并直接返回
    /// 206——那样本地就会把旧包的后半段接在新包后面，属于静默损坏。故弱 ETag 退到时间戳。
    /// </summary>
    private static string? BuildIfRange(DownloadCheckpoint? checkpoint)
    {
        if (checkpoint is null)
        {
            return null;
        }

        if (checkpoint.ETag is { Length: > 0 } etag
            && !etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            return etag;
        }

        return checkpoint.LastModified is { Length: > 0 } lastModified ? lastModified : null;
    }

    private DownloadCheckpoint? ReadCheckpoint(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath))
            {
                return null;
            }

            var checkpoint = JsonSerializer.Deserialize<DownloadCheckpoint>(
                File.ReadAllText(metaPath), MetaJsonOptions);
            return string.IsNullOrEmpty(checkpoint?.Url) ? null : checkpoint;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "读取更新包断点记录失败，按无断点处理");
            return null;
        }
    }

    /// <summary>写元数据（先写临时文件再改名，避免进程被杀时留下半截 JSON）。失败只记日志。</summary>
    private void WriteCheckpoint(string metaPath, DownloadCheckpoint checkpoint)
    {
        try
        {
            string tmp = metaPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(checkpoint, MetaJsonOptions));
            File.Move(tmp, metaPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进元数据不致命：这一轮仍能下完，只是下次无法安全续传。
            _logger?.LogWarning(ex, "写入更新包断点记录失败");
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private TimeSpan RetryDelayFor(int attempt)
    {
        double seconds = _retryDelay.TotalSeconds * Math.Pow(2, attempt - 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
    }

    /// <summary>
    /// 可重试的异常：网络中断、空闲超时、服务端 5xx/限流、磁盘瞬时故障。
    ///
    /// HTTP 状态码要分开看：<c>HttpRequestException</c> 承载了 4xx 与 5xx 两类，
    /// 一律重试会让「资产根本不存在（404）」白等 15 秒还给出模糊提示；
    /// 只有 5xx / 408 / 429 才是「过一会儿可能好」。
    /// </summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            || (int)status >= 500,

        IOException => true,

        // 空闲超时表现为 OperationCanceledException，但用户 token 并未触发（那种情况已在上层单独处理）。
        OperationCanceledException => true,
        HttpRequestException => true,
        TimeoutException => true,
        _ => false,
    };

    private void TryDelete(params string[] paths)
    {
        foreach (string path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "清理更新包临时文件失败：{Path}", path);
            }
        }
    }

    private static string Describe(long total)
        => total > 0 ? total.ToString(CultureInfo.InvariantCulture) : "未知";
}
