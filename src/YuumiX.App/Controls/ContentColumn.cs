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

    /// <summary>
    /// 按滚动区可用宽度算出正文列宽度。
    /// <para>
    /// 宽度必须取整，但仅取整宽度还不够：<c>HAlign(Center)</c> 的左偏移是
    /// <c>(viewportWidth - columnWidth) / 2</c>，两个整数相减为奇数时仍会产生半像素偏移。
    /// 主树文字会被布局取整掩盖这个问题，独立 Popup 树中的 <c>ComboBox</c> 弹层则会被重采样，
    /// 表现为「下拉列表文字比收起态虚」。因此调用方必须用 <see cref="CenterOffset"/> 算出整数左边距，
    /// 并改用左对齐，不能继续依赖居中布局隐式计算偏移（AGENTS.md §1.2）。
    /// </para>
    /// </summary>
    public static double Fit(double viewportWidth) =>
        double.IsNaN(viewportWidth) || viewportWidth <= 0
            ? MaxWidth
            : Math.Floor(Math.Clamp(viewportWidth, MinWidth, MaxWidth));

    /// <summary>
    /// 计算正文列的整数左边距。右侧允许比左侧多不足 1 DIP，以保证左侧落在像素网格上。
    /// </summary>
    public static double CenterOffset(double viewportWidth, double columnWidth) =>
        !double.IsFinite(viewportWidth) || !double.IsFinite(columnWidth)
            ? 0
            : Math.Max(0, Math.Floor((viewportWidth - columnWidth) / 2));
}
