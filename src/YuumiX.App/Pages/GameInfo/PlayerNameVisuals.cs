using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using YuumiX.App.Theming;
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.GameInfo.Models;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Pages.GameInfo;

/// <summary>
/// 玩家名称解析与跳转文本规范化（对应旧版 <c>usePlayerSearch.ts</c>）。
/// 纯逻辑收敛在 Core 层的 <see cref="PlayerIdentity"/>，本类提供界面侧快捷入口。
/// </summary>
public static class PlayerNameVisuals
{
    public static bool IsPlaceholderName(string? name) => PlayerIdentity.IsPlaceholderName(name);

    public static string GetSearchName(PremadePlayerLike p, GameInfoPlayer? d) => PlayerIdentity.GetSearchName(p, d);

    public static string GetDisplayName(PremadePlayerLike p, GameInfoPlayer? d, int index) => PlayerIdentity.GetDisplayName(p, d, index);
}

internal sealed record PlayerNameLinkProps(
    string DisplayName,
    string TooltipText,
    Action OnActivate,
    bool UseBodyStrong = false,
    bool UseCaption = false);

/// <summary>
/// 局内玩家名可点击链接（带悬停变色、按压即时反馈与键盘可达性，对齐战绩详情页 <c>PlayerNameLink</c>）。
/// </summary>
internal sealed class PlayerNameLinkButton : Component<PlayerNameLinkProps>
{
    public override Element Render()
    {
        var (isHovered, setIsHovered) = UseState(false);

        var text = Props.UseBodyStrong
            ? BodyStrong(Props.DisplayName)
            : Props.UseCaption
                ? Caption(Props.DisplayName)
                : Body(Props.DisplayName);

        return text
            .Foreground(isHovered ? Theme.Accent : Theme.PrimaryText)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .VAlign(VerticalAlignment.Center)
            .IsTabStop(true)
            .AutomationName(Props.TooltipText)
            .WithToolTip(TextBlock(Props.TooltipText))
            .OnPointerEntered((_, _) => setIsHovered(true))
            .OnPointerExited((_, _) => setIsHovered(false))
            .OnTapped((_, _) => Props.OnActivate())
            .OnKeyDown((_, args) =>
            {
                if (args.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
                {
                    Props.OnActivate();
                    args.Handled = true;
                }
            });
    }
}
