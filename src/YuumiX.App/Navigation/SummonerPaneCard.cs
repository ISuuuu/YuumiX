using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using Windows.System;
using YuumiX.App.Controls;
using YuumiX.App.Pages.Career;
using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.App.Navigation;

/// <summary>召唤师信息卡片入参。</summary>
/// <param name="Profile">当前登录召唤师；为 <c>null</c> 时渲染占位态。</param>
/// <param name="IsConnected">客户端是否已连接，用来区分「未连接」与「未登录」。</param>
/// <param name="IsPaneOpen">侧边栏是否展开；收起态（图标栏）只显示头像。</param>
/// <param name="CompactPaneWidth">收起态图标栏的宽度（DIP）。收起态卡片要铺满整栏、头像居中。</param>
/// <param name="OnOpen">点击卡片时触发（跳转到生涯页）。</param>
public sealed record SummonerPaneCardProps(
    SummonerProfile? Profile,
    bool IsConnected,
    bool IsPaneOpen,
    double CompactPaneWidth,
    Action OnOpen);

/// <summary>
/// 侧边栏底部的召唤师信息卡片，对应原 Yuumi 的 <c>user-card</c>：
/// 圆形头像 + 名称 + 副标题，点击进入生涯页。
///
/// 与旧版的一处有意差异：旧版副标题是「大区名」（来自 <c>/lol-platform-config/</c> 的
/// platformId 映射），新项目的 LCU 白名单里没有这个前缀，<see cref="SummonerProfile"/>
/// 也没有 Region 字段，因此副标题暂用召唤师等级顶上——位置留住了，数据源等大区接口接入后再换。
/// </summary>
public sealed class SummonerPaneCard : Component<SummonerPaneCardProps>
{
    /// <summary>头像边长（DIP）。与旧版 <c>.user-avatar</c> 的 32px 一致。</summary>
    private const double AvatarSize = 32;

    /// <summary>卡片高度（DIP）。与旧版 <c>.user-card</c> 的 52px 一致。</summary>
    private const double CardHeight = 52;

    public override Element Render()
    {
        var (hovered, setHovered) = UseState(false);
        var profile = Props.Profile;

        Element avatar = profile is null
            ? Border(Body("\uE77B")
                    .FontFamily("Segoe Fluent Icons")
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center))
                .Width(AvatarSize)
                .Height(AvatarSize)
                .CornerRadius(AvatarSize / 2)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke, 1)
            : Component<LcuImage, LcuImageProps>(
                new LcuImageProps(CareerVisuals.ProfileIcon(profile.ProfileIconId), AvatarSize, AvatarSize / 2));

        Element text = profile is null
            ? VStack(
                Body(Props.IsConnected ? "未登录召唤师" : "未连接客户端")
                    .Foreground(Theme.PrimaryText),
                Caption("点击进入生涯")
                    .Foreground(Theme.SecondaryText))
            : VStack(
                BodyStrong(profile.RiotId)
                    .Foreground(Theme.PrimaryText),
                Caption($"等级 {profile.SummonerLevel}")
                    .Foreground(Theme.SecondaryText));

        // 展开态：头像 + 文字，整卡是带背景与描边的信息卡（对齐旧版 .user-card）。
        // 旧版 hover 是「背景加深 + 描边出现」；这里用 SubtleFill/ControlStroke 表达同一状态。
        Element expandedCard = Border(HStack(10, avatar, text))
            .Height(CardHeight)
            .Padding(new Thickness(10, 0, 10, 0))
            .CornerRadius(4)
            .Background(hovered ? Theme.SubtleFill : Theme.CardBackground)
            .WithBorder(hovered ? Theme.ControlStroke : Theme.CardStroke, 1);

        // 收起态（图标栏）：只留一个圆形头像，铺满整栏宽度并水平居中。
        // 这里刻意不画卡片背景与描边——旧版 .sidebar.collapsed .user-info { display:none }，
        // 头像下方那圈白色方框是「卡片背景 + 直角描边」叠出来的，图标栏里它会显得像多画了个方块。
        // hover 反馈改为头像背后的圆形光晕，与圆形头像形状一致。
        Element collapsedAvatar = hovered
            ? Border(avatar)
                .Width(AvatarSize)
                .Height(AvatarSize)
                .CornerRadius(AvatarSize / 2)
                .Background(Theme.SubtleFill)
            : Border(avatar)
                .Width(AvatarSize)
                .Height(AvatarSize)
                .CornerRadius(AvatarSize / 2);

        Element collapsedCard = Border(collapsedAvatar)
            .Width(Props.CompactPaneWidth)
            .Height(CardHeight)
            .HorizontalContentAlignment(HorizontalAlignment.Center)
            .VerticalContentAlignment(VerticalAlignment.Center);

        // 卡片是可点击区域，补 IsTabStop + 盘激活，别让键盘用户够不到（REACTOR_A11Y_004）。
        var card = Props.IsPaneOpen ? expandedCard : collapsedCard;

        return card
            .IsTabStop(true)
            .AutomationName(profile is null ? "召唤师信息（未登录）" : $"召唤师信息：{profile.RiotId}")
            .OnPointerEntered((_, _) => setHovered(true))
            .OnPointerExited((_, _) => setHovered(false))
            .OnTapped((_, _) => Props.OnOpen())
            .OnKeyDown((_, args) =>
            {
                if (args.Key is VirtualKey.Enter or VirtualKey.Space)
                {
                    Props.OnOpen();
                }
            });
    }
}
