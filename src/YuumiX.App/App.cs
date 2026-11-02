using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Diagnostics;
using YuumiX.App.Shell;
using YuumiX.App.Theming;
using YuumiX.App.Windowing;

// 应用入口。Reactor 的约定：没有 Program.cs / App.xaml，直接在顶层语句启动。

// 单例：必须早于 AppHost.Initialize()。初始化会拉起 LCU 连接、自动化宿主与上传队列，
// 开启「随应用启动客户端」时还会真的去启动英雄联盟——第二份实例不能走到那一步。
// 被拦下的进程已经尽力把已运行的主窗拉到前台（见 SingleInstance），这里直接结束。
// 不传 logger：日志基础设施属于 AppHost，为记一行「已存在实例」而提前初始化它，
// 会让被拦截的进程去打开首实例正在写的日志文件，代价大于收益。
//
// Debug 构建不设单例：开发/测试期需要同时跑多份（新旧构建对照、配合 devtools 反复重启、
// 走一遍升级替换路径），而被拦下的进程会直接 return，连断点都停不下来。
// 代价只落在开发机：多份实例共享同一份 config.json / 日志 / saved_players.db，属刻意为之。
#if !DEBUG
if (!SingleInstance.TryAcquire())
{
    return;
}
#endif

// 先接好异常出口，再初始化配置、服务与 XAML，避免启动失败时尚未挂载 AppShell。
var crashLogger = new CrashLogger(AppContext.BaseDirectory,
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuumiX", "log"));
AppDomain.CurrentDomain.UnhandledException += crashLogger.OnUnhandledException;
TaskScheduler.UnobservedTaskException += crashLogger.OnUnobservedTaskException;
ReactorApplication.OnUnhandledException = crashLogger.OnUiUnhandledException;

// 服务必须在窗口创建之前就绪：配置与日志都是 UI 的前置依赖。
AppHost.Initialize();
crashLogger.Logger = AppHost.Services.LoggerFactory.CreateLogger("YuumiX.Crash");

// 第三方 WinRT 控件的 XAML 元数据必须在窗口创建前注册，否则 SettingsCard 模板解析失败（空白）。
ToolkitXamlMetadata.Register();

var config = AppHost.Services.Config;
var (initialWidth, initialHeight) = WindowMetrics.CalculateWindowSize(config.Personalization.DpiScale);

try
{
    // width/height 是窗口**外框**尺寸（DIP），只作为首帧的近似值；
    // 内容区的精确校正由 AppShell 挂载时调用 WindowMetrics.ApplyInitialClientSize 完成
    //（必须等标题栏扩展生效，否则算出来的客户区会带上系统标题栏那 31 DIP）。
    //
    // 为什么首帧不直接给 1216×918：DWM 边框厚度随 DPI / 系统主题变化，
    // 写死外框补偿只在本机成立，而挂载时的校正能实测出来。
    //
    // WithPersistence：跨启动记住窗口位置/尺寸——AppShell 在 Closing 时 SavePlacement，
    // 下次建窗由框架还原；无存档（首次启动/存储被清）走 Default 起始位，与旧行为一致。
    //
    // MinWidth/MinHeight：从拖拽源头挡住"窗口被缩到极小后被持久化"——布局存档对
    // 合法的小尺寸原样还原（框架 IsPlausiblePlacement 只拦荒谬值），下次开局会近乎无法操作。
    // 旧版 Yuumi（Tauri）没有最小尺寸，此约束是 YuumiX 新增的防护，值取桌面应用惯例，可调。
    var spec = new WindowSpec
    {
        Title = "YuumiX",
        Width = initialWidth,
        Height = initialHeight,
        MinWidth = 800,
        MinHeight = 600,
        Icon = WindowIcon.FromPath(AppAssets.AppIconPath),
    }.WithPersistence("main", WindowStartPosition.Default);
    ReactorApp.Run<AppShell>(spec);
}
finally
{
    // 消息循环退出后刷新并关闭日志文件（容器释放是异步的：部分单例只实现 IAsyncDisposable）
    await AppHost.ShutdownAsync();

    // 先放掉单例令牌再让进程退出：用户「退出 → 立刻双击重开」时不该被误判为已有实例。
    SingleInstance.Release();
}
