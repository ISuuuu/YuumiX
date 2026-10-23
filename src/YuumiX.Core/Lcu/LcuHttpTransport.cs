using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Lcu;

/// <summary>传输层最终失败（重试耗尽、并发许可超时）。响应级错误不抛异常，走 <see cref="LcuResponse.StatusCode"/>。</summary>
public sealed class LcuTransportException : Exception
{
    public LcuTransportException(string message, string method, string path, Exception? innerException = null)
        : base(message, innerException)
    {
        Method = method;
        Path = path;
    }

    public string Method { get; }

    public string Path { get; }
}

/// <summary>
/// LCU 的 HTTPS 传输实现，对应原 <c>client.rs</c> 的 <c>lcu_request_lane</c>。
///
/// 四条必须保住的既有行为：
/// <list type="number">
///   <item><b>路径白名单</b>：不在 <see cref="LcuApiCatalog"/> 内的路径直接拒绝；</item>
///   <item><b>并发限流</b>：<see cref="SemaphoreSlim"/> 按配置的并发数（1–32）放行，
///         等许可超过 <see cref="DefaultPermitTimeout"/> 就放弃，避免慢请求占死池子；</item>
///   <item><b>传输层重试</b>：只有网络异常才重试（<see cref="MaxRetries"/> 次、间隔
///         <see cref="DefaultRetryDelay"/>）。HTTP 4xx/5xx 不重试——重试一个 404 没有意义，
///         而且会把选人阶段的写操作挤到后面；</item>
///   <item><b>退避期间释放许可</b>：否则几路超时请求就能把池子占满。</item>
/// </list>
///
/// 与 Rust 的一处有意差异：非 2xx 不抛异常而是原样返回 <see cref="LcuResponse"/>。
/// 404 在 LCU 上是业务常态（非选人阶段查选人会话、按 puuid 查未命名召唤师），
/// 用异常表达会让调用方到处写 try/catch，也丢失了「已拿到响应」这个事实。
/// </summary>
public sealed class LcuHttpTransport : ILcuTransport, IDisposable
{
    /// <summary>传输层重试次数。</summary>
    public const int MaxRetries = 3;

    /// <summary>重试间隔。</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>等待并发许可的上限。超时说明池子被慢请求占满，继续等只会错过时间窗。</summary>
    public static readonly TimeSpan DefaultPermitTimeout = TimeSpan.FromSeconds(8);

    /// <summary>单次请求超时。</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);

    private readonly Uri _baseAddress;
    private readonly string _authorizationHeader;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _permits;
    private readonly ILogger<LcuHttpTransport>? _logger;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _permitTimeout;

    private bool _disposed;

    /// <param name="baseAddress">形如 <c>https://127.0.0.1:50000/</c>。</param>
    /// <param name="authorizationHeader">完整的 Authorization 头值。</param>
    /// <param name="maxConcurrency">并发上限，对应配置项 <c>ApiConcurrencyNumber</c>。</param>
    /// <param name="requestTimeout">单次请求超时。</param>
    /// <param name="logger">可选日志。</param>
    /// <param name="handler">自定义消息处理器（测试注入用）；为 <c>null</c> 时使用生产配置。</param>
    public LcuHttpTransport(
        Uri baseAddress,
        string authorizationHeader,
        int maxConcurrency = 5,
        TimeSpan? requestTimeout = null,
        ILogger<LcuHttpTransport>? logger = null,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationHeader);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxConcurrency, 32);

        _baseAddress = baseAddress;
        _authorizationHeader = authorizationHeader;
        _logger = logger;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _permitTimeout = DefaultPermitTimeout;
        _permits = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _http = new HttpClient(handler ?? CreateProductionHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, // 超时由每次请求的 CancellationToken 控制，避免双重超时
        };
    }

    /// <summary>按凭据构造。生产代码应走这个入口，以保留证书忽略与绕过代理的配置。</summary>
    public static LcuHttpTransport ForCredentials(
        LcuCredentials credentials,
        int maxConcurrency = 5,
        ILogger<LcuHttpTransport>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return new LcuHttpTransport(credentials.BaseAddress, credentials.AuthorizationHeader, maxConcurrency, logger: logger);
    }

    public async Task<LcuResponse> SendAsync(LcuRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!LcuApiCatalog.IsAllowed(request.Path))
        {
            throw new InvalidOperationException($"不允许的 LCU 路径: {request.Method} {request.Path}");
        }

        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            // 每轮重试单独取许可：退避时间不计入占用，否则超时请求会连锁拖垮其它调用
            if (!await _permits.WaitAsync(_permitTimeout, cancellationToken).ConfigureAwait(false))
            {
                throw new LcuTransportException(
                    $"LCU 并发许可等待超时: {request.Method} {request.Path}",
                    request.Method.Method,
                    request.Path);
            }

            try
            {
                using var message = BuildMessage(request);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_requestTimeout);

                using var response = await _http
                    .SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token)
                    .ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

                return new LcuResponse((int)response.StatusCode, body);
            }
            catch (Exception ex) when (IsTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
                _logger?.LogDebug(
                    "LCU 请求失败 ({Attempt}/{MaxRetries}): {Method} {Path} - {Error}",
                    attempt, MaxRetries, request.Method.Method, request.Path, ex.Message);

                if (attempt < MaxRetries)
                {
                    await Task.Delay(DefaultRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _permits.Release();
            }
        }

        throw new LcuTransportException(
            $"LCU 请求失败（已重试 {MaxRetries} 次）: {request.Method.Method} {request.Path}",
            request.Method.Method,
            request.Path,
            lastError);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
        _permits.Dispose();
    }

    private HttpRequestMessage BuildMessage(LcuRequest request)
    {
        var message = new HttpRequestMessage(request.Method, new Uri(_baseAddress, request.Path));

        // LCU 只讲 HTTP/1.1；不显式固定版本，遇到协商异常时错误信息会非常难读
        message.Version = HttpVersion.Version11;
        message.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        message.Headers.TryAddWithoutValidation("Authorization", _authorizationHeader);

        if (request.JsonBody is not null)
        {
            message.Content = new StringContent(request.JsonBody, Encoding.UTF8, "application/json");
        }

        return message;
    }

    /// <summary>
    /// 只有网络层异常才值得重试。HTTP 状态码（含 5xx）不重试：
    /// LCU 的 5xx 几乎都是「当前阶段不允许该操作」，重试三次只会让 UI 卡 1.5 秒再报同样的错。
    /// </summary>
    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException;

    /// <summary>
    /// 生产处理器。两处非默认配置都有明确理由：
    /// <list type="bullet">
    ///   <item><b>忽略证书</b>：LCU 用自签证书，等价于原实现的 <c>danger_accept_invalid_certs</c>；</item>
    ///   <item><b>UseProxy = false</b>：用户可能配置了系统代理（本机就是 <c>127.0.0.1:7897</c>），
    ///         回环地址走代理必然连不上。原实现的 <c>.no_proxy()</c> 是同一条约束。</item>
    /// </list>
    /// </summary>
    private static HttpMessageHandler CreateProductionHandler()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        return handler;
    }
}
