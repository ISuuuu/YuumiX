using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Pages.Bench;
using YuumiX.Core.Features.AramBench;

namespace YuumiX.App.Windowing;

/// <summary>
/// 大乱斗板凳席悬浮窗的宿主：单例创建、顶部居中定位、置顶、以及「用户已关闭」的会话内记忆。
/// 对应旧版 <c>commands/tools.rs::show_bench_overlay_window</c>（Tauri 另开一个
/// <c>bench-overlay</c> 窗口，550×70、无边框、透明、置顶、不进任务栏）。
///
/// 与旧版的差异（WinUI 3 侧的平台限制，不是取舍）：
/// <list type="bullet">
///   <item><b>不做窗口级透明</b>：旧版靠 Tauri 的 <c>transparent(true)</c> + CSS 半透明卡片。
///         WinUI 3 要让整个 HWND 半透明必须走 <c>SetLayeredWindowAttributes</c>，
///         而分层窗口上的 XAML 内容在部分机器上会整体不可见；改用不透明卡片 + 主题色，
///         在客户端窗口之上同样清晰，且深浅主题都成立（AGENTS.md §2）。</item>
///   <item><b>不在打开时抢焦点</b>：旧版 <c>set_focus</c> 会打断选人界面里的键盘输入；
///         这里用 <c>ActivateOnOpen = false</c>，窗口照样可见可点，但不夺焦点。</item>
/// </list>
/// </summary>
public static class AramBenchWindowHost
{
    public const string Title = "大乱斗板凳席";

    /// <summary>窗口宽度（DIP）。与旧版 <c>inner_size(550.0, 70.0)</c> 同量级。</summary>
    public const double WindowWidth = 560;

    /// <summary>窗口高度（DIP）：44 头像 + 上下各 12 的呼吸空间 + 1px 描边。</summary>
    public const double WindowHeight = 72;

    /// <summary>距屏幕上沿的间距，避免贴边后圆角被切。</summary>
    private const double ScreenTopMargin = 8;

    private const uint SpiGetWorkArea = 0x0030;

    private static readonly Lock Gate = new();

    private static ReactorWindow? _window;

    /// <summary>
    /// 本次选人阶段用户是否手动关过悬浮窗。关过就不再自动弹出——
    /// 对齐旧版 <c>benchOverlayRequested</c> 的语义（离开选人阶段才复位）。
    /// </summary>
    private static bool _dismissedByUser;

    /// <summary>悬浮窗当前是否打开（供诊断/日志使用）。</summary>
    public static bool IsOpen
    {
        get
        {
            lock (Gate)
            {
                return _window is not null;
            }
        }
    }

    /// <summary>
    /// 按最新状态同步显隐。由 <see cref="AramBenchOverlayCoordinator"/> 在状态变化时调用。
    ///
    /// 判定规则与旧版一致：<c>EnableBenchOverlay</c> 开启 ∧ 处于选人阶段 ∧ LCU 报告本局有板凳席
    /// （<c>benchEnabled</c>，只有大乱斗族模式为 true）。三者任一不满足即关闭。
    /// </summary>
    public static void Sync(AramBenchState state, bool featureEnabled)
    {
        lock (Gate)
        {
            bool inBenchSelect = state.IsChampSelect && state.BenchEnabled;

            if (!inBenchSelect)
            {
                // 离开选人（或本局根本没有板凳席）：连「用户已关闭」一起复位，
                // 下一局选人才能重新弹出（旧版在 showBenchOverlay(false) 里做同样的事）。
                _dismissedByUser = false;
                CloseWindowLocked();
                return;
            }

            if (!featureEnabled)
            {
                // 中途关掉开关：立即收起，但不动 _dismissedByUser——重新打开开关应能再次弹出。
                CloseWindowLocked();
                return;
            }

            if (_dismissedByUser || _window is not null)
            {
                return;
            }

            OpenWindowLocked();
        }
    }

    /// <summary>用户主动关闭（悬浮窗右上角 ×）：本次选人阶段不再自动弹回。</summary>
    public static void Dismiss()
    {
        lock (Gate)
        {
            _dismissedByUser = true;
            CloseWindowLocked();
        }
    }

    private static void OpenWindowLocked()
    {
        var (x, y) = TopCenterPosition();

        var spec = new WindowSpec
        {
            Title = Title,
            Width = WindowWidth,
            Height = WindowHeight,
            ResizeMode = WindowResizeMode.NoResize,
            StartPosition = WindowStartPosition.Manual,
            ManualPosition = (x, y),
            // 无边框 + 无标题栏：悬浮窗只要那一排英雄头像
            Style = WindowStyle.None,
            CornerStyle = WindowCornerStyle.RoundedSmall,
            // AlwaysOnTop 而非 Floating：Floating 只在 Reactor 自己的窗口之间保持在前，
            // 而这里必须压在英雄联盟客户端之上。
            Level = WindowLevel.AlwaysOnTop,
            ShowInTaskbar = false,
            ShowInSwitcher = false,
            // 允许按住空白处拖动（配合组件里拖动条上的 BeginDragMove）
            IsMovableByBackground = true,
            // 不抢焦点：选人阶段用户可能正在筛选英雄/打字（NoActivate 赋予 WS_EX_NOACTIVATE，ActivateOnOpen 保持 true 窗口才会呈现）
            NoActivate = true,
        };

        try
        {
            var window = ReactorApp.OpenWindow(spec, () => new AramBenchWindowRoot());
            window.Closing += OnWindowClosing;
            _window = window;

            AppHost.Services.LoggerFactory
                .CreateLogger(typeof(AramBenchWindowHost))
                .LogInformation("大乱斗板凳席悬浮窗已打开于 ({X}, {Y})", x, y);
        }
        catch (Exception ex)
        {
            // 悬浮窗打不开不该影响主流程（选人本身仍可正常进行）
            AppHost.Services.LoggerFactory
                .CreateLogger(typeof(AramBenchWindowHost))
                .LogWarning(ex, "大乱斗板凳席悬浮窗创建失败");
        }
    }

    private static void CloseWindowLocked()
    {
        var window = _window;
        if (window is null)
        {
            return;
        }

        // 先摘引用再关：Closing 处理器据此区分「程序性关闭」与「用户点 ×」
        _window = null;

        try
        {
            window.Closing -= OnWindowClosing;
            window.Close();

            AppHost.Services.LoggerFactory
                .CreateLogger(typeof(AramBenchWindowHost))
                .LogInformation("大乱斗板凳席悬浮窗已关闭");
        }
        catch (Exception ex)
        {
            AppHost.Services.LoggerFactory
                .CreateLogger(typeof(AramBenchWindowHost))
                .LogDebug(ex, "关闭大乱斗板凳席悬浮窗失败");
        }
    }

    /// <summary>
    /// 用户通过系统途径关闭窗口（Alt+F4、任务栏等处）：记为「本次选人不再弹出」。
    /// 程序性关闭会先摘掉 <see cref="_window"/> 并解绑本处理器，因此不会走到这里。
    /// </summary>
    private static void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_window, sender))
            {
                return;
            }

            _window = null;
            _dismissedByUser = true;
        }
    }

    /// <summary>
    /// 主显示器工作区顶部居中。与旧版一致取**主**显示器而非鼠标所在屏：
    /// 选人界面通常就在主屏，跟随鼠标会让悬浮窗位置随机跳动。
    /// </summary>
    private static (double X, double Y) TopCenterPosition()
    {
        var workArea = new Rect();
        if (!SystemParametersInfo(SpiGetWorkArea, 0, ref workArea, 0))
        {
            // 取不到工作区就交给系统默认放置，总比丢到 (0,0) 更可预期
            return (100, 100);
        }

        double width = workArea.Right - workArea.Left;
        double x = workArea.Left + Math.Max(0, (width - WindowWidth) / 2);
        double y = workArea.Top + ScreenTopMargin;
        return (Math.Round(x), Math.Round(y));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref Rect pvParam, uint fWinIni);
}

/// <summary>
/// 悬浮窗根容器：与 <c>OpggWindowRoot</c> 同构，为独立窗口提供
/// <see cref="AppHost.Context"/> 与 <see cref="AppHost.ThemeContext"/>。
///
/// 独立窗口是**另一棵**元素树，读不到主窗口 Provide 的上下文；
/// 不 Provide 的话 <c>LcuImage</c> 会退化到静态兜底，主题也不会跟随。
/// </summary>
internal sealed class AramBenchWindowRoot : Component
{
    public override Element Render() =>
        Component<AramBenchOverlayView>()
            .Provide(AppHost.Context, AppHost.Services)
            .Provide(AppHost.ThemeContext, AppHost.Theme);
}
