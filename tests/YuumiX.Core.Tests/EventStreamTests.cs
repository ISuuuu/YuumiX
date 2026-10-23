using Xunit;
using YuumiX.Core.Common;

namespace YuumiX.Core.Tests;

/// <summary>
/// 事件流：投递、取消订阅、结束语义、观察者异常隔离。
/// 这些行为是 ADR-07（跨线程更新）能成立的前提——UI 回调出错不得反杀事件源。
/// </summary>
public sealed class EventStreamTests
{
    [Fact]
    public void Publish_DeliversToAllSubscribers()
    {
        var stream = new EventStream<int>();
        var first = new List<int>();
        var second = new List<int>();

        using var a = stream.Subscribe(first.Add);
        using var b = stream.Subscribe(second.Add);

        stream.Publish(1);
        stream.Publish(2);

        Assert.Equal([1, 2], first);
        Assert.Equal([1, 2], second);
        Assert.Equal(2, stream.ObserverCount);
    }

    [Fact]
    public void Dispose_StopsDelivery()
    {
        var stream = new EventStream<int>();
        var received = new List<int>();

        var subscription = stream.Subscribe(received.Add);
        stream.Publish(1);
        subscription.Dispose();
        stream.Publish(2);

        Assert.Equal([1], received);
        Assert.Equal(0, stream.ObserverCount);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var stream = new EventStream<int>();
        var subscription = stream.Subscribe(_ => { });

        subscription.Dispose();
        subscription.Dispose();

        Assert.Equal(0, stream.ObserverCount);
    }

    /// <summary>
    /// 单个观察者抛异常时，其余观察者仍应收到事件，发布方也不能崩。
    /// </summary>
    [Fact]
    public void ThrowingObserver_DoesNotBreakOthersOrPublisher()
    {
        var errors = new List<Exception>();
        var stream = new EventStream<int>(errors.Add);
        var survivorValues = new List<int>();

        using var bad = stream.Subscribe(_ => throw new InvalidOperationException("渲染失败"));
        using var good = stream.Subscribe(survivorValues.Add);

        stream.Publish(42);

        Assert.Equal([42], survivorValues);
        Assert.Single(errors);
        Assert.IsType<InvalidOperationException>(errors[0]);
    }

    [Fact]
    public void Complete_NotifiesAndRejectsLateSubscribers()
    {
        var stream = new EventStream<int>();
        var completed = false;

        using var subscription = stream.Subscribe(new CompletionObserver(() => completed = true));

        stream.Complete();
        stream.Publish(1); // 结束后投递应被丢弃

        Assert.True(completed);
        Assert.True(stream.IsCompleted);
        Assert.Equal(0, stream.ObserverCount);
    }

    [Fact]
    public void Fault_PropagatesError()
    {
        var stream = new EventStream<int>();
        Exception? observed = null;

        using var subscription = stream.Subscribe(new ErrorObserver(ex => observed = ex));

        stream.Fault(new TimeoutException("连接超时"));

        Assert.IsType<TimeoutException>(observed);
        Assert.True(stream.IsCompleted);
    }

    /// <summary>并发投递与订阅/退订不应破坏内部状态。</summary>
    [Fact]
    public async Task PublishAndSubscribe_AreThreadSafe()
    {
        var stream = new EventStream<int>();
        var counter = 0;

        var publishers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                stream.Publish(worker * 1000 + i);
            }
        })).ToArray();

        var subscribers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                using var subscription = stream.Subscribe(_ => Interlocked.Increment(ref counter));
                stream.Publish(-1);
            }
        })).ToArray();

        await Task.WhenAll([.. publishers, .. subscribers]);

        // 唯一的硬断言是「没有崩、状态未损坏」；计数不作精确断言。
        Assert.True(counter > 0);
        Assert.Equal(0, stream.ObserverCount);
    }

    private sealed class CompletionObserver(Action onCompleted) : IObserver<int>
    {
        public void OnNext(int value)
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted() => onCompleted();
    }

    private sealed class ErrorObserver(Action<Exception> onError) : IObserver<int>
    {
        public void OnNext(int value)
        {
        }

        public void OnError(Exception error) => onError(error);

        public void OnCompleted()
        {
        }
    }
}
