namespace YuumiX.App.Windowing;

using YuumiX.Core.Common;

/// <summary>
/// 可订阅的界面缩放状态（类似 <c>ThemeState</c>）。
/// 承载当前 DpiScale 对应的缩放系数（1.0 / 1.25 / 1.5），支持通过 EventStream 广播变更。
/// </summary>
public sealed class ScaleState
{
    private readonly EventStream<double> _changes;

    public ScaleState(string? initialDpiScale, Action<Exception>? onObserverError = null)
    {
        _changes = new EventStream<double>(onObserverError);
        CurrentDpiScale = initialDpiScale ?? "Auto";
        Current = WindowMetrics.ParseScaleFactor(CurrentDpiScale);
    }

    /// <summary>当前缩放系数（1.0 / 1.25 / 1.5）。</summary>
    public double Current { get; private set; }

    /// <summary>当前配置键名（"Auto" / "100" / "125" / "150"）。</summary>
    public string CurrentDpiScale { get; private set; }

    /// <summary>订阅缩放系数变化。返回的 <see cref="IDisposable"/> 必须由调用方在卸载时释放。</summary>
    public IDisposable Subscribe(Action<double> onChanged) => _changes.Subscribe(onChanged);

    /// <summary>
    /// 用新缩放配置更新状态并通知订阅者。
    /// 若缩放系数未发生实质变化，则不触发冗余重渲染。
    /// </summary>
    public void SetScale(string? dpiScale)
    {
        CurrentDpiScale = dpiScale ?? "Auto";
        SetScaleFactor(WindowMetrics.ParseScaleFactor(CurrentDpiScale));
    }

    /// <summary>
    /// 直接设置缩放系数并通知订阅者。
    /// </summary>
    public void SetScaleFactor(double factor)
    {
        if (Math.Abs(factor - Current) < 0.0001)
        {
            return;
        }

        Current = factor;
        _changes.Publish(factor);
    }
}
