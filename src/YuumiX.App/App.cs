using Microsoft.UI.Reactor;
using YuumiX.App.Composition;
using YuumiX.App.Shell;

// 应用入口。Reactor 的约定：没有 Program.cs / App.xaml，直接在顶层语句启动。
// width/height 为 DIP（100% 缩放下与物理像素一致）。

// 服务必须在窗口创建之前就绪：配置与日志都是 UI 的前置依赖。
AppHost.Initialize();
try
{
    ReactorApp.Run<AppShell>("YuumiX", width: 1280, height: 800);
}
finally
{
    // 消息循环退出后刷新并关闭日志文件
    AppHost.Shutdown();
}
