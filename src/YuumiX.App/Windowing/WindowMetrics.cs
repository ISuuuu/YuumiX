namespace YuumiX.App.Windowing;

using Microsoft.UI.Reactor;
using Microsoft.UI.Windowing;

/// <summary>
/// 主窗口尺寸基准与 DPI / 界面缩放计算（界面缩放方案 B）。
/// </summary>
public static class WindowMetrics
{
    /// <summary>基础窗口内容宽度（DIP，100% 缩放下尺寸）。</summary>
    public const double BaseWidth = 1200;

    /// <summary>基础窗口内容高度（DIP，100% 缩放下尺寸）。</summary>
    public const double BaseHeight = 910;

    /// <summary>
    /// 将配置中的 DpiScale 转换为缩放系数。
    /// 支持 "Auto" (1.0), "100" (1.0), "125" (1.25), "150" (1.5)。
    /// </summary>
    public static double ParseScaleFactor(string? dpiScale) => dpiScale switch
    {
        "125" => 1.25,
        "150" => 1.5,
        _ => 1.0,
    };

    /// <summary>
    /// 根据配置字符串计算等比调整后的窗口尺寸（DIP）。
    /// </summary>
    public static (int Width, int Height) CalculateWindowSize(string? dpiScale) =>
        CalculateWindowSize(ParseScaleFactor(dpiScale));

    /// <summary>
    /// 根据缩放系数计算等比调整后的窗口尺寸（DIP）。
    /// </summary>
    public static (int Width, int Height) CalculateWindowSize(double scaleFactor)
    {
        var width = (int)Math.Round(BaseWidth * scaleFactor);
        var height = (int)Math.Round(BaseHeight * scaleFactor);
        return (width, height);
    }

    /// <summary>
    /// 检查指定窗口当前是否处于最大化状态。
    /// </summary>
    public static bool IsMaximized(ReactorWindow window)
    {
        if (window.State == WindowState.Maximized)
        {
            return true;
        }

        if (window.AppWindow?.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Maximized)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 当窗口未最大化时，根据缩放配置即时调整窗口大小。
    /// 返回是否实际执行了窗口尺寸调整。
    /// </summary>
    public static bool ApplyScaleToWindow(ReactorWindow? window, string? dpiScale)
    {
        if (window is null || IsMaximized(window))
        {
            return false;
        }

        var (width, height) = CalculateWindowSize(dpiScale);
        window.SetSize(width, height);
        return true;
    }
}
