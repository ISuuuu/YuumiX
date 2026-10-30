using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Upload;

/// <summary>
/// 对局上传队列（对应旧 upload.rs 的 <c>UploadQueue</c> + <c>upload_worker</c>）。
///
/// 三个必须存在的理由：
/// <list type="number">
///   <item>去重：同一局在结算瞬间会被 gameflow 抖动触发多次，没有集合守卫会重复提交；</item>
///   <item>串行：并发 POST 同一个上传接口没有意义，旧实现同样是单 worker 顺序消费；</item>
///   <item>落盘：失败必须写进本地暂存，否则这一局战绩在客户端关闭后永久丢失。</item>
/// </list>
/// </summary>
public sealed class UploadQueueService : IDisposable
{
    /// <summary>队列容量（旧 <c>mpsc::channel(128)</c>）。</summary>
    private const int ChannelCapacity = 128;

    /// <summary>
    /// 单局任务总超时（旧 upload_worker 的 35s）。必须显著大于
    /// <see cref="UploadService.RetryBudget"/>：旧实现踩过的坑是外层超时把内层 future 丢掉，
    /// 落盘分支永远执行不到，战绩静默丢失——这里超时也会补一次落盘。
    /// </summary>
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromSeconds(35);

    private readonly Channel<UploadQueueItem> _channel;
    private readonly HashSet<long> _enqueued = [];
    private readonly Lock _gate = new();
    private readonly UploadService _single;
    private readonly PendingUploadStore _store;
    private readonly Func<AppConfig> _configProvider;
    private readonly Func<Task<string?>> _puuidProvider;
    private readonly Func<int, string>? _championNameResolver;
    private readonly ILogger<UploadQueueService>? _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;

    private string? _cachedPuuid;
    private bool _disposed;

    /// <summary>
    /// 构造即启动后台 worker。
    /// </summary>
    /// <param name="single">单局上传服务（复用它的载荷构建与 POST 重试）。</param>
    /// <param name="store">失败落盘用的本地暂存。</param>
    /// <param name="configProvider">每次上传重新读配置，支持运行期改服务器地址。</param>
    /// <param name="puuidProvider">当前登录账号 puuid（首次查询后缓存）。</param>
    /// <param name="championNameResolver">英雄 ID → 名称。</param>
    public UploadQueueService(
        UploadService single,
        PendingUploadStore store,
        Func<AppConfig> configProvider,
        Func<Task<string?>> puuidProvider,
        Func<int, string>? championNameResolver = null,
        ILogger<UploadQueueService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(single);
        ArgumentNullException.ThrowIfNull(store);

        _single = single;
        _store = store;
        _configProvider = configProvider;
        _puuidProvider = puuidProvider;
        _championNameResolver = championNameResolver;
        _logger = logger;

        _channel = Channel.CreateBounded<UploadQueueItem>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        _worker = Task.Run(RunAsync);
    }

    /// <summary>把一局推进上传队列（去重；已在队列中则跳过）。</summary>
    public void Enqueue(long gameId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (gameId == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!_enqueued.Add(gameId))
            {
                _logger?.LogDebug("对局 {GameId} 已在上传队列中，跳过", gameId);
                return;
            }

            if (!_channel.Writer.TryWrite(new UploadQueueItem(gameId, null)))
            {
                // 写不进去说明队列满了：必须把去重标记撤回，否则这一局再也不会被重试
                _enqueued.Remove(gameId);
                _logger?.LogWarning("对局 {GameId} 推入上传队列失败：队列已满", gameId);
                return;
            }
        }

        _logger?.LogInformation("对局 {GameId} 已加入上传队列", gameId);
    }

    /// <summary>
    /// 把本地暂存里还没传完的对局重新入队（对应旧 <c>trigger_pending_retry</c>）。
    /// 超过重试上限的记录跳过——服务端永久拒绝的死信不该每局结束都空传一次。
    /// </summary>
    /// <returns>本次入队的条数。</returns>
    public int TriggerPendingRetry()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IReadOnlyList<PendingUploadItem> items = _store.Load();
        if (items.Count == 0)
        {
            return 0;
        }

        int enqueued = 0;
        foreach (PendingUploadItem item in items)
        {
            if (item.RetryCount >= PendingUploadStore.MaxRetryCount)
            {
                _logger?.LogDebug(
                    "对局 {GameId} 已尝试 {Count} 次，超过上限，跳过自动重试",
                    item.GameId, item.RetryCount);
                continue;
            }

            lock (_gate)
            {
                if (!_enqueued.Add(item.GameId))
                {
                    continue;
                }

                if (!_channel.Writer.TryWrite(new UploadQueueItem(item.GameId, item.Payload)))
                {
                    _enqueued.Remove(item.GameId);
                    _logger?.LogWarning("对局 {GameId} 重新入队失败：队列已满", item.GameId);
                    continue;
                }
            }

            enqueued++;
        }

        if (enqueued > 0)
        {
            _logger?.LogInformation("暂存队列重新入队 {Count}/{Total} 条未完成的对局上传", enqueued, items.Count);
        }

        return enqueued;
    }

    private async Task RunAsync()
    {
        _logger?.LogInformation("上传队列 Worker 已启动");
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out UploadQueueItem item))
                {
                    // 单条任务绝不能把 worker 打死：这里只有一个消费方，一旦跳出外层循环，
                    // 队列里剩下的与后续所有对局都既不上传也不落盘，日志只留一条
                    // 「Worker 异常退出」，之后整场会话再无任何上传。
                    try
                    {
                        await ProcessAsync(item).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "对局 {GameId} 的上传任务异常，已跳过并继续处理后续", item.GameId);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭信号
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "上传队列 Worker 异常退出");
        }

        _logger?.LogInformation("上传队列 Worker 已退出");
    }

    private async Task ProcessAsync(UploadQueueItem item)
    {
        long gameId = item.GameId;
        UploadPayload? payload = item.Payload;

        using var timeout = new CancellationTokenSource(TaskTimeout);
        try
        {
            // 配置读取同样放在 try 内：读配置本身也会抛（文件被占用、反序列化失败），
            // 留在 try 外会让单条记录的异常直接冲出 ProcessAsync。
            var config = _configProvider();
            if (string.IsNullOrWhiteSpace(config.General.UploadApiUrl))
            {
                _logger?.LogWarning("对局 {GameId} 跳过上传: 未配置上传 API 地址", gameId);
                return;
            }

            if (payload is null)
            {
                string? puuid = await ResolvePuuidAsync().ConfigureAwait(false);
                string? lastError = null;

                // 结算初期 LCU 对局详情生成可能有短暂延迟，短间隔重试 5 次
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    var (built, error) = await _single.TryBuildPayloadAsync(
                        gameId, puuid, _championNameResolver, timeout.Token).ConfigureAwait(false);

                    if (built is not null)
                    {
                        payload = built;
                        break;
                    }

                    lastError = error;
                    if (attempt < 4)
                    {
                        await Task.Delay(1500, timeout.Token).ConfigureAwait(false);
                    }
                }

                if (payload is null)
                {
                    // LCU 暂时拉不到详情时退回本地暂存（旧逻辑）：结算瞬间接口抖一下是常态
                    payload = _store.Load().FirstOrDefault(x => x.GameId == gameId)?.Payload;
                    if (payload is null)
                    {
                        _logger?.LogWarning("对局 {GameId} 跳过上传: {Error}", gameId, lastError ?? "拉取详情失败");
                        return;
                    }

                    _logger?.LogInformation("LCU 暂无法获取详情，使用本地暂存的对局 {GameId} 数据上传", gameId);
                }
            }

            string url = UploadUrlBuilder.BuildUploadUrl(_configProvider().General.UploadApiUrl);
            var (outcome, message) = await _single.PostPayloadAsync(payload, url, gameId, timeout.Token)
                .ConfigureAwait(false);

            if (outcome is UploadOutcome.New or UploadOutcome.Exists)
            {
                _store.Remove(gameId);
                _logger?.LogInformation("对局 {GameId} 上传完成: {Outcome}", gameId, outcome);
            }
            else
            {
                _logger?.LogWarning("对局 {GameId} 上传失败，写入本地暂存: {Error}", gameId, message);
                _store.AddOrUpdate(payload, message);
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("对局 {GameId} 上传超时 ({Seconds}s)", gameId, TaskTimeout.TotalSeconds);
            if (payload is not null)
            {
                _store.AddOrUpdate(payload, $"上传超时 ({TaskTimeout.TotalSeconds:0}s)");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "对局 {GameId} 上传处理失败", gameId);

            // 非超时的异常同样要落盘：这条记录已经出队，只在内存里丢日志等于永久丢这一局
            // （客户端一关就没了）。落盘自身失败由 RunAsync 的兜底接住，不会打死 worker。
            if (payload is not null)
            {
                _store.AddOrUpdate(payload, ex.Message);
            }
        }
        finally
        {
            lock (_gate)
            {
                _enqueued.Remove(gameId);
            }
        }
    }

    /// <summary>当前账号 puuid：客户端一次登录内不变，查一次就够（旧 fetch_or_cache_puuid）。</summary>
    private async Task<string?> ResolvePuuidAsync()
    {
        if (!string.IsNullOrEmpty(_cachedPuuid))
        {
            return _cachedPuuid;
        }

        try
        {
            _cachedPuuid = await _puuidProvider().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "获取当前召唤师 puuid 失败，上传载荷内层参与者将退化为首位");
        }

        return _cachedPuuid;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _channel.Writer.TryComplete();

        try
        {
            // 给在途任务一个收尾的机会，但不允许把退出流程卡住
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // worker 内部已记日志
        }

        _shutdown.Dispose();
    }
}

/// <summary>队列任务：要么只有 gameId（需要现拉详情），要么带上已落盘的载荷直接重发。</summary>
internal readonly record struct UploadQueueItem(long GameId, UploadPayload? Payload);
