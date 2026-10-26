using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor.Core;
using YuumiX.App.Navigation;
using YuumiX.App.Theming;
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

    /// <summary>
    /// 主题状态上下文键（ADR-12 决策三）。与 <see cref="Context"/> 一样在根组件上 Provide 一次；
    /// 需要读取当前主题色/主题模式的组件用它，而不是去翻配置——配置没有变更通知。
    /// </summary>
    public static readonly Context<ThemeState> ThemeContext = new(null!, "ThemeState");

    /// <summary>
    /// 侧边栏菜单可见性状态上下文。与 <see cref="ThemeContext"/> 同理在根组件上 Provide 一次；
    /// 「隐藏菜单」这类影响全局菜单的配置由它承载，而不是寄望配置自身有变更通知。
    /// </summary>
    public static readonly Context<MenuVisibilityState> MenuContext = new(null!, "MenuVisibilityState");

    /// <summary>
    /// 全局 Toast 服务上下文（R5）。由窗口根各持一份 <see cref="ToastQueue"/> 并 Provide
    /// （AppShell / OpggWindowRoot 互不串扰）；页面组件经 UseContext 取用发起提示。
    /// </summary>
    public static readonly Context<IToastService> ToastContext = new(null!, "ToastService");

    private static AppServices? _services;
    private static ThemeState? _theme;
    private static MenuVisibilityState? _menu;

    /// <summary>进程级服务实例。仅供根组件使用。</summary>
    public static AppServices Services =>
        _services ?? throw new InvalidOperationException("AppHost 尚未初始化，请先调用 Initialize()");

    /// <summary>进程级主题状态。仅供根组件使用（其余组件走 <see cref="ThemeContext"/>）。</summary>
    public static ThemeState Theme =>
        _theme ?? throw new InvalidOperationException("AppHost 尚未初始化，请先调用 Initialize()");

    /// <summary>进程级菜单可见性状态。仅供根组件使用（其余组件走 <see cref="MenuContext"/>）。</summary>
    public static MenuVisibilityState Menu =>
        _menu ?? throw new InvalidOperationException("AppHost 尚未初始化，请先调用 Initialize()");

    /// <summary>
    /// 构造 DI 容器、日志与配置，并启动 LCU 连接循环。必须早于任何 UI 渲染。
    ///
    /// 连接在这里启动而不是在 <c>AppServices.Create</c> 里：Create 是纯构造入口，
    /// 单测与工具会调用它，不能顺带拉起后台线程。宿主才是有权决定「什么时候开始连客户端」的一方。
    /// </summary>
    public static void Initialize()
    {
        _services = AppServices.Create();
        _theme = new ThemeState(
            _services.Config.Personalization,
            ex => _services.LoggerFactory
                .CreateLogger<ThemeState>()
                .LogError(ex, "主题订阅者抛异常"));
        _menu = new MenuVisibilityState(
            _services.Config.Functions,
            ex => _services.LoggerFactory
                .CreateLogger<MenuVisibilityState>()
                .LogError(ex, "菜单可见性订阅者抛异常"));
        _services.Services.GetRequiredService<LcuConnection>().Start(_services.ShutdownToken);
        _services.Services.GetRequiredService<YuumiX.Core.Automation.AutomationHost>().Start(_services.ShutdownToken);

        // 板凳席服务同样是「构造即订阅」，这里提前解析一次：悬浮窗的显隐由 UI 继承状态流驱动，
        // 若等到第一次渲染才创建，助手启动时已经处于选人阶段的用户会漏掉开局那几秒的事件。
        _services.Services.GetRequiredService<YuumiX.Core.Features.AramBench.IAramBenchService>();

        // 「随应用启动客户端」：消费 General.EnableStartLolWithApp（旧版 launch_lol_client 语义）。
        // 放后台线程：进程扫描与注册表探测都是阻塞调用，不能挡住 UI 首帧；
        // 客户端启动失败只记日志——主页「启动客户端」按钮承担人工兜底。
        if (_services.Config.General.EnableStartLolWithApp)
        {
            var config = _services.Config;
            var launcher = _services.Services.GetRequiredService<YuumiX.Core.Features.Tools.LolClientLauncher>();
            var logger = _services.LoggerFactory.CreateLogger<YuumiX.Core.Features.Tools.LolClientLauncher>();
            _ = Task.Run(() =>
            {
                string? error = launcher.Launch(config.General.LolPath, config.General.WegamePath);
                if (error is not null)
                {
                    logger.LogWarning("随应用启动客户端失败: {Error}", error);
                }
            });
        }

        // 「检查更新」开关的消费点：启动时后台静默检查（旧 startup_check_update 语义）。
        // 发现新版仅记日志——侧边栏红点属于托盘/阶段 8 体系，本版不引入。
        if (_services.Config.General.EnableCheckUpdate)
        {
            var updateService = _services.Services.GetRequiredService<YuumiX.Core.Updates.UpdateService>();
            var updateLogger = _services.LoggerFactory.CreateLogger<YuumiX.Core.Updates.UpdateService>();
            _ = Task.Run(async () =>
            {
                try
                {
                    var info = await updateService.CheckForUpdateAsync(_services.Paths.IsPortable).ConfigureAwait(false);
                    if (info is not null)
                    {
                        updateLogger.LogInformation("启动检查发现新版本 v{Version}（当前 v{Current}）", info.Version, info.CurrentVersion);
                    }
                }
                catch (Exception ex)
                {
                    updateLogger.LogWarning(ex, "启动更新检查失败（离线或网络受限属常态）");
                }
            });
        }

        // 对局结束后的云端上传（旧 upload.rs：EndOfGame → 拉最新对局 → 入队 → POST 上传 API）。
        // 走队列而不是直接调 UploadService：去重、串行、失败落盘重试这三件事只在队列里有一份实现，
        // 直接调用等于丢掉断网期间的对局（旧版 upload_single_task 的落盘分支）。
        // UploadEnabled 开关由 UploadService 内部消费，UI 放在战绩查询页工具栏（同旧 Search.vue）。
        var lifecycle = _services.Services.GetRequiredService<YuumiX.Core.Features.GameLifecycle.GameLifecycleService>();
        var uploadQueue = _services.Services.GetRequiredService<YuumiX.Core.Upload.UploadQueueService>();
        var summonerService = _services.Services.GetRequiredService<YuumiX.Core.Features.Summoner.ISummonerService>();
        var matchService = _services.Services.GetRequiredService<YuumiX.Core.Features.Match.IMatchService>();
        var uploadLogger = _services.LoggerFactory.CreateLogger<YuumiX.Core.Upload.UploadQueueService>();
        _ = lifecycle.GameEnded.Subscribe(_timestamp =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // 延迟 2 秒等待 LCU 数据写入（对齐旧 UploadTrigger）
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                    var profile = await summonerService.GetCurrentSummonerAsync().ConfigureAwait(false);
                    if (profile is null)
                    {
                        return;
                    }

                    var page = await matchService.GetMatchHistoryAsync(profile.Puuid, 0, 0).ConfigureAwait(false);
                    if (page.Games.Count == 0)
                    {
                        return;
                    }

                    uploadQueue.Enqueue(page.Games[0].GameId);
                    // 顺带把以前没传完的一起重试（旧 UploadTrigger 收尾动作）
                    uploadQueue.TriggerPendingRetry();
                }
                catch (Exception ex)
                {
                    uploadLogger.LogWarning(ex, "对局结束后的自动上传失败");
                }
            });
        });

        // 启动恢复：上次没传完的对局重新入队（旧 UploadQueue::new 里启动 5s 后触发一次）。
        // 延后是刻意的——此时客户端可能还没起来，立刻重试只会全部失败并白白累加重试次数。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                int pending = uploadQueue.TriggerPendingRetry();
                if (pending > 0)
                {
                    uploadLogger.LogInformation("启动恢复：{Count} 场对局重新进入上传队列", pending);
                }
            }
            catch (Exception ex)
            {
                uploadLogger.LogWarning(ex, "启动恢复暂存上传失败");
            }
        });

        // 启动 SignalR Hub 远程反代（对齐旧 lib.rs 条件启动：LcuRealtimeEnabled 开启且配置了 URL）
        var signalr = _services.Services.GetRequiredService<YuumiX.Core.Realtime.SignalrService>();
        _ = Task.Run(async () =>
        {
            try
            {
                await signalr.ApplyConfigAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _services.LoggerFactory.CreateLogger<YuumiX.Core.Realtime.SignalrService>()
                    .LogWarning(ex, "启动 SignalR 远程反代服务失败");
            }
        });
    }

    /// <summary>释放服务与日志。</summary>
    public static async ValueTask ShutdownAsync()
    {
        if (_services is { } services)
        {
            await services.DisposeAsync().ConfigureAwait(false);
        }

        _services = null;
        _theme = null;
        _menu = null;
    }
}
