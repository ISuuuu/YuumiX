using Microsoft.UI.Reactor;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Shell;
using YuumiX.App.Theming;

// 应用入口。Reactor 的约定：没有 Program.cs / App.xaml，直接在顶层语句启动。
// width/height 为 DIP（100% 缩放下与物理像素一致）。

// 服务必须在窗口创建之前就绪：配置与日志都是 UI 的前置依赖。
AppHost.Initialize();

// 第三方 WinRT 控件的 XAML 元数据必须在窗口创建前注册，否则 SettingsCard 模板解析失败（空白）。
ToolkitXamlMetadata.Register();

try
{
    // icon：非打包应用不会自动继承 MSIX 清单里的图标，必须显式交给 AppWindow.SetIcon；
    // 交付的 .ico 含 16–256 多尺寸帧，任务栏与 Alt+Tab 才不会缩放发虚。
    ReactorApp.Run<AppShell>(
        "YuumiX",
        width: 1280,
        height: 800,
        icon: WindowIcon.FromPath(AppAssets.AppIconPath));
}
finally
{
    // 消息循环退出后刷新并关闭日志文件
    AppHost.Shutdown();
}
