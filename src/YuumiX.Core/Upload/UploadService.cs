using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Upload;

/// <summary>单场对局上传结果（对齐旧 upload.rs 的语义三分支）。</summary>
public enum UploadOutcome
{
    /// <summary>服务器没有这局（isNewMatch = true）。</summary>
    New,

    /// <summary>服务器已有这局（isNewMatch = false）。</summary>
    Exists,

    /// <summary>上传失败（网络/服务器错误耗尽重试预算，或客户端错误被拒）。</summary>
    Failed,
}

/// <summary>
/// 对局数据上传服务（对应旧 <c>upload.rs</c>）。
///
/// 语义对齐：
/// <list type="number">
///   <item>URL 由 <see cref="UploadUrlBuilder"/> 从配置的 <c>UploadApiUrl</c> 推导
///         （缺 scheme 补 http://、自动拼 /api/lol/upload）；</item>
///   <item>POST 载荷失败最多重试 5 次（间隔 1s，受总预算约束）；4xx 客户端错误不重试；</item>
///   <item>服务器响应 <c>{ success, data.isNewMatch }</c> 决定 New/Exists；</item>
///   <item>开关语义：<c>Functions.UploadEnabled</c> 在旧版位于战绩查询页，本次只在 Core
///         保留消费点（不新增 UI）；<c>General.UploadApiUrl</c> 为空时上传不执行。</item>
/// </list>
/// </summary>
public sealed class UploadService
{
    /// <summary>重试预算：与旧实现一致的 5 次重试 + 1s 间隔（总耗时上限 6s 左右）。</summary>
    internal static readonly TimeSpan RetryBudget = TimeSpan.FromSeconds(6);
    private const int MaxRetries = 5;

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Func<AppConfig> _configProvider;
    private readonly ILogger<UploadService>? _logger;

    /// <summary>独立 HTTP 客户端（外网 API，走系统代理；旧实现同款 external_http_client 语义）。
    /// 批量上传复用同一个实例：否则每次批量都要重新建连接池。</summary>
    internal static readonly Lazy<HttpClient> ExternalClient = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            // 与 LCU 自签证书无关的外网请求，正常校验证书
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    });

    public UploadService(
        Func<ILcuTransport?> transportProvider,
        Func<AppConfig> configProvider,
        ILogger<UploadService>? logger = null)
    {
        _transportProvider = transportProvider;
        _configProvider = configProvider;
        _logger = logger;
    }

    /// <summary>
    /// 上传指定对局。返回结局与原因；未连接 LCU、开关关闭或地址未配置时返回
    /// <see cref="UploadOutcome.Failed"/> 且原因说明跳过缘由（调用方据此不落盘重试）。
    /// </summary>
    public async Task<(UploadOutcome Outcome, string Message)> UploadMatchAsync(
        long gameId,
        string? currentSummonerPuuid,
        Func<int, string>? championNameResolver = null,
        CancellationToken cancellationToken = default)
    {
        var config = _configProvider();
        if (!config.Functions.UploadEnabled)
        {
            return (UploadOutcome.Failed, "上传功能未开启");
        }

        if (string.IsNullOrWhiteSpace(config.General.UploadApiUrl))
        {
            return (UploadOutcome.Failed, "未配置服务器 API 地址");
        }

        var (payload, error) = await TryBuildPayloadAsync(
            gameId, currentSummonerPuuid, championNameResolver, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return (UploadOutcome.Failed, error ?? "对局详情为空");
        }

        string url = UploadUrlBuilder.BuildUploadUrl(config.General.UploadApiUrl);
        return await PostPayloadAsync(payload, url, gameId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 拉取对局详情并构建上传载荷。单局与批量两条链路共用同一份实现：
    /// 载荷字段集是上传契约里最容易漂移的部分，复制第二份迟早会对不上。
    /// </summary>
    /// <returns>成功返回载荷；失败返回原因（调用方据此决定是否落盘重试）。</returns>
    internal async Task<(UploadPayload? Payload, string? Error)> TryBuildPayloadAsync(
        long gameId,
        string? currentSummonerPuuid,
        Func<int, string>? championNameResolver,
        CancellationToken cancellationToken)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            return (null, "LCU 未连接");
        }

        LcuResponse resp = await transport.SendAsync(
            LcuRequest.Get($"/lol-match-history/v1/games/{gameId}"),
            cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccess || string.IsNullOrWhiteSpace(resp.Body))
        {
            return (null, $"拉取对局详情失败: HTTP {resp.StatusCode}");
        }

        JsonElement detail;
        try
        {
            using var doc = JsonDocument.Parse(resp.Body);
            detail = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return (null, $"解析对局详情失败: {ex.Message}");
        }

        UploadPayload? payload = UploadPayloadBuilder.FromGameDetail(
            detail, currentSummonerPuuid, championNameResolver);
        return payload is null ? (null, "对局详情为空") : (payload, null);
    }

    /// <summary>POST 载荷：5 次重试 + 4xx 不重试 + success/isNewMatch 判定（对齐旧 post_payload_to_api）。</summary>
    internal async Task<(UploadOutcome, string)> PostPayloadAsync(
        UploadPayload payload,
        string uploadUrl,
        long gameId,
        CancellationToken cancellationToken)
    {
        string lastError = string.Empty;
        var deadline = DateTime.UtcNow + RetryBudget;

        for (var retry = 0; retry <= MaxRetries; retry++)
        {
            if (DateTime.UtcNow >= deadline)
            {
                lastError = $"重试预算耗尽: {lastError}";
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await ExternalClient.Value.PostAsJsonAsync(
                    uploadUrl, payload, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(
                            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                        var root = doc.RootElement;
                        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
                        {
                            bool isNew = root.TryGetProperty("data", out var data)
                                && data.ValueKind == JsonValueKind.Object
                                && data.TryGetProperty("isNewMatch", out var isNewMatch)
                                && isNewMatch.ValueKind == JsonValueKind.True;
                            _logger?.LogInformation("对局 {GameId} 上传成功 ({Result})", gameId, isNew ? "new" : "exists");
                            return isNew
                                ? (UploadOutcome.New, "new")
                                : (UploadOutcome.Exists, "exists");
                        }

                        lastError = $"服务器处理失败: {(root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString() : "未知错误")}";
                    }
                    catch (JsonException)
                    {
                        lastError = "解析响应失败";
                    }
                }
                else if ((int)response.StatusCode is >= 400 and < 500)
                {
                    // 4xx 客户端错误（参数/鉴权等）重试无意义，直接放弃
                    _logger?.LogWarning("对局 {GameId} 上传被拒绝: HTTP {Status}，不重试", gameId, (int)response.StatusCode);
                    return (UploadOutcome.Failed, $"对局 {gameId} 上传失败: HTTP {(int)response.StatusCode} (客户端错误不重试)");
                }
                else
                {
                    lastError = $"HTTP {(int)response.StatusCode}";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = $"请求失败: {ex.Message}";
            }

            if (retry < MaxRetries)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                _logger?.LogWarning("对局 {GameId} 上传失败 ({Error}), 重试 {Retry}/{Max}", gameId, lastError, retry + 1, MaxRetries);
                await Task.Delay(TimeSpan.FromSeconds(1) < remaining ? TimeSpan.FromSeconds(1) : remaining, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return (UploadOutcome.Failed, $"对局 {gameId} 上传失败: {lastError}");
    }
}

/// <summary>
/// 上传 URL 推导（对齐旧 ensure_scheme / build_upload_url / signalr.rs clean_server_url）。
/// 纯函数、可单测：上传 API 与 SignalR Hub 基址共用同一个配置字段 <c>UploadApiUrl</c>。
/// </summary>
public static class UploadUrlBuilder
{
    /// <summary>缺 scheme 时补 http://（旧 ensure_scheme）。</summary>
    public static string EnsureScheme(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"http://{url}";

    /// <summary>拼接单次上传 URL（旧 build_upload_url）。</summary>
    public static string BuildUploadUrl(string baseUrl)
    {
        string url = EnsureScheme(baseUrl.TrimEnd('/'));
        if (url.EndsWith("/api/lol/upload-batch", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^"/api/lol/upload-batch".Length];
        }

        if (!url.Contains("/api/lol/upload", StringComparison.Ordinal))
        {
            url += "/api/lol/upload";
        }

        return url;
    }

    /// <summary>拼接批量上传 URL（旧 build_batch_upload_url）：先剥掉单局后缀再拼批量后缀。</summary>
    public static string BuildBatchUploadUrl(string baseUrl)
    {
        string url = EnsureScheme(baseUrl.TrimEnd('/'));
        if (url.EndsWith("/api/lol/upload-batch", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        const string UploadSuffix = "/api/lol/upload";
        if (url.EndsWith(UploadSuffix, StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^UploadSuffix.Length];
        }

        return url + "/api/lol/upload-batch";
    }

    /// <summary>
    /// 推导 SignalR Hub 基址（旧 signalr.rs clean_server_url 的循环剥离规则）：
    /// 反复剥掉尾斜杠与 /api/lol/upload、/api/lol/upload-batch、/api/lol/lcu、/lcuHub 后缀，再拼 /lcuHub。
    /// </summary>
    public static string BuildHubBaseUrl(string baseUrl)
    {
        string clean = baseUrl.Trim();
        while (true)
        {
            string before = clean;
            clean = clean.TrimEnd('/');
            foreach (string suffix in new[]
                     {
                         "/api/lol/upload",
                         "/api/lol/upload-batch",
                         "/api/lol/lcu",
                         "/lcuHub",
                     })
            {
                if (clean.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    clean = clean[..^suffix.Length];
                }
            }

            if (clean == before)
            {
                break;
            }
        }

        return $"{clean}/lcuHub";
    }
}
