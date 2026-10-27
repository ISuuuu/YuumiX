using Xunit;
using YuumiX.App.Windowing;

namespace YuumiX.App.Tests;

public sealed class ScaleStateTests
{
    [Fact]
    public void ScaleState_InitializesWithDefaultValues()
    {
        var scale = new ScaleState("Auto");
        Assert.Equal("Auto", scale.CurrentDpiScale);
        Assert.Equal(1.0, scale.Current);

        var scale125 = new ScaleState("125");
        Assert.Equal("125", scale125.CurrentDpiScale);
        Assert.Equal(1.25, scale125.Current);

        var scaleNull = new ScaleState(null);
        Assert.Equal("Auto", scaleNull.CurrentDpiScale);
        Assert.Equal(1.0, scaleNull.Current);
    }

    [Fact]
    public void SetScale_UpdatesStateAndNotifiesSubscribers()
    {
        var scale = new ScaleState("Auto");
        var notifications = new List<double>();
        using var subscription = scale.Subscribe(notifications.Add);

        scale.SetScale("125");
        Assert.Equal("125", scale.CurrentDpiScale);
        Assert.Equal(1.25, scale.Current);
        Assert.Single(notifications);
        Assert.Equal(1.25, notifications[0]);

        // 重复设置相同缩放：不应再次触发通知
        scale.SetScale("125");
        Assert.Single(notifications);

        scale.SetScale("150");
        Assert.Equal("150", scale.CurrentDpiScale);
        Assert.Equal(1.5, scale.Current);
        Assert.Equal(2, notifications.Count);
        Assert.Equal(1.5, notifications[1]);
    }

    [Fact]
    public void SetScale_UnsubscribeStopsNotifications()
    {
        var scale = new ScaleState("Auto");
        var count = 0;
        var subscription = scale.Subscribe(_ => count++);

        scale.SetScale("125");
        Assert.Equal(1, count);

        subscription.Dispose();
        scale.SetScale("150");
        Assert.Equal(1, count);
    }
}
