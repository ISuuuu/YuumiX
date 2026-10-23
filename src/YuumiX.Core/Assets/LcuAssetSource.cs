using System.Net;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Assets;

/// <summary>
/// LCU 直取来源，对应原 <c>resolve_asset</c> 的第 1 步。
///
/// 为什么不复用 <see cref="ILcuTransport"/>：资源下载的并发画像与 API 完全不同——
/// 一屏图标是几十个并发小请求，若与战绩查询共用一个许可池（默认 5），
/// 结果只能是两边互相把对方挤到超时。这里自持一个 <see cref="HttpClient"/>，
/// 并发由 <see cref="AssetService"/> 统一限制在 8 路。
///
/// 与 API 传输共享的是**连接配置**（忽略自签证书、绕开系统代理、HTTP/1.1），
/// 由 <see cref="LcuHttpHandlerFactory"/> 收敛，不在这里重复写一遍。
/// </summary>
public sealed class LcuAssetSource : IAssetSource, IDisposable
{
    /// <summary>单次请求超时。比 CDN 宽：本机请求失败通常意味着客户端正在启动，愿意多等一会儿。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 双前缀降级的匹配串。LCU 的部分 <c>iconPath</c> 带 <c>/assets/assets/</c>，
    /// 这是历史数据形态，重试一次单前缀是原实现验证过的兜底。
    /// </summary>
    private const string DoubleAssetsSegment = "/assets/assets/";

    private readonly Func<LcuCredentials?> _credentialsProvider;
    private readonly Func<LcuCredentials, Uri> _baseAddressFactory;
    private readonly HttpClient _http;
    private readonly ILogger<LcuAssetSource>? _logger;

    /// <param name="credentialsProvider">当前凭据提供器（来自 <c>LcuConnection.Credentials</c>）；未连接时返回 <c>null</c>。</param>
    /// <param name="handler">自定义消息处理器（测试注入用）。</param>
    /// <param name="baseAddressFactory">基地址构造（测试注入用）；默认取 <see cref="LcuCredentials.BaseAddress"/>（https）。</param>
    /// <param name="timeout">单次请求超时。</param>
    /// <param name="logger">可选日志。</param>
    public LcuAssetSource(
        Func<LcuCredentials?> credentialsProvider,
        HttpMessageHandler? handler = null,
        Func<LcuCredentials, Uri>? baseAddressFactory = null,
        TimeSpan? timeout = null,
        ILogger<LcuAssetSource>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(credentialsProvider);

        _credentialsProvider = credentialsProvider;
        _baseAddressFactory = baseAddressFactory ?? (credentials => credentials.BaseAddress);
        _logger = logger;
        _http = new HttpClient(handler ?? LcuHttpHandlerFactory.Create(), disposeHandler: true)
        {
            Timeout = timeout ?? DefaultTimeout,

            // LCU 只讲 HTTP/1.1；不固定版本时协商异常的错误信息极难读
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    public string Name => "LCU";

    public async Task<AssetPayload?> TryGetAsync(string assetPath, CancellationToken cancellationToken = default)
    {
        // http(s) 地址归 CDragon 源处理，这里不抢
        if (AssetPathMapper.IsHttpPath(assetPath) || !AssetPathMapper.IsAllowedPath(assetPath))
        {
            return null;
        }

        var credentials = _credentialsProvider();
        if (credentials is null)
        {
            return null;
        }

        var path = AssetPathMapper.NormalizeLcuPath(assetPath);
        var payload = await FetchAsync(credentials, path, cancellationToken).ConfigureAwait(false);

        if (payload is null && path.Contains(DoubleAssetsSegment, StringComparison.Ordinal))
        {
            var singlePrefixPath = path.Replace(DoubleAssetsSegment, "/assets/", StringComparison.Ordinal);
            _logger?.LogDebug("LCU 双前缀资源 404，降级为单前缀重试: {Path}", singlePrefixPath);
            payload = await FetchAsync(credentials, singlePrefixPath, cancellationToken).ConfigureAwait(false);
        }

        return payload;
    }

    public void Dispose() => _http.Dispose();

    private async Task<AssetPayload?> FetchAsync(
        LcuCredentials credentials,
        string path,
        CancellationToken cancellationToken)
    {
        var url = new Uri(_baseAddressFactory(credentials), path);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", credentials.AuthorizationHeader);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug("LCU 未提供资源: {Path} → HTTP {StatusCode}", path, (int)response.StatusCode);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return null;
            }

            return new AssetPayload(bytes, ResolveContentType(response, path), Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // 客户端正在退出、端口已关都会走到这里，属于「此来源暂时不可用」
            _logger?.LogDebug(ex, "LCU 资源请求失败: {Path}", path);
            return null;
        }
    }

    /// <summary>只在响应头确实是 <c>image/*</c> 时采用它，否则按扩展名推断。</summary>
    private static string ResolveContentType(HttpResponseMessage response, string path)
    {
        var header = response.Content.Headers.ContentType?.MediaType;

        return !string.IsNullOrWhiteSpace(header)
               && header.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? header
            : AssetPathMapper.GuessContentType(path);
    }
}
