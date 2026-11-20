using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Diagnostics;
using YuumiX.Core.Features.Opgg;
using YuumiX.Core.Persistence;
using YuumiX.Core.Runtime;
using YuumiX.Core.ServiceRegistration;

namespace YuumiX.Core;

/// <summary>
/// 应用组合根：DI 容器、日志、配置的唯一构造点。
///
/// 生命周期刻意不使用 <c>Microsoft.Extensions.Hosting</c> 的 <c>IHost</c>：
/// 宿主是 <c>ReactorApp.Run</c>，它自带窗口与消息循环，再叠一层 Host 只会让启动路径
/// 出现两个「谁先启动」的问题。
/// </summary>
public sealed class AppServices : IAsyncDisposable
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

    /// <summary>多语言本地化服务。</summary>
    public Localization.I18nService I18n => Services.GetRequiredService<Localization.I18nService>();

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
        // 加载前先做旧版 Yuumi 数据目录的一次性迁移：自己的 config.json 尚不存在时，
        // 从 %APPDATA%/Yuumi 复制配置与用户数据（路人集库、搜索历史）过来。
        // 静态类不能作 CreateLogger<T> 的类型参数，改用类别名字符串。
        LegacyDataMigrator.Migrate(paths, loggerFactory.CreateLogger("LegacyDataMigrator"));
        var config = store.Load();

        var collection = new ServiceCollection();
        collection.AddSingleton(paths);
        collection.AddSingleton(fileProvider);
        collection.AddSingleton(loggerFactory);
        collection.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        collection.AddSingleton(store);
        collection.AddSingleton(config);

        // ── 多语言本地化服务 ──────────────────────────────────────
        collection.AddSingleton(provider => new Localization.I18nService(
            provider.GetRequiredService<AppConfig>().Personalization.Language,
            provider.GetService<ILogger<Localization.I18nService>>()));

        // ── 按领域模块化注册 ──────────────────────────────────────
        collection
            .AddLcuServices()
            .AddAssetsAndGameDataServices()
            .AddFeatureServices()
            .AddAutomationServices()
            .AddCloudAndUpdateServices();

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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 顺序要紧：先发关闭信号，让后台循环尽快退出并写完最后一条日志；
        // 再异步关容器——IAutomationAgent 等单例只实现 IAsyncDisposable，
        // 用同步 Dispose 关容器会抛 InvalidOperationException，等于每次退出都留一条未处理异常。
        // 最后关日志工厂。
        _shutdown.Cancel();

        if (Services is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            (Services as IDisposable)?.Dispose();
        }

        LoggerFactory.Dispose();
        _shutdown.Dispose();
    }
}
