using Microsoft.Extensions.Logging;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Features.Tools;

public enum ClientLaunchPhase
{
    Idle = 0,
    Launching = 1,
    WaitingForConnect = 2,
}

public sealed record ClientLaunchStatus(
    ClientLaunchPhase Phase,
    string? LastError = null,
    int ErrorSequence = 0);

/// <summary>
/// 应用级客户端启动协调器：管理「正在启动… → 等待客户端连接」状态机、防重复启动门闩、
/// 连接超时兜底与外部手动启动后的自动收敛。
/// </summary>
public sealed class ClientLaunchCoordinator : IDisposable
{
    private static readonly TimeSpan DefaultClientTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DefaultWeGameTimeout = TimeSpan.FromSeconds(90);

    private readonly Func<IReadOnlyList<string>, string?, string?> _launchFunc;
    private readonly Func<bool> _isConnectedFunc;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;
    private readonly IDisposable? _lcuSubscription;
    private readonly Lock _gate = new();

    private ClientLaunchStatus _status = new(ClientLaunchPhase.Idle);
    private CancellationTokenSource? _waitTimeoutCts;
    private int _errorSequence;
    private bool _disposed;

    public ClientLaunchCoordinator(
        LolClientLauncher launcher,
        LcuConnection lcu,
        TimeProvider? timeProvider = null,
        ILogger<ClientLaunchCoordinator>? logger = null)
        : this(
            (paths, wegamePath) => launcher.Launch(paths, wegamePath),
            () => lcu.CurrentState.Status == LcuConnectionStatus.Connected,
            timeProvider,
            logger)
    {
        _lcuSubscription = lcu.States.Subscribe(new ConnectionObserver(this));
    }

    internal ClientLaunchCoordinator(
        Func<IReadOnlyList<string>, string?, string?> launchFunc,
        Func<bool> isConnectedFunc,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        _launchFunc = launchFunc;
        _isConnectedFunc = isConnectedFunc;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public ClientLaunchStatus CurrentStatus
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public event Action<ClientLaunchStatus>? StatusChanged;

    public Task StartLaunchAsync(IReadOnlyList<string> paths, string? wegamePath, TimeSpan? timeoutOverride = null)
    {
        lock (_gate)
        {
            if (_disposed || _status.Phase != ClientLaunchPhase.Idle || _isConnectedFunc())
            {
                return Task.CompletedTask;
            }

            UpdateStatusLocked(new ClientLaunchStatus(ClientLaunchPhase.Launching));
        }

        return ExecuteLaunchAsync(paths, wegamePath, timeoutOverride);
    }

    internal void NotifyConnectionStatus(LcuConnectionStatus connectionStatus)
    {
        if (connectionStatus != LcuConnectionStatus.Connected)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _status.Phase == ClientLaunchPhase.Idle)
            {
                return;
            }

            CancelWaitTimeoutLocked();
            UpdateStatusLocked(new ClientLaunchStatus(ClientLaunchPhase.Idle));
        }
    }

    private async Task ExecuteLaunchAsync(IReadOnlyList<string> paths, string? wegamePath, TimeSpan? timeoutOverride)
    {
        string? error;
        try
        {
            error = await Task.Run(() => _launchFunc(paths, wegamePath)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "手动启动客户端异常");
            error = $"启动客户端失败: {ex.Message}";
        }

        if (error is not null)
        {
            _logger?.LogWarning("手动启动客户端失败: {Error}", error);
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                CancelWaitTimeoutLocked();
                UpdateStatusLocked(new ClientLaunchStatus(ClientLaunchPhase.Idle, error, ++_errorSequence));
            }
            return;
        }

        CancellationTokenSource? timeoutCts = null;
        TimeSpan timeout = timeoutOverride ?? (paths.Contains(AppConfig.WeGameMarker)
            ? DefaultWeGameTimeout
            : DefaultClientTimeout);

        lock (_gate)
        {
            if (_disposed || _isConnectedFunc())
            {
                CancelWaitTimeoutLocked();
                UpdateStatusLocked(new ClientLaunchStatus(ClientLaunchPhase.Idle));
                return;
            }

            CancelWaitTimeoutLocked();
            timeoutCts = new CancellationTokenSource();
            _waitTimeoutCts = timeoutCts;
            UpdateStatusLocked(new ClientLaunchStatus(ClientLaunchPhase.WaitingForConnect));
        }

        _ = WaitForTimeoutAsync(timeoutCts, timeout);
    }

    private async Task WaitForTimeoutAsync(CancellationTokenSource cts, TimeSpan timeout)
    {
        try
        {
            await Task.Delay(timeout, _timeProvider, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_waitTimeoutCts, cts) || _status.Phase != ClientLaunchPhase.WaitingForConnect)
            {
                return;
            }

            _waitTimeoutCts = null;
            cts.Dispose();

            if (_isConnectedFunc())
            {
                UpdateStatusLocked(new ClientLaunchStatus(ClientLaunchPhase.Idle));
                return;
            }

            UpdateStatusLocked(new ClientLaunchStatus(
                ClientLaunchPhase.Idle,
                "等待客户端连接超时，请确认客户端已启动后重试",
                ++_errorSequence));
        }
    }

    private void CancelWaitTimeoutLocked()
    {
        if (_waitTimeoutCts is not null)
        {
            _waitTimeoutCts.Cancel();
            _waitTimeoutCts.Dispose();
            _waitTimeoutCts = null;
        }
    }

    private void UpdateStatusLocked(ClientLaunchStatus next)
    {
        _status = next;
        StatusChanged?.Invoke(next);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelWaitTimeoutLocked();
        }

        _lcuSubscription?.Dispose();
    }

    private sealed class ConnectionObserver(ClientLaunchCoordinator owner) : IObserver<LcuConnectionState>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(LcuConnectionState value) => owner.NotifyConnectionStatus(value.Status);
    }
}
