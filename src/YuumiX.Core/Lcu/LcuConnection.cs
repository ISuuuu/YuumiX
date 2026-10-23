using YuumiX.Core.Common;
using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Lcu;

/// <summary>连接层对外暴露的状态。</summary>
public enum LcuConnectionStatus
{
    /// <summary>没有可用连接。区别于 <see cref="Connecting"/>：此时连凭据都拿不到，或探活未通过。</summary>
    Disconnected,

    /// <summary>已拿到凭据并正在探活，尚未建立事件流。</summary>
    Connecting,

    /// <summary>事件流已建立，可以接收 LCU 事件。</summary>
    Connected,
}

/// <summary>
/// 一次状态快照。<see cref="Reason"/> 是面向日志与排障的中文说明，不用于程序判断——
/// 判断一律看 <see cref="Status"/>，避免文案调整破坏调用方逻辑。
/// </summary>
public sealed record LcuConnectionState(LcuConnectionStatus Status, LcuCredentials? Credentials, string? Reason)
{
    public static LcuConnectionState Disconnected(string reason) => new(LcuConnectionStatus.Disconnected, null, reason);

    public static LcuConnectionState Connecting(LcuCredentials credentials) =>
        new(LcuConnectionStatus.Connecting, credentials, "正在探活");

    public static LcuConnectionState Connected(LcuCredentials credentials) =>
        new(LcuConnectionStatus.Connected, credentials, null);
}

/// <summary>
/// LCU 连接聚合：把「进程发现 → 探活 → 建传输 → 订阅事件」串成一条顺序循环，
/// 对应原项目 <c>monitor.rs</c> 的监视循环 + <c>ws.rs</c> 的连接管理的合并形态。
///
/// 为什么用一条顺序循环而不是若干个并发循环：
/// <list type="bullet">
///   <item>客户端重启会换端口与 token，任何「重连到旧端点」的循环都注定无效；
///         顺序循环每次迭代都重新发现，端口变化天然被覆盖；</item>
///   <item>探活本身只有毫秒级耗时，重连延迟比固定 2 秒的盲目重试更短；</item>
///   <item>单线程状态推进意味着状态发布顺序恒定（Disconnected → Connecting → Connected），
///         UI 不会看到「Connected 之后又冒出一个更早的 Connecting」。</item>
/// </list>
///
/// 状态经 <see cref="States"/> 暴露，事件经 <see cref="Events"/> 暴露。
/// 两个流都是**稳定实例**：重连时内部的 <see cref="LcuEventStream"/> 会被替换，
/// 但订阅方只订阅一次，不需要感知替换。
/// </summary>
public sealed class LcuConnection : IDisposable
{
    /// <summary>无客户端、探活失败、连接断开后的统一重试间隔。</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>与配置项 <c>ApiConcurrencyNumber</c> 的缺省值一致。</summary>
    public const int DefaultMaxConcurrency = 5;

    private readonly LcuProcessFinder _finder;
    private readonly TimeSpan _retryDelay;
    private readonly ILogger<LcuConnection>? _logger;
    private readonly Func<LcuCredentials, ILcuTransport> _transportFactory;
    private readonly Func<LcuCredentials, LcuEventStream> _eventStreamFactory;
    private readonly EventStream<LcuConnectionState> _states;
    private readonly EventStream<LcuEvent> _events;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();

    private LcuConnectionState _current = LcuConnectionState.Disconnected("尚未启动");
    private LcuCredentials? _credentials;
    private ILcuTransport? _transport;
    private Task? _loop;
    private int _disposed;

    /// <param name="finder">进程发现器。</param>
    /// <param name="maxConcurrency">HTTP 并发上限，对应配置项 <c>ApiConcurrencyNumber</c>。</param>
    /// <param name="loggerFactory">日志工厂；内部为传输层与事件流各自创建类别。</param>
    /// <param name="transportFactory">传输构造（测试注入用）；为 <c>null</c> 时走 <see cref="LcuHttpTransport"/></param>
    /// <param name="eventStreamFactory">事件流构造（测试注入用）；为 <c>null</c> 时走 <see cref="LcuEventStream"/></param>
    /// <param name="retryDelay">重试间隔（测试注入用，避免用例等待秒级）。</param>
    public LcuConnection(
        LcuProcessFinder finder,
        int maxConcurrency = DefaultMaxConcurrency,
        ILoggerFactory? loggerFactory = null,
        Func<LcuCredentials, ILcuTransport>? transportFactory = null,
        Func<LcuCredentials, LcuEventStream>? eventStreamFactory = null,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(finder);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxConcurrency, 32);

        _finder = finder;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _logger = loggerFactory?.CreateLogger<LcuConnection>();

        _transportFactory = transportFactory ?? (credentials => LcuHttpTransport.ForCredentials(
            credentials,
            maxConcurrency,
            loggerFactory?.CreateLogger<LcuHttpTransport>()));

        _eventStreamFactory = eventStreamFactory ?? (credentials => LcuEventStream.ForCredentials(
            credentials,
            loggerFactory?.CreateLogger<LcuEventStream>()));

        _states = new EventStream<LcuConnectionState>(ex => _logger?.LogWarning(ex, "连接状态观察者抛出异常，已隔离"));
        _events = new EventStream<LcuEvent>(ex => _logger?.LogWarning(ex, "连接事件观察者抛出异常，已隔离"));
    }

    /// <summary>连接状态流。</summary>
    public EventStream<LcuConnectionState> States => _states;

    /// <summary>LCU 事件流。跨重连保持同一实例，订阅方无需重复订阅。</summary>
    public EventStream<LcuEvent> Events => _events;

    /// <summary>最近一次发布的状态。</summary>
    public LcuConnectionState CurrentState
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>当前传输层；未连接时为 <c>null</c>。重连后会指向新实例，调用方不要缓存。</summary>
    public ILcuTransport? Transport
    {
        get
        {
            lock (_gate)
            {
                return _transport;
            }
        }
    }

    /// <summary>当前凭据；未连接时为 <c>null</c>。SGP（观战）等需要 token 的功能从这里取。</summary>
    public LcuCredentials? Credentials
    {
        get
        {
            lock (_gate)
            {
                return _credentials;
            }
        }
    }

    /// <summary>
    /// 在后台启动连接循环。幂等：重复调用不会起第二个循环。
    /// 循环监听进程关闭令牌（<see cref="AppServices.ShutdownToken"/>），应用退出时自动结束。
    /// </summary>
    public void Start(CancellationToken shutdownToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _loop = Task.Run(async () =>
            {
                try
                {
                    await RunAsync(shutdownToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 正常关闭
                }
                catch (Exception ex)
                {
                    // 循环自身不该抛异常；真抛了必须留痕，否则表现为「状态永远停在 Connecting」
                    _logger?.LogError(ex, "LCU 连接循环异常终止");
                }
            });
        }
    }

    /// <summary>
    /// 连接主循环：发现 → 探活 → 建流 → 运行，直到令牌取消。
    /// 每个失败分支都会发布 <see cref="LcuConnectionState.Disconnected"/> 并等待后重试，
    /// 不会把「连不上」变成忙循环。
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var token = linked.Token;

        while (!token.IsCancellationRequested)
        {
            var credentials = TryFindCredentials();
            if (credentials is null)
            {
                Publish(LcuConnectionState.Disconnected("未检测到英雄联盟客户端"));
                if (!await DelayAsync(_retryDelay, token).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            Publish(LcuConnectionState.Connecting(credentials));

            var transport = EnsureTransport(credentials);
            if (!await ProbeAsync(transport, token).ConfigureAwait(false))
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Publish(LcuConnectionState.Disconnected("客户端尚未就绪"));
                if (!await DelayAsync(_retryDelay, token).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            await RunSessionAsync(credentials, token).ConfigureAwait(false);

            if (!await DelayAsync(_retryDelay, token).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>跑完一次事件流会话（连接 → 断开）。返回即表示已断开或已取消。</summary>
    private async Task RunSessionAsync(LcuCredentials credentials, CancellationToken token)
    {
        var stream = _eventStreamFactory(credentials);

        try
        {
            // 转发而非直接暴露 stream.Events：重连换实例时订阅方不应重新订阅
            using var subscription = stream.Events.Subscribe(evt => _events.Publish(evt));

            Publish(LcuConnectionState.Connected(credentials));

            await stream.RunAsync(token).ConfigureAwait(false);

            _logger?.LogInformation("LCU 事件流已结束: {Credentials}", credentials);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 取消不是「断开」的语义，但状态不能停在 Connected——UI 会据此以为仍可收发事件。
            // Dispose 已先完成流的情况下 Publish 是空操作，无需额外判断。
            Publish(LcuConnectionState.Disconnected("已取消"));
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LCU 事件流异常终止: {Credentials}", credentials);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);

            // 客户端重启后安装目录可能变化，清掉路径缓存让下一轮重新定位
            _finder.Reset();
        }

        Publish(LcuConnectionState.Disconnected("连接已断开"));
    }

    /// <summary>
    /// 按凭据取传输层。凭据未变则复用，避免每轮重试都新建 <see cref="HttpClient"/> 与连接池。
    /// </summary>
    private ILcuTransport EnsureTransport(LcuCredentials credentials)
    {
        lock (_gate)
        {
            if (_transport is not null && _credentials == credentials)
            {
                return _transport;
            }

            _transport?.Dispose();
            _transport = _transportFactory(credentials);
            _credentials = credentials;
            return _transport;
        }
    }

    /// <summary>
    /// 探活：只有 2xx 才算就绪。端口开放不代表 LCU 可用——客户端启动过程中端口先开、
    /// 接口后通，直接建 WS 会拿到 404/401，随后又被误判成「token 失效」。
    /// </summary>
    private async Task<bool> ProbeAsync(ILcuTransport transport, CancellationToken token)
    {
        try
        {
            var response = await transport
                .SendAsync(LcuRequest.Get(LcuApiCatalog.ReadinessPath), token)
                .ConfigureAwait(false);

            if (response.IsSuccess)
            {
                return true;
            }

            _logger?.LogDebug("LCU 探活未就绪: HTTP {StatusCode}", response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            // 连接被拒绝、超时、以及传输层重试耗尽都归为「未就绪」，属于启动期的正常现象
            _logger?.LogDebug(ex, "LCU 探活失败");
            return false;
        }
    }

    private LcuCredentials? TryFindCredentials()
    {
        try
        {
            return _finder.TryFind();
        }
        catch (Exception ex)
        {
            // 进程枚举可能因权限或进程竞态失败，不能因此让整个循环终止
            _logger?.LogWarning(ex, "LCU 进程发现失败");
            return null;
        }
    }

    private void Publish(LcuConnectionState state)
    {
        LcuConnectionState previous;

        lock (_gate)
        {
            if (_current == state)
            {
                return;
            }

            previous = _current;
            _current = state;
        }

        _logger?.LogInformation(
            "LCU 连接状态: {Previous} → {Current}（{Reason}）",
            previous.Status,
            state.Status,
            state.Reason ?? "-");

        _states.Publish(state);
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 只发取消信号，不 Dispose 令牌源也不等待循环：
        // 循环可能阻塞在 ReceiveAsync 上，等它会让窗口关闭卡住；而 Dispose 令牌源
        // 会让循环里的注册抛出 ObjectDisposedException，反而污染退出路径。
        _stop.Cancel();

        var loop = _loop;
        if (loop is null)
        {
            CleanupTransport();
        }
        else
        {
            // 循环退出后再释放传输层，避免与进行中的请求竞争
            _ = loop.ContinueWith(_ => CleanupTransport(), TaskScheduler.Default);
        }

        _states.Complete();
        _events.Complete();
    }

    private void CleanupTransport()
    {
        lock (_gate)
        {
            _transport?.Dispose();
            _transport = null;
            _credentials = null;
        }
    }
}
