using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Assets;
using YuumiX.Core.Automation;
using YuumiX.Core.Diagnostics;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.GameLifecycle;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Opgg;
using YuumiX.Core.Features.Replay;
using YuumiX.Core.Features.SavedPlayers;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Lcu;
using YuumiX.Core.Lcu.Sgp;
using YuumiX.Core.Persistence;
using YuumiX.Core.Runtime;

namespace YuumiX.Core;

/// <summary>
/// 应用组合根：DI 容器、日志、配置的唯一构造点。
///
/// 生命周期刻意不使用 <c>Microsoft.Extensions.Hosting</c> 的 <c>IHost</c>：
/// 宿主是 <c>ReactorApp.Run</c>，它自带窗口与消息循环，再叠一层 Host 只会让启动路径
/// 出现两个「谁先启动」的问题。等到阶段 3 引入 LCU 进程监控这类真正的后台服务时，
/// 再评估是否值得引入 <c>IHostedService</c> 语义。
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    private AppServices(
        RuntimePaths paths,
        ILoggerFactory loggerFactory,
        ServiceProvider services,
        ConfigStore configStore,
        AppConfig config)
    {
        Paths = paths;
        LoggerFactory = loggerFactory;
        Services = services;
        ConfigStore = configStore;
        Config = config;
    }

    /// <summary>运行时路径。</summary>
    public RuntimePaths Paths { get; }

    /// <summary>全局日志工厂。</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>DI 容器，供后续阶段注册的 Core 服务解析。</summary>
    public IServiceProvider Services { get; }

    /// <summary>配置读写器。</summary>
    public ConfigStore ConfigStore { get; }

    /// <summary>已加载的配置。设计为进程内单一实例，所有修改最终经 <see cref="ConfigStore"/> 落盘。</summary>
    public AppConfig Config { get; }

    /// <summary>资源磁盘缓存。</summary>
    public YuumiX.Core.Assets.AssetCache AssetCache => Services.GetRequiredService<YuumiX.Core.Assets.AssetCache>();

    /// <summary>OP.GG HTTP 客户端与缓存。</summary>
    public OpggHttpClient OpggHttpClient => Services.GetRequiredService<OpggHttpClient>();

    /// <summary>OP.GG 业务服务。</summary>
    public IOpggService OpggService => Services.GetRequiredService<IOpggService>();

    /// <summary>公告服务（GitHub NOTICE.md 拉取 + 本地缓存兜底）。</summary>
    public Features.Notice.NoticeService NoticeService =>
        Services.GetRequiredService<Features.Notice.NoticeService>();

    /// <summary>
    /// 进程级关闭令牌。所有后台循环（LCU 轮询、WebSocket 重连）必须监听它，
    /// 否则窗口关闭后进程退不掉——后台任务会把 CLR 拖住。
    /// </summary>
    public CancellationToken ShutdownToken => _shutdown.Token;

    /// <summary>
    /// 构造全部基础设施并完成首次配置加载。
    ///
    /// 启动顺序是固定的：解析路径 → 建日志 → 读配置 → 建容器 → 用配置里的级别调整日志。
    /// 配置读取刻意放在建容器**之前**：容器里的服务需要配置值（如 LCU 并发上限），
    /// 而读配置只依赖日志。反过来做就得引入一个可变的配置持有者来补位。
    ///
    /// 本方法只做构造，**不启动任何后台循环**——<c>LcuConnection</c> 是注册而非自启动，
    /// 由宿主（<c>AppHost.Initialize</c>）决定何时开始连接。
    /// </summary>
    /// <param name="paths">运行时路径；<c>null</c> 时自动探测。</param>
    public static AppServices Create(RuntimePaths? paths = null)
    {
        paths ??= RuntimePaths.Detect();

        // 先以 Info 级别开放日志，保证「配置加载」这件事本身可被记录
        var fileProvider = new RotatingFileLoggerProvider(paths.LogDirectory);
        var loggerFactory = LoggingSetup.CreateFactory(fileProvider);

        var store = new ConfigStore(paths.ConfigFilePath, loggerFactory.CreateLogger<ConfigStore>());
        var config = store.Load();

        var collection = new ServiceCollection();
        collection.AddSingleton(paths);
        collection.AddSingleton(fileProvider);
        collection.AddSingleton(loggerFactory);
        collection.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        collection.AddSingleton(store);
        collection.AddSingleton(config);

        collection.AddSingleton<ILcuProcessSource>(_ => new WindowsLcuProcessSource());
        collection.AddSingleton(provider => new LcuProcessFinder(
            provider.GetRequiredService<ILcuProcessSource>(),
            logger: provider.GetRequiredService<ILogger<LcuProcessFinder>>()));

        // ── 资源层（ADR-11）────────────────────────────────────
        // 缓存目录与 Rust 实现一致（<数据目录>/cache/assets），并共用同一套 FNV 哈希命名，
        // 使迁移期两个版本的缓存可互相复用。
        collection.AddSingleton(provider => new AssetCache(
            AssetCacheDirectory(provider.GetRequiredService<RuntimePaths>()),
            TimeProvider.System,
            provider.GetRequiredService<ILogger<AssetCache>>()));
        collection.AddSingleton(provider => new AssetCacheStamp(
            AssetCacheDirectory(provider.GetRequiredService<RuntimePaths>()),
            provider.GetRequiredService<ILogger<AssetCacheStamp>>()));

        collection.AddSingleton(provider => new LcuConnection(
            provider.GetRequiredService<LcuProcessFinder>(),
            provider.GetRequiredService<AppConfig>().Functions.ApiConcurrencyNumber,
            provider.GetRequiredService<ILoggerFactory>(),
            cacheStamp: provider.GetRequiredService<AssetCacheStamp>()));

        // ── 游戏资源映射表（阶段 4b）────────────────────────────
        // 用提供器而非实例：重连会换传输层，映射表却应当跨重连复用
        collection.AddSingleton(provider => new GameDataLoader(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            provider.GetRequiredService<ILogger<GameDataLoader>>()));
        collection.AddSingleton(provider => new GameDataService(
            provider.GetRequiredService<GameDataLoader>(),
            provider.GetRequiredService<ILogger<GameDataService>>()));

        collection.AddSingleton(provider => new LcuAssetSource(
            () => provider.GetRequiredService<LcuConnection>().Credentials,
            logger: provider.GetRequiredService<ILogger<LcuAssetSource>>()));
        collection.AddSingleton(provider => new CdnAssetSource(
            logger: provider.GetRequiredService<ILogger<CdnAssetSource>>()));
        collection.AddSingleton(provider => new AssetService(
            provider.GetRequiredService<AssetCache>(),
            [
                provider.GetRequiredService<LcuAssetSource>(),
                provider.GetRequiredService<CdnAssetSource>(),
            ],
            AssetService.DefaultConcurrency,
            provider.GetRequiredService<ILogger<AssetService>>()));

        // ── 战绩查询与召唤师（阶段 6）──────────────────────────────
        // 同样用提供器而非实例：重连会换传输层，而服务本身应当跨重连复用——
        // 已结束的对局详情不会因为重连而失效，清掉缓存反而要重新拉一遍。
        collection.AddSingleton<ISgpService>(provider => new SgpService(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            () => provider.GetRequiredService<LcuConnection>().Credentials,
            logger: provider.GetRequiredService<ILogger<SgpService>>()));
        collection.AddSingleton<IMatchService>(provider => new MatchService(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            sgpService: provider.GetRequiredService<ISgpService>(),
            credentialsProvider: () => provider.GetRequiredService<LcuConnection>().Credentials,
            logger: provider.GetRequiredService<ILogger<MatchService>>()));
        collection.AddSingleton<ISummonerService>(provider => new SummonerService(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            logger: provider.GetRequiredService<ILogger<SummonerService>>()));

        // ── 路人集 SQLite 存储（阶段 7）────────────────────────────
        collection.AddSingleton(provider => new SavedPlayersStore(
            provider.GetRequiredService<RuntimePaths>(),
            provider.GetService<ILogger<SavedPlayersStore>>()));

        // ── 生涯（排位、战利品、熟练度与最近队友）──────────────────────────
        collection.AddSingleton<ICareerService>(provider => new CareerService(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            savedPlayersStore: provider.GetRequiredService<SavedPlayersStore>(),
            gameDataService: provider.GetRequiredService<GameDataService>(),
            logger: provider.GetRequiredService<ILogger<CareerService>>()));

        // ── ROFL 对局回放服务 ──────────────────────────────────────
        collection.AddSingleton<IReplayService>(provider => new ReplayService(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            logger: provider.GetRequiredService<ILogger<ReplayService>>()));

        // ── 搜索历史存储 ──────────────────────────────────────────
        collection.AddSingleton(provider => new SearchHistoryStore(
            provider.GetRequiredService<RuntimePaths>(),
            logger: provider.GetService<ILogger<SearchHistoryStore>>()));

        // ── 对局生命周期监控服务（自动刷新战绩/生涯）──────────────────
        collection.AddSingleton(provider => new GameLifecycleService(
            provider.GetRequiredService<LcuConnection>(),
            logger: provider.GetService<ILogger<GameLifecycleService>>()));

        // ── 云端服务（requirements-settings.md S5，对应原 signalr.rs / upload.rs）────
        // Hub 基址与上传 API 共用 General.UploadApiUrl；状态经 EventStream 推给设置页徽标。
        collection.AddSingleton<Realtime.SignalrService>(provider => new Realtime.SignalrService(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetService<ILogger<Realtime.SignalrService>>()));
        collection.AddSingleton<Upload.UploadService>(provider => new Upload.UploadService(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetService<ILogger<Upload.UploadService>>()));

        // ── 在线更新（requirements-settings.md S6，对应原 updater.rs / portable_updater.rs）──
        // GitHub 域名白名单 + 代理 + 24h 更新日志缓存；安装版/便携版双策略见 UpdateService。
        collection.AddSingleton<Updates.UpdateService>(provider => new Updates.UpdateService(
            provider.GetRequiredService<RuntimePaths>(),
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetService<ILogger<Updates.UpdateService>>()));

        // ── 公告（对应旧 NoticePopup.vue 的 fetch_github_text 链路）──
        // GitHub 白名单 + 代理复用见 GitHubClient；本地缓存兜底见 NoticeService。
        collection.AddSingleton<Features.Notice.NoticeService>(provider => new Features.Notice.NoticeService(
            provider.GetRequiredService<RuntimePaths>(),
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetService<ILogger<Features.Notice.NoticeService>>()));

        // ── 自动化与工具箱（阶段 7 / requirements-tools.md）─────────
        collection.AddSingleton<AgentSupervisor>(provider => new AgentSupervisor(
            logger: provider.GetService<ILogger<AgentSupervisor>>()));
        collection.AddSingleton<AgentLifecycle>();

        collection.AddSingleton<IToolsService>(provider => new ToolsService(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            provider.GetRequiredService<ISgpService>(),
            () => provider.GetRequiredService<LcuConnection>().Credentials,
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetService<ILogger<ToolsService>>()));
        collection.AddSingleton<IFriendCleanerService>(provider => new FriendCleanerService(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            provider.GetService<ILogger<FriendCleanerService>>()));

        // ── 客户端路径检测与启动（requirements-settings.md S2，对应原 commands/tools.rs）──
        collection.AddSingleton<LolPathDetector>(new LolPathDetector(
            WindowsRegistryReader.Instance,
            WindowsProcessPathSource.Instance));
        collection.AddSingleton<LolClientLauncher>(provider => new LolClientLauncher(
            WindowsProcessPathSource.Instance));

        // ── OP.GG 英雄梯度与出装推荐 ──────────────────────────────
        collection.AddSingleton(provider => new OpggHttpClient(
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetRequiredService<RuntimePaths>(),
            provider.GetService<ILogger<OpggHttpClient>>()));
        collection.AddSingleton<IOpggService>(provider => new OpggService(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            provider.GetRequiredService<OpggHttpClient>(),
            provider.GetService<ILogger<OpggService>>()));

        collection.AddSingleton<AutoAcceptAgent>(provider => new AutoAcceptAgent(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetRequiredService<AgentSupervisor>(),
            provider.GetRequiredService<AgentLifecycle>(),
            provider.GetService<ILogger<AutoAcceptAgent>>()));
        collection.AddSingleton<AutoReconnectAgent>(provider => new AutoReconnectAgent(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetRequiredService<AgentSupervisor>(),
            provider.GetRequiredService<AgentLifecycle>(),
            provider.GetService<ILogger<AutoReconnectAgent>>()));
        collection.AddSingleton<AutoBanPickAgent>(provider => new AutoBanPickAgent(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetRequiredService<AgentSupervisor>(),
            provider.GetRequiredService<AgentLifecycle>(),
            provider.GetService<IOpggService>(),
            provider.GetService<ILogger<AutoBanPickAgent>>()));
        collection.AddSingleton<AutoCreateLobbyAgent>(provider => new AutoCreateLobbyAgent(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetRequiredService<AgentSupervisor>(),
            provider.GetRequiredService<AgentLifecycle>(),
            provider.GetService<ILogger<AutoCreateLobbyAgent>>()));
        collection.AddSingleton<AramTeamSideAgent>(provider => new AramTeamSideAgent(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetRequiredService<AgentSupervisor>(),
            provider.GetRequiredService<AgentLifecycle>(),
            provider.GetService<ILogger<AramTeamSideAgent>>()));
        collection.AddSingleton<AutoEncounterRecorderAgent>(provider => new AutoEncounterRecorderAgent(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            provider.GetRequiredService<SavedPlayersStore>(),
            provider.GetRequiredService<AgentSupervisor>(),
            provider.GetRequiredService<AgentLifecycle>(),
            provider.GetService<ILogger<AutoEncounterRecorderAgent>>()));
        collection.AddSingleton<AutoTagReminderAgent>(provider => new AutoTagReminderAgent(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetRequiredService<SavedPlayersStore>(),
            provider.GetRequiredService<AgentSupervisor>(),
            provider.GetRequiredService<AgentLifecycle>(),
            provider.GetService<ILogger<AutoTagReminderAgent>>()));
        collection.AddSingleton<AutoInviteAgent>(provider => new AutoInviteAgent(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetRequiredService<AgentSupervisor>(),
            provider.GetService<ILogger<AutoInviteAgent>>()));
        collection.AddSingleton<AutoHonorAgent>(provider => new AutoHonorAgent(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetRequiredService<AgentSupervisor>(),
            provider.GetService<ILogger<AutoHonorAgent>>()));
        collection.AddSingleton<AutoPlayAgainAgent>(provider => new AutoPlayAgainAgent(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetRequiredService<AgentSupervisor>(),
            provider.GetRequiredService<AgentLifecycle>(),
            provider.GetService<ILogger<AutoPlayAgainAgent>>()));

        // ── 多杀自动截图（requirements-settings.md S4，对应原 agents/auto_screenshot.rs）──
        // Live Client Data 是游戏进程的独立 HTTP 端口（127.0.0.1:2999），不走 LCU Transport。
        collection.AddSingleton<MultiKillCaptureAgent>(provider => new MultiKillCaptureAgent(
            () => provider.GetRequiredService<AppConfig>(),
            () => provider.GetService<ISummonerService>(),
            provider.GetService<ILogger<MultiKillCaptureAgent>>()));
        collection.AddSingleton<AutomationHost>();

        // ── 对局信息（GameInfo 模块）────────────────────────────────
        collection.AddSingleton<ILiveClientService>(provider => new LiveClientService(
            logger: provider.GetService<ILogger<LiveClientService>>()));
        collection.AddSingleton<IMasteryService>(provider => new MasteryService(
            () => provider.GetRequiredService<LcuConnection>().Transport));
        collection.AddSingleton<IFateService>(provider => new FateService(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            () => provider.GetService<GameDataService>()?.Current));
        collection.AddSingleton<IGameInfoService>(provider => new GameInfoService(
            () => provider.GetRequiredService<LcuConnection>().Transport,
            provider.GetRequiredService<ISummonerService>(),
            provider.GetRequiredService<IMatchService>(),
            provider.GetRequiredService<ICareerService>(),
            provider.GetRequiredService<IMasteryService>(),
            provider.GetRequiredService<IFateService>(),
            provider.GetRequiredService<AppConfig>(),
            connection: provider.GetRequiredService<LcuConnection>(),
            logger: provider.GetRequiredService<ILogger<GameInfoService>>(),
            sgpService: provider.GetRequiredService<ISgpService>(),
            savedPlayersStore: provider.GetRequiredService<SavedPlayersStore>(),
            liveClientService: provider.GetRequiredService<ILiveClientService>(),
            catalogProvider: () => provider.GetService<GameDataService>()?.Current));

        var services = collection.BuildServiceProvider();

        var logger = loggerFactory.CreateLogger<AppServices>();
        logger.LogInformation(
            "YuumiX 启动：数据目录={DataDirectory}，便携版={Portable}，配置版本={Version}，日志级别={LogLevel}",
            paths.DataDirectory,
            paths.IsPortable,
            config.Version,
            config.General.LogLevel);

        // 配置就绪后才能确定真实的日志级别
        fileProvider.MinLevel = LoggingSetup.MapLogLevel(config.General.LogLevel);

        return new AppServices(paths, loggerFactory, services, store, config);
    }

    /// <summary>资源缓存目录：<c>&lt;数据目录&gt;/cache/assets</c>，与原实现同构。</summary>
    private static string AssetCacheDirectory(RuntimePaths paths) =>
        Path.Combine(paths.DataDirectory, "cache", "assets");

    /// <summary>
    /// 把配置中的日志等级应用到已构建的 provider（设置页改日志等级后即时生效）。
    ///
    /// 之所以能在运行期改：<see cref="LoggingSetup.CreateFactory"/> 把工厂级别放到 Trace，
    /// 真正的过滤只发生在 provider 这一层，因此这里改 <see cref="RotatingFileLoggerProvider.MinLevel"/>
    /// 就是改真实生效的阈值。若哪天把过滤挪回工厂级，本方法会静默失效——不要那样改。
    /// </summary>
    public void ApplyLogLevel(int logLevel)
    {
        if (Services.GetService<RotatingFileLoggerProvider>() is { } provider)
        {
            provider.MinLevel = LoggingSetup.MapLogLevel(logLevel);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 顺序要紧：先发关闭信号，让后台循环尽快退出并写完最后一条日志；
        // 再关容器（触发各服务 Dispose）；最后关日志工厂。
        _shutdown.Cancel();

        (Services as IDisposable)?.Dispose();
        LoggerFactory.Dispose();
        _shutdown.Dispose();
    }
}
