using Microsoft.Extensions.Logging;
using YuumiX.Core.Features.GameLifecycle;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Realtime;
using YuumiX.Core.Updates;
using YuumiX.Core.Upload;

namespace YuumiX.Core.Runtime;

/// <summary>
/// Core 层后台任务协调器：统一托管与 UI 无关的后台初始化与事件响应任务，
/// 避免这些纯 Core 逻辑散落在应用外壳（<c>AppHost</c>）中。
///
/// 托管的任务包括：
/// <list type="number">
///   <item>订阅 <see cref="GameLifecycleService.GameEnded"/>：立即清战绩列表缓存，延迟后二次清缓存并拉取/入队自动上传、触发暂存重试；</item>
///   <item>启动延迟后触发一次 <see cref="UploadQueueService.TriggerPendingRetry"/> 恢复上次未上传对局；</item>
///   <item>后台清理过期更新包（<see cref="UpdateService.CleanupDownloadCache"/>）；</item>
///   <item>启动应用 SignalR 远程反代配置（<see cref="SignalrService.ApplyConfigAsync"/>）。</item>
/// </list>
/// </summary>
public sealed class AppBackgroundCoordinator : IDisposable
{
    private static readonly TimeSpan DefaultGameEndedUploadDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultStartupPendingRetryDelay = TimeSpan.FromSeconds(5);

    private readonly GameLifecycleService _lifecycle;
    private readonly IMatchService _matchService;
    private readonly ISummonerService _summonerService;
    private readonly UploadQueueService _uploadQueue;
    private readonly UpdateService _updateService;
    private readonly SignalrService _signalrService;
    private readonly ILogger<AppBackgroundCoordinator>? _logger;
    private readonly TimeSpan _gameEndedUploadDelay;
    private readonly TimeSpan _startupPendingRetryDelay;

    private IDisposable? _gameEndedSubscription;
    private int _started;
    private bool _disposed;

    public AppBackgroundCoordinator(
        GameLifecycleService lifecycle,
        IMatchService matchService,
        ISummonerService summonerService,
        UploadQueueService uploadQueue,
        UpdateService updateService,
        SignalrService signalrService,
        ILogger<AppBackgroundCoordinator>? logger = null)
        : this(
            lifecycle,
            matchService,
            summonerService,
            uploadQueue,
            updateService,
            signalrService,
            DefaultGameEndedUploadDelay,
            DefaultStartupPendingRetryDelay,
            logger)
    {
    }

    internal AppBackgroundCoordinator(
        GameLifecycleService lifecycle,
        IMatchService matchService,
        ISummonerService summonerService,
        UploadQueueService uploadQueue,
        UpdateService updateService,
        SignalrService signalrService,
        TimeSpan gameEndedUploadDelay,
        TimeSpan startupPendingRetryDelay,
        ILogger<AppBackgroundCoordinator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(matchService);
        ArgumentNullException.ThrowIfNull(summonerService);
        ArgumentNullException.ThrowIfNull(uploadQueue);
        ArgumentNullException.ThrowIfNull(updateService);
        ArgumentNullException.ThrowIfNull(signalrService);

        _lifecycle = lifecycle;
        _matchService = matchService;
        _summonerService = summonerService;
        _uploadQueue = uploadQueue;
        _updateService = updateService;
        _signalrService = signalrService;
        _gameEndedUploadDelay = gameEndedUploadDelay;
        _startupPendingRetryDelay = startupPendingRetryDelay;
        _logger = logger;
    }

    /// <summary>
    /// 启动所有 Core 层后台任务，并全部绑定到 <paramref name="shutdownToken"/>。
    /// 幂等：重复调用仅首次生效。
    /// </summary>
    public void Start(CancellationToken shutdownToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        // 1. 订阅对局结束：同步清列表缓存，延迟 2s 二次清缓存并自动入队上传 + 重试暂存
        _gameEndedSubscription = _lifecycle.GameEnded.Subscribe(_ => OnGameEnded(shutdownToken));

        // 2. 启动 5s 后触发一次暂存上传恢复
        _ = RunStartupPendingRetryAsync(shutdownToken);

        // 3. 后台清理过期更新包
        _ = RunUpdateCacheCleanupAsync(shutdownToken);

        // 4. 启动应用 SignalR 配置
        _ = RunSignalrStartupAsync(shutdownToken);
    }

    private void OnGameEnded(CancellationToken shutdownToken)
    {
        // 对局一结束立即使战绩列表缓存失效：与页面挂载无关，避免后续首次读取拿到旧列表。
        _matchService.InvalidateHistoryCache();

        if (shutdownToken.IsCancellationRequested)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (_gameEndedUploadDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_gameEndedUploadDelay, shutdownToken).ConfigureAwait(false);
                }

                // 二次失效：延迟期间若有页面抢先取数可能缓存了不含本局的残缺列表。
                _matchService.InvalidateHistoryCache();

                long targetGameId = _lifecycle.LastEndedGameId ?? 0;
                if (targetGameId == 0)
                {
                    var profile = await _summonerService.GetCurrentSummonerAsync(shutdownToken).ConfigureAwait(false);
                    if (profile is not null)
                    {
                        var page = await _matchService.GetMatchHistoryAsync(profile.Puuid, 0, 0, shutdownToken).ConfigureAwait(false);
                        if (page.Games.Count > 0)
                        {
                            targetGameId = page.Games[0].GameId;
                        }
                    }
                }

                if (targetGameId > 0)
                {
                    _uploadQueue.Enqueue(targetGameId);
                    _uploadQueue.TriggerPendingRetry();
                }
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "对局结束后的自动上传失败");
            }
        }, shutdownToken);
    }

    private Task RunStartupPendingRetryAsync(CancellationToken shutdownToken) =>
        Task.Run(async () =>
        {
            try
            {
                if (_startupPendingRetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_startupPendingRetryDelay, shutdownToken).ConfigureAwait(false);
                }

                int pending = _uploadQueue.TriggerPendingRetry();
                if (pending > 0)
                {
                    _logger?.LogInformation("启动恢复：{Count} 场对局重新进入上传队列", pending);
                }
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "启动恢复暂存上传失败");
            }
        }, shutdownToken);

    private Task RunUpdateCacheCleanupAsync(CancellationToken shutdownToken) =>
        Task.Run(() =>
        {
            if (shutdownToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                _updateService.CleanupDownloadCache();
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "启动清理过期更新包失败");
            }
        }, shutdownToken);

    private Task RunSignalrStartupAsync(CancellationToken shutdownToken) =>
        Task.Run(async () =>
        {
            if (shutdownToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await _signalrService.ApplyConfigAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "启动 SignalR 远程反代服务失败");
            }
        }, shutdownToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gameEndedSubscription?.Dispose();
        _gameEndedSubscription = null;
    }
}
