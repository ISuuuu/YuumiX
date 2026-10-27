using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Automation.Peers;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Navigation;
using YuumiX.App.Theming;
using YuumiX.Core;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Settings;

/// <summary>
/// 分节渲染的公共上下文：页面持有全部状态与副作用，分节文件只读快照 + 回调写回（R14 拆分约定）。
/// 每次渲染由 SettingsPage 重建，分节不得缓存。
/// </summary>
internal sealed record SettingsUi(
    AppConfig Config,
    AppServices Services,
    IToastService Toast,
    ThemeState Theme,
    MenuVisibilityState Menu,
    UpdateNoticeState UpdateNotice,
    YuumiPalette Palette,
    ILogger Logger,
    Action<string> Commit,
    Action<string?> SetError,
    ReactorWindow? Window = null);

/// <summary>设置页共享视觉构件与纯函数（R14，禁止复制到第二个页面）。</summary>
internal static class SettingsVisuals
{
    /// <summary>分组标题只给语义层级与间距，字重/字号/颜色由 Subtitle 与主题提供。</summary>
    public static Element GroupHeader(string title) =>
        Subtitle(title)
            .HeadingLevel(AutomationHeadingLevel.Level2)
            .Margin(top: 16);

    /// <summary>纯信息行（关于页/更新卡），没有「可编辑」语义。</summary>
    public static Element InfoRow(string label, string value) =>
        HStack(16,
            TextBlock(label).Width(320),
            TextBlock(value).Foreground(Theme.SecondaryText).TextWrapping());

    public static Element ColorSwatch(string color) =>
        Border(Empty())
            .Width(56)
            .Height(24)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Background(color)
            .WithBorder(Theme.CardStroke, 1);

    public static string OnOff(bool value) => value ? "已开启" : "已关闭";

    public static int IndexOf(IReadOnlyList<int> values, int current, int fallback)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] == current)
            {
                return i;
            }
        }

        return fallback;
    }

    public static int IndexOf(IReadOnlyList<string> values, string? current, int fallback)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], current, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return fallback;
    }
}
