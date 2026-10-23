using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Diagnostics;
using YuumiX.Core.Lcu;
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
        collection.AddSingleton(provider => new LcuConnection(
            provider.GetRequiredService<LcuProcessFinder>(),
            provider.GetRequiredService<AppConfig>().Functions.ApiConcurrencyNumber,
            provider.GetRequiredService<ILoggerFactory>()));

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
