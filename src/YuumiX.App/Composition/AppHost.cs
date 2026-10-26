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

    /// <summary>
    /// 「检测到可用更新」状态上下文。与 <see cref="ThemeContext"/> 同理在根组件上 Provide 一次：
    /// 设置页需要读取启动检查的结果（免得用户为了看版本详情再手动查一次），
    /// 并把自己检查到的结果发布回来。
    /// </summary>
    public static readonly Context<UpdateNoticeState> UpdateNoticeContext = new(null!, "UpdateNoticeState");

    /// <summary>
    /// 后台复查间隔。缓存有效期是 24 小时（<c>UpdateService.CacheTtl</c>），
    /// 缓存命中时 <c>CheckForUpdateAsync</c> 不发任何网络请求，
    /// 因此 6 小时复查的实际网络成本为零；真正的联网频率仍是每 24 小时一次。
    /// </summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    private static AppServices? _services;
    private static ThemeState? _theme;
    private static MenuVisibilityState? _menu;
    private static UpdateNoticeState? _updateNotice;

    /// <summary>进程级服务实例。仅供根组件使用。</summary>
    public static AppServices Services =>
        _services ?? throw new InvalidOperationException("AppHost 尚未初始化，请先调用 Initialize()");

    /// <summary>进程级主题状态。仅供根组件使用（其余组件走 <see cref="ThemeContext"/>）。</summary>
    public static ThemeState Theme =>
        _theme ?? throw new InvalidOperationException("AppHost 尚未初始化，请先调用 Initialize()");

    /// <summary>进程级菜单可见性状态。仅供根组件使用（其余组件走 <see cref="MenuContext"/>）。</summary>
    public static MenuVisibilityState Menu =>
        _menu ?? throw new InvalidOperationException("AppHost 尚未初始化，请先调用 Initialize()");

    /// <summary>进程级更新提示状态。仅供根组件使用（其余组件走 <see cref="UpdateNoticeContext"/>）。</summary>
    public static UpdateNoticeState UpdateNotice =>
        _updateNotice ?? throw new InvalidOperationException("AppHost 尚未初始化，请先调用 Initialize()");

    /// <summary>
    /// 构造 DI 容器、日志与配置，并启动 LCU 连接循环。必须早于任何 UI 渲染。
    ///
    /// 连接在这里启动而不是在 <c>AppServices.Create</c> 里：Create 是纯构造入口，
    /// 单测与工具会调用它，不能顺带拉起后台线程。宿主才是有权决定「什么时候开始连客户端」的一方。
    /// </summary>
    public static void Initialize()
    {
        _services = AppServices.Create();

        // 协调控制台生命周期：若从终端（dotnet run 或 CLI）启动，监听 Ctrl+C 并确保干净退出
        AppLifetime.InitializeConsoleLifetime(_services.LoggerFactory.CreateLogger("YuumiX.Lifetime"));

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
        _updateNotice = new UpdateNoticeState(
            ex => _services.LoggerFactory
                .CreateLogger<UpdateNoticeState>()
                .LogError(ex, "更新提示订阅者抛异常"));
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

        // 上次更新是否半途失败：延迟脚本失败时会留一个标记文件（见 UpdateService.FailureMarkerPath）。
        // 失败发生在主进程退出之后，这是唯一能跨越「退出 → 下次启动」把结果带回给用户的通道；
        // 这里只读一次，实际提示由外壳挂载后弹 Toast 完成。
        try
        {
            var updateService = _services.Services.GetRequiredService<YuumiX.Core.Updates.UpdateService>();
            if (updateService.ConsumeFailureMarker() is { } failureCode)
            {
                _updateNotice.ReportFailure(failureCode);
            }
        }
        catch (Exception ex)
        {
            // 提示功能不该挡住启动：读不到就当没有。
            _services.LoggerFactory
                .CreateLogger<UpdateNoticeState>()
                .LogWarning(ex, "读取「上次更新未完成」标记失败");
        }

        // 「检查更新」开关的消费点：启动时后台静默检查，并在应用驻留期间周期复查
        // （旧 startup_check_update 语义 + 侧边栏红点）。
        StartUpdateChecks(_services);

        // 清理过期更新包：每下载一次就留一个整包（便携版约 300MB），不清会一直堆积。
        // 只删「版本不高于当前」的包与中断留下的 .tmp，正在等安装的新包保留（见 CleanupDownloadCache）。
        var servicesForCleanup = _services;
        var cleanupLogger = _services.LoggerFactory.CreateLogger<YuumiX.Core.Updates.UpdateService>();
        _ = Task.Run(
            () =>
            {
                try
                {
                    servicesForCleanup.Services
                        .GetRequiredService<YuumiX.Core.Updates.UpdateService>()
                        .CleanupDownloadCache();
                }
                catch (Exception ex)
                {
                    cleanupLogger.LogWarning(ex, "启动清理过期更新包失败");
                }
            },
            servicesForCleanup.ShutdownToken);

        // 对局结束后的云端上传（旧 upload.rs：EndOfGame → 拉最新对局 → 入队 → POST 上传 API）。
        // 走队列而不是直接调 UploadService：去重、串行、失败落盘重试这三件事只在队列里有一份实现，
        // 直接调用等于丢掉断网期间的对局（旧版 upload_single_task 的落盘分支）。
        // 只要配置了 General.UploadApiUrl 即自动上传；战绩查询页的 UploadEnabled 专用于翻页补传。
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

                    // 优先使用局内记录的真实对局 ID（对齐旧 upload.rs：LCU 战绩列表落库存在延迟，不能盲查第 0 场）
                    long targetGameId = lifecycle.LastEndedGameId ?? 0;
                    if (targetGameId == 0)
                    {
                        var profile = await summonerService.GetCurrentSummonerAsync().ConfigureAwait(false);
                        if (profile is not null)
                        {
                            matchService.InvalidateCache();
                            var page = await matchService.GetMatchHistoryAsync(profile.Puuid, 0, 0).ConfigureAwait(false);
                            if (page.Games.Count > 0)
                            {
                                targetGameId = page.Games[0].GameId;
                            }
                        }
                    }

                    if (targetGameId > 0)
                    {
                        uploadQueue.Enqueue(targetGameId);
                        // 顺带把以前没传完的一起重试（旧 UploadTrigger 收尾动作）
                        uploadQueue.TriggerPendingRetry();
                    }
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

    /// <summary>
    /// 启动「检查更新」后台循环：立即检查一次，之后每 <see cref="UpdateCheckInterval"/> 复查。
    ///
    /// 三处刻意的设计：
    /// <list type="number">
    ///   <item>放后台线程：GitHub 请求最长 15 秒超时，不能挡住首帧；</item>
    ///   <item>失败只记警告：离线、代理未启动、GitHub 限流都是常态，
    ///         且红点保持上一次检测结果——网络抖动不该把「有更新」的提示清掉；</item>
    ///   <item>每轮复查前重读开关：用户运行期关掉「检查更新」后循环自行退出并熄灭红点，
    ///         否则会出现「功能已关，红点还在提示一个永远不会刷新的结果」。</item>
    /// </list>
    /// </summary>
    private static void StartUpdateChecks(AppServices services)
    {
        var notice = _updateNotice
            ?? throw new InvalidOperationException("更新提示状态未初始化，StartUpdateChecks 必须在 Initialize 内调用");

#if DEBUG
        // 假更新触发器优先于「检查更新」开关：验收红点不该被迫改动用户配置
        // （开发机上把开关关掉是常态，开关挡在前面会让这个触发器形同虚设）。
        if (ReadFakeUpdateOverride() is { } fakeVersion)
        {
            notice.Publish(fakeVersion);
            services.LoggerFactory
                .CreateLogger<YuumiX.Core.Updates.UpdateService>()
                .LogInformation("已启用 YUUMIX_FAKE_UPDATE={Version}：跳过联网检查", fakeVersion.Version);
            return;
        }
#endif

        if (!services.Config.General.EnableCheckUpdate)
        {
            return;
        }

        var updateService = services.Services.GetRequiredService<YuumiX.Core.Updates.UpdateService>();
        var logger = services.LoggerFactory.CreateLogger<YuumiX.Core.Updates.UpdateService>();
        var token = services.ShutdownToken;

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(UpdateCheckInterval);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!services.Config.General.EnableCheckUpdate)
                    {
                        notice.Clear();
                        return;
                    }

                    var info = await updateService.CheckForUpdateAsync(services.Paths.IsPortable, token).ConfigureAwait(false);

                    // 检测期间开关可能刚被关掉：此时不再发布结果，避免刚熄灭的红点又被点亮。
                    if (!services.Config.General.EnableCheckUpdate)
                    {
                        notice.Clear();
                        return;
                    }

                    notice.Publish(info);
                    if (info is not null)
                    {
                        logger.LogInformation("检测到新版本 v{Version}（当前 v{Current}）", info.Version, info.CurrentVersion);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "更新检查失败（离线或网络受限属常态）");
                }

                try
                {
                    if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, token);
    }

#if DEBUG
    /// <summary>
    /// 开发期触发器：环境变量 <c>YUUMIX_FAKE_UPDATE=9.9.9</c> 时跳过联网、直接发布一个假版本。
    ///
    /// 为什么需要：红点只在「远程确实存在更高版本」时出现，本地验收要么等发版、要么临时改代码。
    /// 这个开关把「红点 + 呼吸动画」的验收与 GitHub 的真实状态解耦，
    /// 也便于日后回归检查动画是否还活着（红色小圆点不会因为没有更新而永远无人验证）。
    /// 只在 Debug 构建生效，发行版行为不受影响。
    /// </summary>
    private static YuumiX.Core.Updates.UpdateInfo? ReadFakeUpdateOverride()
    {
        string? raw = Environment.GetEnvironmentVariable("YUUMIX_FAKE_UPDATE")?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        return new YuumiX.Core.Updates.UpdateInfo(
            raw,
            YuumiX.Core.Updates.UpdateService.CurrentVersion,
            "开发期假更新：仅用于验收侧边栏红点与呼吸动画。",
            null,
            // 指向一个必然 404 的 GitHub 资产地址：只为让「下载更新」按钮出现在页面上
            //（没有 URL 时 UI 会走「该版本未提供更新包」分支，主操作卡就验证不到）。
            $"https://github.com/{YuumiX.Core.Updates.GitHubClient.Repository}/releases/download/v{raw}/YuumiX_{raw}_x64-setup.exe");
    }
#endif

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
        _updateNotice = null;

        AppLifetime.Shutdown();
    }
}
