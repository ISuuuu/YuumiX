using Microsoft.UI.Reactor;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Shell;
using YuumiX.App.Theming;

// 应用入口。Reactor 的约定：没有 Program.cs / App.xaml，直接在顶层语句启动。

// 服务必须在窗口创建之前就绪：配置与日志都是 UI 的前置依赖。
AppHost.Initialize();

// 第三方 WinRT 控件的 XAML 元数据必须在窗口创建前注册，否则 SettingsCard 模板解析失败（空白）。
ToolkitXamlMetadata.Register();

try
{
    // width/height 是窗口**外框**尺寸（DIP），只作为首帧的近似值；
    // 内容区的精确校正由 AppShell 挂载时调用 WindowMetrics.ApplyInitialClientSize 完成
    //（必须等标题栏扩展生效，否则算出来的客户区会带上系统标题栏那 31 DIP）。
    //
    // 为什么首帧不直接给 1216×918：DWM 边框厚度随 DPI / 系统主题变化，
    // 写死外框补偿只在本机成立，而挂载时的校正能实测出来。
    ReactorApp.Run<AppShell>(
        new WindowSpec
        {
            Title = "YuumiX",
            // 外框 = 内容区 + DWM 边框。边框在 DIP 口径下恒为 8（左右各 8、底部 8，随 DPI 等比放大），
            // 因此这里按 8 补偿；挂载后仍会实测校正一次，换系统版本也不会偏。
            Width = WindowMetrics.WindowWidth,
            Height = WindowMetrics.WindowHeight,
            // icon：非打包应用不会自动继承 MSIX 清单里的图标，必须显式交给 AppWindow.SetIcon；
            // 交付的 .ico 含 16–256 多尺寸帧，任务栏与 Alt+Tab 才不会缩放发虚。
            Icon = WindowIcon.FromPath(AppAssets.AppIconPath),
        });
}
finally
{
    // 消息循环退出后刷新并关闭日志文件（容器释放是异步的：部分单例只实现 IAsyncDisposable）
    await AppHost.ShutdownAsync();
}

/// <summary>
/// 主窗口尺寸基线：数值取自旧版 Tauri 窗口配置，是本次「尺寸对齐」的比较基准而不是随手取值。
/// </summary>
internal static class WindowMetrics
{
    /// <summary>
    /// 旧版 <c>tauri.conf.json</c> 主窗口 width —— Tauri 该字段是**内容区**宽度
    /// （旧版 <c>decorations: false</c>，外框与内容区同尺寸，1200×910 的截图就是全部绘制区域）。
    /// </summary>
    internal const double ContentWidth = 1200;

    /// <summary>旧版主窗口 height（内容区）。</summary>
    internal const double ContentHeight = 910;

    /// <summary>
    /// DWM 可缩放边框在 DIP 口径下的厚度（Win11 的 <c>SM_CXSIZEFRAME + SM_CXPADDEDBORDER</c>）。
    /// 左右各一份、底部一份，顶部因标题栏扩展进内容区而为 0。
    /// </summary>
    private const double FrameThickness = 8;

    /// <summary>窗口外框宽度：内容区 + 左右边框。</summary>
    internal const double WindowWidth = ContentWidth + FrameThickness * 2;

    /// <summary>窗口外框高度：内容区 + 底部边框。</summary>
    internal const double WindowHeight = ContentHeight + FrameThickness;
}
