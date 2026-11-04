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
    private readonly TimeSpan _retryDelay;

    /// <summary>
    /// 释放信号。许可等待、请求超时与重试退避都链接它，使释放能立刻结束在途请求。
    /// 刻意不在 <see cref="Dispose"/> 里释放它：在途请求可能正往它上面注册回调，
    /// 释放会与注册竞争抛 <see cref="ObjectDisposedException"/>；它没有计时器，随传输层一并回收即可。
    /// </summary>
    private readonly CancellationTokenSource _disposeCts = new();

    private readonly CancellationToken _disposeToken;

    /// <summary>0 = 存活，1 = 已释放。锁外读取使用 <see cref="Volatile"/>。</summary>
    private int _disposed;

    /// <summary>在途 <see cref="SendAsync"/> 数量。释放时据此判定「是否还有操作可能触碰信号量」。</summary>
    private int _activeOperations;
    private readonly object _operationLock = new();

    /// <param name="baseAddress">形如 <c>https://127.0.0.1:50000/</c>。</param>
    /// <param name="authorizationHeader">完整的 Authorization 头值。</param>
    /// <param name="maxConcurrency">并发上限，对应配置项 <c>ApiConcurrencyNumber</c>。</param>
    /// <param name="requestTimeout">单次请求超时。</param>
    /// <param name="logger">可选日志。</param>
    /// <param name="handler">自定义消息处理器（测试注入用）；为 <c>null</c> 时使用生产配置。</param>
    /// <param name="retryDelay">重试间隔，默认 <see cref="DefaultRetryDelay"/>。</param>
    public LcuHttpTransport(
        Uri baseAddress,
        string authorizationHeader,
        int maxConcurrency = 5,
        TimeSpan? requestTimeout = null,
        ILogger<LcuHttpTransport>? logger = null,
        HttpMessageHandler? handler = null,
        TimeSpan? retryDelay = null)
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
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _permits = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _disposeToken = _disposeCts.Token;
        _http = new HttpClient(handler ?? LcuHttpHandlerFactory.Create(), disposeHandler: true)
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

        // 登记与释放状态使用同一把锁。Cancel 返回时，WaitAsync 仍可能在异步退出。
        if (!TryEnterOperation())
        {
            throw CreateDisposedException(request);
        }

        try
        {
            return await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    private async Task<LcuResponse> SendCoreAsync(LcuRequest request, CancellationToken cancellationToken)
    {
        if (!LcuApiCatalog.IsAllowed(request.Path))
        {
            throw new InvalidOperationException($"不允许的 LCU 路径: {request.Method} {request.Path}");
        }

        Exception? lastError = null;

        try
        {
            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                // 每轮重试单独取许可：退避时间不计入占用，否则超时请求会连锁拖垮其它调用。
                // 许可等待同时响应调用方取消和传输层释放。
                using var waitScope = CreateOperationScope(cancellationToken);
                bool acquired;
                try
                {
                    acquired = await _permits.WaitAsync(_permitTimeout, waitScope.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (IsDisposedCancellation(cancellationToken))
                {
                    throw CreateDisposedException(request);
                }

                if (!acquired)
                {
                    // 释放也可能让等待中的许可直接落空，此时按「已释放」表达而不是「许可超时」
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        throw CreateDisposedException(request);
                    }

                    throw new LcuTransportException(
                        $"LCU 并发许可等待超时: {request.Method} {request.Path}",
                        request.Method.Method,
                        request.Path);
                }

                try
                {
                    using var message = BuildMessage(request);
                    using var timeout = CreateOperationScope(cancellationToken);
                    timeout.CancelAfter(_requestTimeout);

                    using var response = await _http
                        .SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token)
                        .ConfigureAwait(false);

                    var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

                    return new LcuResponse((int)response.StatusCode, body);
                }
                catch (Exception ex) when (IsTransient(ex) && !cancellationToken.IsCancellationRequested)
                {
                    // 释放会中断在途请求，异常形态是「瞬时网络错误」。此时重试毫无意义：
                    // 直接归一为「已释放」并终止，既不多付一次退避，也不把语义降级成「重试耗尽」。
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        throw CreateDisposedException(request, ex);
                    }

                    lastError = ex;
                    _logger?.LogDebug(
                        "LCU 请求失败 ({Attempt}/{MaxRetries}): {Method} {Path} - {Error}",
                        attempt, MaxRetries, request.Method.Method, request.Path, ex.Message);
                }
                finally
                {
                    _permits.Release();
                }

                if (attempt < MaxRetries)
                {
                    await DelayBackoffAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (ObjectDisposedException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw CreateDisposedException(request, ex);
        }

        throw new LcuTransportException(
            $"LCU 请求失败（已重试 {MaxRetries} 次）: {request.Method.Method} {request.Path}",
            request.Method.Method,
            request.Path,
            lastError);
    }

    public void Dispose()
    {
        lock (_operationLock)
        {
            if (_disposed != 0)
            {
                return;
            }

            Volatile.Write(ref _disposed, 1);
            if (_activeOperations == 0)
            {
                _permits.Dispose();
            }
        }

        // 若还有请求，最后退出的请求负责释放信号量。连接清理无需等待请求退出。
        try
        {
            _disposeCts.Cancel();
        }
        finally
        {
            _http.Dispose();
        }
    }

    private bool TryEnterOperation()
    {
        lock (_operationLock)
        {
            if (_disposed != 0)
            {
                return false;
            }

            _activeOperations++;
            return true;
        }
    }

    private void ExitOperation()
    {
        lock (_operationLock)
        {
            _activeOperations--;
            if (_activeOperations == 0 && _disposed != 0)
            {
                _permits.Dispose();
            }
        }
    }

    /// <summary>
    /// 把「调用方取消」与「传输层释放」合并成一个等待令牌。
    /// 两者语义不同——前者继续传播 <see cref="OperationCanceledException"/>，
    /// 后者归一为 <see cref="LcuTransportException"/>——所以取消异常靠 <see cref="IsDisposedCancellation"/> 分流。
    /// </summary>
    private CancellationTokenSource CreateOperationScope(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeToken);

    /// <summary>
    /// 该取消是否来自传输层释放（而非调用方主动取消）。
    /// 调用方取消优先：两者同时发生时仍按取消传播，符合「调用方主动取消继续传播取消」的契约。
    /// </summary>
    private bool IsDisposedCancellation(CancellationToken cancellationToken) =>
        _disposeToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

    /// <summary>重试退避。等待期间同样响应释放，避免释放后还要空等完整个退避间隔。</summary>
    private async Task DelayBackoffAsync(LcuRequest request, CancellationToken cancellationToken)
    {
        using var scope = CreateOperationScope(cancellationToken);
        try
        {
            await Task.Delay(_retryDelay, scope.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsDisposedCancellation(cancellationToken))
        {
            throw CreateDisposedException(request);
        }
    }

    /// <summary>构造「传输层已释放」异常，供入口检查与在途失败两条路径复用。</summary>
    private static LcuTransportException CreateDisposedException(LcuRequest request, Exception? innerException = null) =>
        new(
            $"LCU 传输层已释放: {request.Method} {request.Path}",
            request.Method.Method,
            request.Path,
            innerException);

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
}
