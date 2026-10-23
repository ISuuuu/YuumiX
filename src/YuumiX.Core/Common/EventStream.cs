namespace YuumiX.Core.Common;

/// <summary>
/// 轻量多播事件流：Core 向 UI 暴露状态的唯一载体（ADR-05 / ADR-10）。
///
/// 不引入 <c>System.Reactive</c>：本项目只需要「订阅 + 推送 + 结束」三件事，
/// 引一整套操作符库换不来任何收益，反而多一个版本约束。
///
/// 线程语义：
/// <list type="bullet">
///   <item><see cref="Publish"/> 可在任意线程调用（LCU 事件在后台线程到达）；</item>
///   <item>投递期间不持锁，观察者在自己的线程上被同步调用；</item>
///   <item>单个观察者抛异常不会影响其余观察者，也不会打断发布方——
///         这是后台事件的硬性要求，否则一次 UI 渲染异常会连带杀死 WebSocket 读循环。</item>
/// </list>
///
/// 注意：本类型不做线程封送。需要回到 UI 线程由消费方负责，
/// Reactor 侧对应 <c>UseState(..., threadSafe: true)</c>（ADR-07）。
/// </summary>
public sealed class EventStream<T> : IObservable<T>
{
    private readonly object _gate = new();
    private readonly List<IObserver<T>> _observers = [];
    private readonly Action<Exception>? _observerErrorHandler;

    private bool _completed;

    /// <param name="observerErrorHandler">
    /// 观察者回调抛异常时的回调。传入日志记录器，避免异常被完全吞掉。
    /// </param>
    public EventStream(Action<Exception>? observerErrorHandler = null)
    {
        _observerErrorHandler = observerErrorHandler;
    }

    /// <summary>当前订阅者数量，供诊断与测试使用。</summary>
    public int ObserverCount
    {
        get
        {
            lock (_gate)
            {
                return _observers.Count;
            }
        }
    }

    /// <summary>事件流是否已结束（正常完成或出错）。</summary>
    public bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                return _completed;
            }
        }
    }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_gate)
        {
            if (_completed)
            {
                observer.OnCompleted();
                return EmptySubscription.Instance;
            }

            _observers.Add(observer);
        }

        return new Subscription(this, observer);
    }

    /// <summary>以委托形式订阅，省掉实现 <see cref="IObserver{T}"/> 的样板。</summary>
    public IDisposable Subscribe(Action<T> onNext)
    {
        ArgumentNullException.ThrowIfNull(onNext);
        return Subscribe(new DelegateObserver(onNext));
    }

    public void Publish(T value)
    {
        IObserver<T>[] snapshot;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            snapshot = [.. _observers];
        }

        foreach (var observer in snapshot)
        {
            try
            {
                observer.OnNext(value);
            }
            catch (Exception ex)
            {
                _observerErrorHandler?.Invoke(ex);
            }
        }
    }

    /// <summary>正常结束：通知所有观察者并在之后再订阅时立即完成。</summary>
    public void Complete()
    {
        IObserver<T>[] snapshot;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            snapshot = [.. _observers];
            _observers.Clear();
        }

        foreach (var observer in snapshot)
        {
            SafeInvoke(observer.OnCompleted);
        }
    }

    /// <summary>异常结束：把异常交给观察者。</summary>
    public void Fault(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        IObserver<T>[] snapshot;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            snapshot = [.. _observers];
            _observers.Clear();
        }

        foreach (var observer in snapshot)
        {
            SafeInvoke(() => observer.OnError(error));
        }
    }

    private void Remove(IObserver<T> observer)
    {
        lock (_gate)
        {
            _observers.Remove(observer);
        }
    }

    private void SafeInvoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _observerErrorHandler?.Invoke(ex);
        }
    }

    private sealed class Subscription(EventStream<T> owner, IObserver<T> observer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            owner.Remove(observer);
        }
    }

    private sealed class DelegateObserver(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    private sealed class EmptySubscription : IDisposable
    {
        public static readonly EmptySubscription Instance = new();

        public void Dispose()
        {
        }
    }
}
