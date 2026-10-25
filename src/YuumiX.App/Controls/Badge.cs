using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;

namespace YuumiX.App.Controls;

/// <summary>
/// 胶囊徽标工厂（R12）：名字行的小强调标签（我/友/敌/连胜/单双/灵活）、计数、
/// 组队 chips、VS、等级徽标等统一从这里出，圆角固定胶囊档，不再各处手写。
/// 内容与颜色由调用方决定；对齐方式等额外修饰照常链式追加。
/// 颜色形参分 ThemeRef / string 两组重载，与 Reactor Background/WithBorder 的重载形态一致。
/// </summary>
public static class Badge
{
    /// <summary>构造胶囊徽标（语义色：Theme.Accent / palette.WinBg 等）。</summary>
    public static BorderElement Create(
        Element content,
        ThemeRef? background = null,
        ThemeRef? border = null,
        double padH = 6,
        double padV = 1)
    {
        var badge = Border(content)
            .CornerRadius(YuumiTheme.PillRadius)
            .Padding(padH, padV);

        if (background is { } bg)
        {
            badge = badge.Background(bg);
        }

        if (border is { } bd)
        {
            badge = badge.WithBorder(bd, 1);
        }

        return badge;
    }

    /// <summary>构造胶囊徽标（字面色/业务色：AssetColors.*、palette.* 等 ARGB 字符串）。</summary>
    public static BorderElement Create(
        Element content,
        string? background = null,
        string? border = null,
        double padH = 6,
        double padV = 1)
    {
        var badge = Border(content)
            .CornerRadius(YuumiTheme.PillRadius)
            .Padding(padH, padV);

        if (background is not null)
        {
            badge = badge.Background(background);
        }

        if (border is not null)
        {
            badge = badge.WithBorder(border, 1);
        }

        return badge;
    }
}
