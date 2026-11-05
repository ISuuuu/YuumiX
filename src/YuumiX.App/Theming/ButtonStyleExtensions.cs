using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace YuumiX.App.Theming;

/// <summary>
/// 按钮样式扩展方法（对齐 Fluent 3.0 按钮体系）。
/// </summary>
public static class ButtonStyleExtensions
{
    /// <summary>
    /// 次要/幽灵按钮（Subtle 样式）：透明背景，悬浮时微亮，用于次要操作、刷新、取消或卡片内辅助动作。
    /// </summary>
    public static ButtonElement GhostButton(this ButtonElement button) =>
        button.Resources(static resources => resources
            .Set("ButtonBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
            .Set("ButtonBackgroundPointerOver", Theme.Ref("SubtleFillColorSecondaryBrush"))
            .Set("ButtonBackgroundPressed", Theme.Ref("SubtleFillColorTertiaryBrush"))
            .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"))
            .Set("ButtonBorderBrushPointerOver", Theme.Ref("SubtleFillColorTransparentBrush"))
            .Set("ButtonBorderBrushPressed", Theme.Ref("SubtleFillColorTransparentBrush"))
            .Set("ButtonBorderBrushDisabled", Theme.Ref("SubtleFillColorTransparentBrush")));

    /// <summary>
    /// 双态切换按钮（Tab、筛选按钮等「选中态由 isSelected 驱动」的场景）的指针态资源：
    /// 选中时走强调色体系（AccentButton*），未选中时保持系统默认按钮的指针态。
    /// 必须用资源覆写而非本地 Background/Foreground 提供悬停反馈——
    /// WinUI 按钮的 PointerOver 视觉状态直接改写模板根 Border 的背景，本地值在悬停期间不生效。
    /// </summary>
    public static ButtonElement TogglePointerOverStates(this ButtonElement button, bool isSelected) =>
        button.Resources(resources => resources
            .Set("ButtonBackgroundPointerOver", Theme.Ref(isSelected ? "AccentButtonBackgroundPointerOver" : "ButtonBackgroundPointerOver"))
            .Set("ButtonBackgroundPressed", Theme.Ref(isSelected ? "AccentButtonBackgroundPressed" : "ButtonBackgroundPressed"))
            .Set("ButtonForegroundPointerOver", Theme.Ref(isSelected ? "AccentButtonForegroundPointerOver" : "ButtonForegroundPointerOver"))
            .Set("ButtonForegroundPressed", Theme.Ref(isSelected ? "AccentButtonForegroundPressed" : "ButtonForegroundPressed")));

    /// <summary>
    /// OP.GG 绿色主操作按钮（设为当前符文页、一键应用出装）：
    /// 正常态与悬停/按下态均覆写资源，避免指针进入时模板回退为系统亮白底色造成白底白字不可读。
    /// </summary>
    public static ButtonElement OpggGreenButton(this ButtonElement button) =>
        button
            .CornerRadius(YuumiTheme.ControlRadius)
            .Resources(static resources => resources
                .Set("ButtonBackground", "#2fbc5d")
                .Set("ButtonBackgroundPointerOver", "#25a44e")
                .Set("ButtonBackgroundPressed", "#1e853f")
                .Set("ButtonBackgroundDisabled", Theme.Ref("ButtonBackgroundDisabled"))
                .Set("ButtonForeground", "#FFFFFF")
                .Set("ButtonForegroundPointerOver", "#FFFFFF")
                .Set("ButtonForegroundPressed", "#FFFFFF")
                .Set("ButtonForegroundDisabled", Theme.Ref("ButtonForegroundDisabled"))
                .Set("ButtonBorderBrush", "Transparent")
                .Set("ButtonBorderBrushPointerOver", "Transparent")
                .Set("ButtonBorderBrushPressed", "Transparent")
                .Set("ButtonBorderBrushDisabled", "Transparent"));
}
