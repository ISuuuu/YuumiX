namespace YuumiX.App.Controls;

/// <summary>
/// 工具页 / 设置页共用的正文列宽度约束（这两页是「卡片长列表」，行长需要上限；
/// 其余页面如生涯、公告是数据密集页，正文铺满即可，不套本约束）。
///
/// <para><b>为什么用显式 <c>Width</c> 而不是 <c>MaxWidth + HAlign(Stretch)</c></b></para>
/// WinUI 的 <c>FrameworkElement</c> 在「Stretch 但被 MaxWidth 夹紧」时，居中偏移是按**期望尺寸**
/// 算的，而不是按 MaxWidth：本页正文的期望宽度只有约 627（由最宽的一张卡片决定），
/// 于是内容以 627 为基准居中，实际宽度却是 1100，整体右移并向右溢出被裁掉。
/// 实测（<c>ScrollViewer</c> viewport = 1199）：期望 627、实际 1100、左偏移 287，
/// 与 <c>(1199 - 627) / 2</c> 完全吻合；把上限从 800 改成 1100 只改变溢出量，偏移量不变。
/// 显式 <c>Width</c> 不触发夹紧，因此 <c>HAlign(Center)</c> 的偏移是
/// <c>(viewport - Width) / 2</c>，窄窗口下 Width 收缩到可用宽度时也不会出现裁切。
/// </summary>
public static class ContentColumn
{
    /// <summary>正文列最大宽度（DIP）。超过此宽后两侧留白对称增长，保证单行字数可读。</summary>
    public const double MaxWidth = 1100;

    /// <summary>可用宽度小于该值时不再收窄（宁可让窗口更窄，也不把内容压到不可读）。</summary>
    public const double MinWidth = 320;

    /// <summary>按滚动区可用宽度算出正文列宽度。</summary>
    public static double Fit(double viewportWidth) =>
        double.IsNaN(viewportWidth) || viewportWidth <= 0
            ? MaxWidth
            : Math.Clamp(viewportWidth, MinWidth, MaxWidth);
}
