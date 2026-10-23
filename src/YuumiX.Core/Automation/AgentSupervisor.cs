using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Automation;

/// <summary>
/// 正在执行的自动化任务快照。
/// </summary>
public sealed record TaskSnapshot(long Id, TaskKind Kind, string Description, DateTimeOffset StartedAt);

/// <summary>
/// 当同类任务正在执行且去重策略为 RejectNew 时抛出的拒绝异常。
/// </summary>
public sealed class DuplicateRejectedException(TaskKind kind)
    : Exception($"任务启动被拒绝: 已有同类任务正在运行 ({kind})")
{
    public TaskKind Kind { get; } = kind;
}

/// <summary>
/// 异步自动化任务监管中心（对应旧版 agents/supervisor.rs::TaskSupervisor）。
/// 管理后台异步任务生命周期、去重决策、协作式取消以及替换旧任务时的优雅让出等待。
/// </summary>
public sealed class AgentSupervisor : IAsyncDisposable
{
    public static readonly TimeSpan DefaultReplaceGraceTimeout = TimeSpan.FromMilliseconds(1000);

    private readonly TimeSpan _replaceGraceTimeout;
    private readonly ILogger<AgentSupervisor>? _logger;
    private readonly object _lock = new();
    private readonly Dictionary<long, TaskEntry> _tasks = [];
    private long _nextId = 1;
    private bool _disposed;

    private sealed class TaskEntry(
        long id,
        TaskKind kind,
        string description,
        CancellationTokenSource cts,
        TaskCompletionSource<bool> tcs)
    {
        public long Id { get; } = id;
        public TaskKind Kind { get; } = kind;
        public string Description { get; } = description;
        public CancellationTokenSource Cts { get; } = cts;
        public TaskCompletionSource<bool> Tcs { get; } = tcs;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    }

    public AgentSupervisor(TimeSpan? replaceGraceTimeout = null, ILogger<AgentSupervisor>? logger = null)
    {
        _replaceGraceTimeout = replaceGraceTimeout ?? DefaultReplaceGraceTimeout;
        _logger = logger;
    }

    /// <summary>
    /// 获取当前在册运行的任务快照列表。
    /// </summary>
    public IReadOnlyList<TaskSnapshot> GetActiveTasks()
    {
        lock (_lock)
        {
            return _tasks.Values
                .Select(t => new TaskSnapshot(t.Id, t.Kind, t.Description, t.StartedAt))
                .ToList();
        }
    }

    /// <summary>
    /// 注册并启动一个受监管的异步任务。若被去重策略拒绝则抛出 <see cref="DuplicateRejectedException"/>。
    /// </summary>
    public async Task<long> SpawnAsync(
        TaskKind kind,
        string description,
        Func<CancellationToken, Task> taskFactory)
    {
        var (spawned, id) = await TrySpawnAsync(kind, description, taskFactory).ConfigureAwait(false);
        if (!spawned)
        {
            throw new DuplicateRejectedException(kind);
        }

        return id;
    }

    /// <summary>
    /// 尝试注册并启动一个受监管的异步任务。若被去重策略拒绝则返回 (false, 0)。
    /// </summary>
    public async Task<(bool Spawned, long TaskId)> TrySpawnAsync(
        TaskKind kind,
        string description,
        Func<CancellationToken, Task> taskFactory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(taskFactory);

        DuplicateStrategy strategy = kind.DefaultStrategy;
        List<(long Id, Task DoneTask)> replaced = [];

        // 阶段一：根据去重策略检查现有任务并收集需要等待退出的任务
        lock (_lock)
        {
            switch (strategy)
            {
                case DuplicateStrategy.RejectNew:
                    bool alreadyRunning = _tasks.Values.Any(t => t.Kind == kind);
                    if (alreadyRunning)
                    {
                        _logger?.LogDebug("任务被去重策略 RejectNew 拒绝: {Kind}", kind);
                        return (false, 0);
                    }
                    break;

                case DuplicateStrategy.ReplaceOld:
                    List<long> oldIds = _tasks.Values
                        .Where(t => t.Kind == kind)
                        .Select(t => t.Id)
                        .ToList();

                    foreach (long oldId in oldIds)
                    {
                        if (_tasks.Remove(oldId, out TaskEntry? entry))
                        {
                            entry.Cts.Cancel();
                            replaced.Add((oldId, entry.Tcs.Task));
                        }
                    }
                    break;

                case DuplicateStrategy.AllowConcurrent:
                    break;
            }
        }

        // 阶段二：锁外等待旧任务真正让出执行权（带超时宽限期）
        foreach (var (oldId, doneTask) in replaced)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(_replaceGraceTimeout);
                await doneTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                _logger?.LogDebug("旧任务 #{OldId} 已正常退出，接力启动新任务", oldId);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogWarning("旧任务 #{OldId} 在宽限期 {Timeout}ms 内未退出，放行新任务", oldId, _replaceGraceTimeout.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "旧任务 #{OldId} 异常终止，放行新任务", oldId);
            }
        }

        // 阶段三：入册并启动新任务
        long taskId;
        CancellationTokenSource cts;
        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
        {
            if (_disposed)
            {
                return (false, 0);
            }

            // 阶段二等待期间可能有同类任务插队入册（并发触发），让位于最新请求
            if (strategy == DuplicateStrategy.ReplaceOld)
            {
                List<long> racingIds = _tasks.Values
                    .Where(t => t.Kind == kind)
                    .Select(t => t.Id)
                    .ToList();

                foreach (long rId in racingIds)
                {
                    if (_tasks.Remove(rId, out TaskEntry? rEntry))
                    {
                        try { rEntry.Cts.Cancel(); } catch (ObjectDisposedException) { }
                    }
                }
            }

            taskId = Interlocked.Increment(ref _nextId);
            cts = new CancellationTokenSource();
            var entry = new TaskEntry(taskId, kind, description, cts, tcs);
            _tasks[taskId] = entry;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await taskFactory(cts.Token).ConfigureAwait(false);
                tcs.TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cts.Token);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "受监管任务 #{TaskId} ({Kind}) 执行异常", taskId, kind);
                tcs.TrySetException(ex);
            }
            finally
            {
                lock (_lock)
                {
                    _tasks.Remove(taskId);
                }
                cts.Dispose();
            }
        });

        return (true, taskId);
    }

    /// <summary>
    /// 取消指定 ID 的任务。
    /// </summary>
    public bool CancelTask(long taskId)
    {
        lock (_lock)
        {
            if (_tasks.Remove(taskId, out TaskEntry? entry))
            {
                try { entry.Cts.Cancel(); } catch (ObjectDisposedException) { }
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 取消指定类型的所有任务。
    /// </summary>
    public int CancelKind(TaskKind kind)
    {
        List<TaskEntry> toCancel = [];
        lock (_lock)
        {
            List<long> matchedIds = _tasks.Values
                .Where(t => t.Kind == kind)
                .Select(t => t.Id)
                .ToList();

            foreach (long id in matchedIds)
            {
                if (_tasks.Remove(id, out TaskEntry? entry))
                {
                    toCancel.Add(entry);
                }
            }
        }

        foreach (var entry in toCancel)
        {
            try { entry.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        return toCancel.Count;
    }

    /// <summary>
    /// 取消所有在册任务。
    /// </summary>
    public void CancelAll()
    {
        List<TaskEntry> toCancel;
        lock (_lock)
        {
            toCancel = _tasks.Values.ToList();
            _tasks.Clear();
        }

        foreach (var entry in toCancel)
        {
            try { entry.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
        }

        CancelAll();
        return ValueTask.CompletedTask;
    }
}
