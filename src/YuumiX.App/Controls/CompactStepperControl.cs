using System;
using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;

namespace YuumiX.App.Controls;

/// <summary>
/// 紧凑型数值步进器：替代原生 NumberBox。
/// 样式为 [ − ]  N  [ + ]（宽约 110px，高 30px）。
/// 彻底消除 WinUI ScrollView 滚动时光标自动跳入 NumberBox 导致弹出上下箭头的焦点问题。
/// </summary>
/// <remarks>
/// 两个按钮必须 <c>Padding(0)</c>：WinUI 按钮默认 Padding(8,5,8,6) 只给内容留 12px 宽，
/// 单字符字形会在窄内容区内被裁掉右侧（加号右臂缺失、减号变短），去掉 Padding 后字形在 28px 全宽内居中渲染。
/// </remarks>
public static class CompactStepperControl
{
    public static Element Render(
        int value,
        int min,
        int max,
        int step,
        Action<int> onChange,
        YuumiPalette palette,
        string? automationName = null)
    {
        return Border(
            HStack(0,
                Button("−", () =>
                {
                    if (value - step >= min)
                    {
                        onChange(value - step);
                    }
                })
                .Width(28)
                .Height(28)
                .Padding(new Thickness(0))
                .Background("#00000000")
                .BorderThickness(new Thickness(0))
                .AutomationName(automationName is null ? "减少" : $"{automationName} 减少"),

                BodyStrong(value.ToString(CultureInfo.InvariantCulture))
                    .Width(44)
                    .TextAlignment(TextAlignment.Center)
                    .VAlign(VerticalAlignment.Center),

                Button("+", () =>
                {
                    if (value + step <= max)
                    {
                        onChange(value + step);
                    }
                })
                .Width(28)
                .Height(28)
                .Padding(new Thickness(0))
                .Background("#00000000")
                .BorderThickness(new Thickness(0))
                .AutomationName(automationName is null ? "增加" : $"{automationName} 增加")
            )
        )
        .Width(110)
        .Height(30)
        .CornerRadius(6)
        .Background(palette.HoverBg)
        .WithBorder(palette.BorderSubtle, 1);
    }

    public static Element RenderDouble(
        double value,
        double min,
        double max,
        double step,
        Action<double> onChange,
        YuumiPalette palette,
        string? automationName = null)
    {
        return Border(
            HStack(0,
                Button("−", () =>
                {
                    if (value - step >= min - 0.001)
                    {
                        onChange(Math.Round(value - step, 1));
                    }
                })
                .Width(28)
                .Height(28)
                .Padding(new Thickness(0))
                .Background("#00000000")
                .BorderThickness(new Thickness(0))
                .AutomationName(automationName is null ? "减少" : $"{automationName} 减少"),

                BodyStrong(value.ToString("0.#", CultureInfo.InvariantCulture))
                    .Width(44)
                    .TextAlignment(TextAlignment.Center)
                    .VAlign(VerticalAlignment.Center),

                Button("+", () =>
                {
                    if (value + step <= max + 0.001)
                    {
                        onChange(Math.Round(value + step, 1));
                    }
                })
                .Width(28)
                .Height(28)
                .Padding(new Thickness(0))
                .Background("#00000000")
                .BorderThickness(new Thickness(0))
                .AutomationName(automationName is null ? "增加" : $"{automationName} 增加")
            )
        )
        .Width(110)
        .Height(30)
        .CornerRadius(6)
        .Background(palette.HoverBg)
        .WithBorder(palette.BorderSubtle, 1);
    }
}

