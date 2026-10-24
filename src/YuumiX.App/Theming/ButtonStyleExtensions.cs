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
    /// 警告/危险按钮（Critical 样式）：红底高亮，用于强提醒或具有破坏性的操作（如重启客户端、删除好友）。
    /// </summary>
    public static ButtonElement CriticalButton(this ButtonElement button) =>
        button.Resources(static resources => resources
            .Set("ButtonBackground", Theme.Ref("SystemFillColorCriticalBrush"))
            .Set("ButtonBackgroundPointerOver", "#D83B01")
            .Set("ButtonBackgroundPressed", "#A80000")
            .Set("ButtonBackgroundDisabled", Theme.Ref("ControlFillColorDisabledBrush"))
            .Set("ButtonForeground", "#FFFFFFFF")
            .Set("ButtonForegroundPointerOver", "#FFFFFFFF")
            .Set("ButtonForegroundPressed", "#FFFFFFFF")
            .Set("ButtonBorderBrush", Theme.Ref("SystemFillColorCriticalBrush"))
            .Set("ButtonBorderBrushPointerOver", "#D83B01")
            .Set("ButtonBorderBrushPressed", "#A80000"));
}
