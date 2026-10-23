using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor.Core;
using YuumiX.Core;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Composition;

/// <summary>
/// 组合根在 UI 侧的入口（ADR-06 的落地形态）。
///
/// 用法：
/// <list type="bullet">
///   <item>根组件 <c>AppShell</c> 通过 <see cref="Services"/> 取用（它位于 Provide 之上，
///         读不到自己提供的上下文，这是 Context 机制的正常约束）；</item>
///   <item>其余所有子组件一律 <c>UseContext(AppHost.Context)</c>，不依赖静态状态，
///         便于将来把页面搬到独立窗口时单独提供上下文。</item>
/// </list>
///
/// 命名上避开 <c>AppContext</c>：<c>System.AppContext</c> 已占用该名字，
/// 且 Reactor 内部另有 <c>Microsoft.UI.Reactor.Core.AppContexts</c>，
/// 沿用原 ADR 中的命名会在 <c>using</c> 处产生解析歧义（与 ADR-04 避开 <c>Theme</c> 同类）。
/// </summary>
public static class AppHost
{
    /// <summary>
    /// 服务上下文键，供 <c>Provide</c> / <c>UseContext</c> 使用。
    ///
    /// Reactor 的 <c>Context&lt;T&gt;</c> 构造签名是 <c>(T defaultValue, string? name)</c>：
    /// 上下文链上没有提供者时返回 <paramref name="defaultValue"/>。此处传 <c>null!</c>，
    /// 让「忘记 Provide」变成即时的空引用异常，而不是拿到一个看似可用的空服务对象。
    /// </summary>
    public static readonly Context<AppServices> Context = new(null!, "AppServices");

    private static AppServices? _services;

    /// <summary>进程级服务实例。仅供根组件使用。</summary>
    public static AppServices Services =>
        _services ?? throw new InvalidOperationException("AppHost 尚未初始化，请先调用 Initialize()");

    /// <summary>
    /// 构造 DI 容器、日志与配置，并启动 LCU 连接循环。必须早于任何 UI 渲染。
    ///
    /// 连接在这里启动而不是在 <c>AppServices.Create</c> 里：Create 是纯构造入口，
    /// 单测与工具会调用它，不能顺带拉起后台线程。宿主才是有权决定「什么时候开始连客户端」的一方。
    /// </summary>
    public static void Initialize()
    {
        _services = AppServices.Create();
        _services.Services.GetRequiredService<LcuConnection>().Start(_services.ShutdownToken);
    }

    /// <summary>释放服务与日志。</summary>
    public static void Shutdown()
    {
        _services?.Dispose();
        _services = null;
    }
}
