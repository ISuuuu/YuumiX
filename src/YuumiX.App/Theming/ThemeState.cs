using YuumiX.Core.Common;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Theming;

/// <summary>
/// 可订阅的主题状态（ADR-12 决策三）。
///
/// 为什么需要它：<see cref="AppConfig"/> 是无变更通知的 POCO，设置页改了
/// <c>Personalization</c> 之后，位于它上层的 <c>AppShell</c> 不会重渲染，
/// 主题模式/主题色/背景就会「保存成功但界面不变」。
///
/// 为什么放在 App 而非 Core：主题是纯 UI 概念，Core 的 TFM 里没有 <c>ElementTheme</c>，
/// 也不该为了刷新一次界面就去依赖 WPF/WinUI 类型（ADR-09）。
///
/// 复用 Core 的 <see cref="EventStream{T}"/> 而不是 <c>event Action</c>：
/// 后者在观察者抛异常时会连带打断发布方（这里就是「点一下开关，整个设置页崩掉」）。
/// </summary>
public sealed class ThemeState
{
    private readonly EventStream<YuumiPalette> _changes;

    public ThemeState(PersonalizationConfig initial, Action<Exception>? onObserverError = null)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _changes = new EventStream<YuumiPalette>(onObserverError);
        Current = YuumiPalette.FromConfig(initial);
    }

    /// <summary>当前调色板。UI 首帧直接取它，避免先渲染一帧默认色再被覆盖。</summary>
    public YuumiPalette Current { get; private set; }

    /// <summary>订阅主题变化。返回的 <see cref="IDisposable"/> 必须由调用方在卸载时释放。</summary>
    public IDisposable Subscribe(Action<YuumiPalette> onChanged) => _changes.Subscribe(onChanged);

    /// <summary>
    /// 用最新配置刷新调色板并通知订阅者。
    /// 值未变化时直接返回——记录类型的值相等语义让「重复点同一项」不产生多余渲染。
    /// </summary>
    public void Apply(PersonalizationConfig personalization)
    {
        var next = YuumiPalette.FromConfig(personalization);
        if (next == Current)
        {
            return;
        }

        Current = next;
        _changes.Publish(next);
    }
}
