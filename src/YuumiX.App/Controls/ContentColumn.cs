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
/// 显式 <c>Width</c> 不触发夹紧，因此偏移是 <c>(viewport - Width) / 2</c>，
/// 窄窗口下 Width 收缩到可用宽度时也不会出现裁切。
///
/// <para><b>为什么禁止 <c>HAlign(Center)</c>，必须用 <see cref="CenterOffset"/> + 左对齐</b></para>
/// Center 的左偏移 = <c>(viewportWidth - columnWidth) / 2</c>，宽度差为奇数时偏移带 .5 尾数
/// （视口 1341、列宽 1100 时左偏移 120.5）。主树控件有布局取整，收起态看不出问题；
/// ComboBox 弹层宿主在独立 Popup 树，会继承这个未取整的锚点位置，弹层文字被半像素重采样
/// 而发虚（实测弹层文字笔画过渡带 2~4px，主树 1px）。调用方必须用整数左边距 + 左对齐，
/// 不能依赖居中布局的隐式偏移。
///
/// <para><b>为什么边距要按像素网格量化，仅取整还不够</b></para>
/// 渲染缩放非 100% 时整数 DIP 不落在整数物理像素上（系统 125% 下 49 DIP = 61.25 px，
/// 弹层依旧发虚）；界面缩放（Viewbox 档位）会把子树坐标再乘一次缩放系数，同理。
/// 边距必须落在「最小整数步长 s 使 s × 缩放系数恰为整数」的网格上（100%→1、125%→4、150%→2），
/// 锚点才会恰好落在像素网格。两侧留白因此最多相差一个步长（≤4 DIP），视觉上仍居中。
/// </summary>
public static class ContentColumn
{
    /// <summary>正文列最大宽度（DIP）。超过此宽后两侧留白对称增长，保证单行字数可读。</summary>
    public const double MaxWidth = 1100;

    /// <summary>可用宽度小于该值时不再收窄（宁可让窗口更窄，也不把内容压到不可读）。</summary>
    public const double MinWidth = 320;

    /// <summary>按滚动区可用宽度算出正文列宽度。取整：列宽带尾数会把 .5 传给列内右对齐控件的锚点。</summary>
    public static double Fit(double viewportWidth) =>
        double.IsNaN(viewportWidth) || viewportWidth <= 0
            ? MaxWidth
            : Math.Floor(Math.Clamp(viewportWidth, MinWidth, MaxWidth));

    /// <summary>
    /// 计算正文列的整数左边距。<paramref name="pixelScale"/> 是「子树 DIP → 物理像素」的
    /// 总缩放系数（系统 DPI 缩放 × 界面缩放档位）。右侧允许比左侧多不足一个步长，
    /// 以保证弹层锚点恰好落在像素网格上。
    /// </summary>
    public static double CenterOffset(double viewportWidth, double columnWidth, double pixelScale)
    {
        if (!double.IsFinite(viewportWidth) || !double.IsFinite(columnWidth) || !double.IsFinite(pixelScale))
        {
            return 0;
        }

        var raw = Math.Max(0, (viewportWidth - columnWidth) / 2);
        if (raw <= 0)
        {
            return 0;
        }

        var step = PixelGridStep(pixelScale);
        return Math.Floor(raw / step) * step;
    }

    /// <summary>
    /// 最小整数步长 s，使 s × pixelScale 恰为整数物理像素（100%→1、125%→4、150%→2；
    /// 系统缩放与界面缩放叠加出的 1.5625 → 16 也能覆盖）。找不到（≤0 或异常值）回退 1。
    /// </summary>
    private static double PixelGridStep(double pixelScale)
    {
        if (pixelScale <= 0)
        {
            return 1;
        }

        for (var step = 1; step <= 32; step++)
        {
            var product = step * pixelScale;
            if (Math.Abs(product - Math.Round(product)) < 0.01)
            {
                return step;
            }
        }

        return 1;
    }
}
