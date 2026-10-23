using YuumiX.Core.Common;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Navigation;

/// <summary>
/// 可订阅的侧边栏菜单可见性状态。与 <c>ThemeState</c>（ADR-12 决策三）同一套解法：
/// 配置 POCO（<see cref="AppConfig"/>）没有变更通知，设置页切了「隐藏菜单」开关后，
/// 上层的 <see cref="Shell.AppShell"/> 不会重渲染，表现为「开关已保存但侧边栏不变」。
///
/// 为什么放在 App 而非 Core：主题同款理由——菜单显隐是纯 UI 概念，
/// Core 不该为了刷新侧边栏依赖 UI 类型（ADR-09）。
/// </summary>
public sealed class MenuVisibilityState
{
    private readonly EventStream<MenuVisibilityState> _changes;

    public MenuVisibilityState(FunctionsConfig initial, Action<Exception>? onObserverError = null)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _changes = new EventStream<MenuVisibilityState>(onObserverError);
        HideTft = initial.HideTft;
        HideSavedPlayers = initial.HideSavedPlayers;
    }

    /// <summary>当前「隐藏云顶之弈入口」。</summary>
    public bool HideTft { get; private set; }

    /// <summary>当前「隐藏路人集入口」。</summary>
    public bool HideSavedPlayers { get; private set; }

    /// <summary>订阅菜单可见性变化。返回的 <see cref="IDisposable"/> 必须由调用方在卸载时释放。</summary>
    public IDisposable Subscribe(Action<MenuVisibilityState> onChanged) => _changes.Subscribe(onChanged);

    /// <summary>
    /// 用最新开关值刷新状态并通知订阅者。值未变化时直接返回。
    /// 通知的是 <c>this</c> 本体：消费方在渲染里读的是最新属性值。
    /// </summary>
    public void Apply(bool hideTft, bool hideSavedPlayers)
    {
        if (hideTft == HideTft && hideSavedPlayers == HideSavedPlayers)
        {
            return;
        }

        HideTft = hideTft;
        HideSavedPlayers = hideSavedPlayers;
        _changes.Publish(this);
    }
}
