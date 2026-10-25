using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Upload;

/// <summary>批量上传结果（对齐旧 <c>BatchUploadResult</c>）。</summary>
public sealed record BatchUploadResult(int SuccessCount, int FailedCount, string? Error);

/// <summary>
/// 多局批量上传（对应旧 <c>batch_upload_matches</c> 命令与 <c>batch_upload_by_ids</c>）。
///
/// 与单局链路的分工：单局走队列（去重、串行、失败落盘重试），批量走一次性提交——
/// 战绩查询页翻出一页对局时用它补传，服务端已有就返回 exists，重复提交是幂等的，
/// 因此批量不需要落盘重试（失败的对局会在下一次翻页或对局结束时再次被提交）。
/// </summary>
public sealed class UploadBatchService
{
    /// <summary>并发拉取对局详情的上限（旧 <c>buffer_unordered(10)</c>）：不加限流会打爆 LCU。</summary>
    private const int DetailConcurrency = 10;

    /// <summary>每批提交的对局数（旧 <c>chunks(10)</c>）。</summary>
    private const int BatchSize = 10;

    /// <summary>整批上传的总超时（旧 120s）。</summary>
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(120);

    private readonly UploadService _single;
    private readonly Func<AppConfig> _configProvider;
    private readonly Func<int, string>? _championNameResolver;
    private readonly ILogger<UploadBatchService>? _logger;

    public UploadBatchService(
        UploadService single,
        Func<AppConfig> configProvider,
        Func<int, string>? championNameResolver = null,
        ILogger<UploadBatchService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(single);
        _single = single;
        _configProvider = configProvider;
        _championNameResolver = championNameResolver;
        _logger = logger;
    }

    /// <summary>
    /// 按 gameId 列表批量上传。返回成功/失败计数与首个错误原因。
    /// </summary>
    /// <param name="gameIds">待上传的对局 ID。</param>
    /// <param name="currentSummonerPuuid">当前账号 puuid（决定内层参与者）。</param>
    /// <param name="championNameResolver">英雄 ID → 名称。</param>
    public async Task<BatchUploadResult> UploadAsync(
        IReadOnlyList<long> gameIds,
        string? currentSummonerPuuid,
        Func<int, string>? championNameResolver = null,
        CancellationToken cancellationToken = default)
    {
        if (gameIds.Count == 0)
        {
            return new BatchUploadResult(0, 0, null);
        }

        string baseUrl = _configProvider().General.UploadApiUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new BatchUploadResult(0, gameIds.Count, "未配置服务器 API 地址");
        }

        string batchUrl = UploadUrlBuilder.BuildBatchUploadUrl(baseUrl);
        _logger?.LogInformation("批量上传开始: {Count} 场, url={Url}", gameIds.Count, batchUrl);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OverallTimeout);

        try
        {
            List<UploadPayload> payloads = await BuildPayloadsAsync(
                gameIds, currentSummonerPuuid, championNameResolver ?? _championNameResolver, timeout.Token)
                .ConfigureAwait(false);

            if (payloads.Count == 0)
            {
                _logger?.LogWarning("批量上传: 全部 {Count} 场对局详情获取失败", gameIds.Count);
                return new BatchUploadResult(0, gameIds.Count, $"全部 {gameIds.Count} 场对局详情获取失败");
            }

            BatchUploadResult result = await PostBatchesAsync(payloads, batchUrl, timeout.Token).ConfigureAwait(false);
            _logger?.LogInformation(
                "批量上传结束: 成功={Success}, 失败={Failed}, 请求={Requested}",
                result.SuccessCount, result.FailedCount, gameIds.Count);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BatchUploadResult(0, gameIds.Count, $"批量上传超时 ({OverallTimeout.TotalSeconds:0}s)");
        }
    }

    /// <summary>并发拉取详情并构建载荷，保持请求顺序（旧实现按 gameId 顺序产出）。</summary>
    private async Task<List<UploadPayload>> BuildPayloadsAsync(
        IReadOnlyList<long> gameIds,
        string? currentSummonerPuuid,
        Func<int, string>? championNameResolver,
        CancellationToken cancellationToken)
    {
        var results = new UploadPayload?[gameIds.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, gameIds.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = DetailConcurrency,
                CancellationToken = cancellationToken,
            },
            async (index, ct) =>
            {
                long gameId = gameIds[index];
                var (payload, error) = await _single.TryBuildPayloadAsync(
                    gameId, currentSummonerPuuid, championNameResolver, ct).ConfigureAwait(false);

                if (payload is null)
                {
                    _logger?.LogWarning("批量上传: 对局 {GameId} 详情获取失败: {Error}", gameId, error);
                    return;
                }

                results[index] = payload;
            }).ConfigureAwait(false);

        return results.OfType<UploadPayload>().ToList();
    }

    /// <summary>分批提交（每批 10 场）。连接失败即停止后续批次——继续只会重复同一个错误。</summary>
    private async Task<BatchUploadResult> PostBatchesAsync(
        List<UploadPayload> payloads,
        string batchUrl,
        CancellationToken cancellationToken)
    {
        int success = 0;
        int failed = 0;
        string? error = null;

        for (int offset = 0; offset < payloads.Count; offset += BatchSize)
        {
            int count = Math.Min(BatchSize, payloads.Count - offset);
            List<UploadPayload> chunk = payloads.GetRange(offset, count);
            int batchIndex = offset / BatchSize + 1;

            try
            {
                using var response = await UploadService.ExternalClient.Value
                    .PostAsJsonAsync(batchUrl, chunk, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    failed += chunk.Count;
                    error = $"HTTP {(int)response.StatusCode}";
                    _logger?.LogWarning("批量上传第 {Index} 批 HTTP 错误 {Status}", batchIndex, (int)response.StatusCode);
                    continue;
                }

                using var doc = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                var root = doc.RootElement;

                if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    JsonElement? data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                        ? d
                        : null;

                    success += ReadNumber(data, "successCount") ?? ReadNumber(data, "newMatches") ?? chunk.Count;
                    failed += ReadNumber(data, "failedCount") ?? 0;
                    continue;
                }

                failed += chunk.Count;
                error = $"服务器处理失败: {ReadString(root, "message") ?? "未知错误"}";
                _logger?.LogWarning("批量上传第 {Index} 批服务器返回失败: {Error}", batchIndex, error);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 连接层失败：后续批次不会突然成功，直接停
                failed += chunk.Count;
                error = $"连接失败: {ex.Message}";
                _logger?.LogError(ex, "批量上传第 {Index} 批连接失败，停止后续批次", batchIndex);
                break;
            }
        }

        return new BatchUploadResult(success, failed, error);
    }

    private static int? ReadNumber(JsonElement? source, string name) =>
        source is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static string? ReadString(JsonElement source, string name) =>
        source.ValueKind == JsonValueKind.Object
        && source.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
