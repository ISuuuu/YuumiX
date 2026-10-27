using Microsoft.UI.Reactor;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Shell;
using YuumiX.App.Theming;
using YuumiX.App.Windowing;

// 应用入口。Reactor 的约定：没有 Program.cs / App.xaml，直接在顶层语句启动。

// 服务必须在窗口创建之前就绪：配置与日志都是 UI 的前置依赖。
AppHost.Initialize();

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
    ReactorApp.Run<AppShell>(
        "YuumiX",
        width: initialWidth,
        height: initialHeight,
        icon: WindowIcon.FromPath(AppAssets.AppIconPath));
}
finally
{
    // 消息循环退出后刷新并关闭日志文件（容器释放是异步的：部分单例只实现 IAsyncDisposable）
    await AppHost.ShutdownAsync();
}
