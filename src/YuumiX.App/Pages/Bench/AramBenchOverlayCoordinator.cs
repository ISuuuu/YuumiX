using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Windowing;
using YuumiX.Core.Features.AramBench;

namespace YuumiX.App.Pages.Bench;

/// <summary>
/// 悬浮窗的显隐协调者：订阅板凳席状态流，把「该不该有悬浮窗」翻译成
/// <see cref="AramBenchWindowHost"/> 的打开/关闭。本身不渲染任何内容。
///
/// 为什么单独拆一个组件而不是塞进 <c>AppShell</c>：板凳席状态在选人阶段每秒都在变
/// （倒计时驱动会话回推），把它放进外壳会让整棵导航树跟着重渲染；
/// 独立组件的重渲染只花一个 <c>Empty()</c>。
///
/// 对齐旧版 <c>App.vue</c> 的两个 watcher：阶段变化 → 离开选人即关闭；
/// 会话变化 → <c>benchEnabled</c> 为真即拉起。这里合并成一条状态流，避免两个真源打架。
/// </summary>
public sealed class AramBenchOverlayCoordinator : Component
{
    public override Element Render()
    {
        var services = UseContext(AppHost.Context) ?? AppHost.Services;
        var bench = services.Services.GetRequiredService<IAramBenchService>();

        // threadSafe: true —— 状态推送来自 LCU 后台线程（ADR-07）
        var (state, setState) = UseState(bench.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = bench.States.Subscribe(setState);
                return () => subscription.Dispose();
            },
            0);

        bool featureEnabled = services.Config.Functions.EnableBenchOverlay;

        // 只有影响显隐的三位变化时才同步：英雄列表每秒都在变，跟着它调窗口只会白白抖动。
        // 位运算而不用元组：元组做 deps 会触发 REACTOR_HOOKS_004（同一渲染内 deps 长度必须稳定）。
        var syncKey = (state.IsChampSelect ? 1 : 0) | (state.BenchEnabled ? 2 : 0) | (featureEnabled ? 4 : 0);

        UseEffect(
            () =>
            {
                AramBenchWindowHost.Sync(state, featureEnabled);
                return static () => { };
            },
            syncKey);

        return Empty();
    }
}
