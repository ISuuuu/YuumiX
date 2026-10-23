using Microsoft.UI.Reactor;
using YuumiX.App.Shell;

// 应用入口。Reactor 的约定：没有 Program.cs / App.xaml，直接在顶层语句启动。
// width/height 为 DIP（100% 缩放下与物理像素一致）。
ReactorApp.Run<AppShell>("YuumiX", width: 1280, height: 800);
