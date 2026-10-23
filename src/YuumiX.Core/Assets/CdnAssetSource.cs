using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Assets;

/// <summary>
/// CDragon 兜底来源，对应原 <c>resolve_asset</c> 的第 2 步。
///
/// 与 <see cref="LcuAssetSource"/> 的三点差异都源于「访问的是公网」：
/// <list type="bullet">
///   <item>走系统代理（用户可能靠代理访问境外 CDN），不做回环直连那套配置；</item>
///   <item>校验证书（CDN 是有效证书，忽略校验只会把劫持风险白白引入）；</item>
///   <item>超时 10 秒（原 <c>cdn_client()</c> 的取值），比 LCU 的 20 秒短——
///         慢 CDN 会拖住整个列表的首次渲染。</item>
/// </list>
/// </summary>
public sealed class CdnAssetSource : IAssetSource, IDisposable
{
    /// <summary>单次请求超时。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly string _cdnBase;
    private readonly HttpClient _http;
    private readonly ILogger<CdnAssetSource>? _logger;

    /// <param name="cdnBase">CDragon 根地址；测试可指向本地假服务端。</param>
    /// <param name="handler">自定义消息处理器（测试注入用）。</param>
    /// <param name="timeout">单次请求超时。</param>
    /// <param name="logger">可选日志。</param>
    public CdnAssetSource(
        string? cdnBase = null,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null,
        ILogger<CdnAssetSource>? logger = null)
    {
        _cdnBase = (cdnBase ?? AssetPathMapper.DefaultCdnBase).TrimEnd('/');
        _logger = logger;
        _http = new HttpClient(handler ?? CreateProductionHandler(), disposeHandler: true)
        {
            Timeout = timeout ?? DefaultTimeout,
        };
    }

    public string Name => "CDragon";

    public async Task<AssetPayload?> TryGetAsync(string assetPath, CancellationToken cancellationToken = default)
    {
        if (!AssetPathMapper.IsAllowedPath(assetPath))
        {
            return null;
        }

        var url = AssetPathMapper.ToCdnUrl(assetPath, _cdnBase);

        try
        {
            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug("CDragon 未提供资源: {Url} → HTTP {StatusCode}", url, (int)response.StatusCode);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return null;
            }

            return new AssetPayload(bytes, ResolveContentType(response, assetPath), Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // 离线、DNS 失败、超时都归为「此来源不可用」，交给链路上的下一个来源/上层兜底
            _logger?.LogDebug(ex, "CDragon 请求失败: {Url}", url);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// 只在响应头确实是 <c>image/*</c> 时采用它，否则按扩展名推断。
    /// CDragon 的错误页也会带 content-type，直接采信会让「一张图变成一段 HTML」写进缓存。
    /// </summary>
    private static string ResolveContentType(HttpResponseMessage response, string assetPath)
    {
        var header = response.Content.Headers.ContentType?.MediaType;

        return !string.IsNullOrWhiteSpace(header)
               && header.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? header
            : AssetPathMapper.GuessContentType(assetPath);
    }

    /// <summary>
    /// 生产处理器：保留系统代理（访问公网 CDN 需要），不忽略证书。
    /// 刻意不设 <c>UseProxy = false</c>——那是只适用于 127.0.0.1 的规则。
    /// </summary>
    private static HttpMessageHandler CreateProductionHandler() => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = true,
    };
}
