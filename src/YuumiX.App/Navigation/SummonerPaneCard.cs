using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using Windows.System;
using YuumiX.App.Controls;
using YuumiX.App.Pages.Career;
using YuumiX.Core.Features.Summoner.Models;

using YuumiX.App.Theming;

namespace YuumiX.App.Navigation;

/// <summary>召唤师信息卡片入参。</summary>
/// <param name="Profile">当前登录召唤师；为 <c>null</c> 时渲染占位头像。</param>
/// <param name="IsConnected">客户端是否已连接，用来区分「未连接」与「未登录」。</param>
/// <param name="IsPaneOpen">侧边栏是否展开：展开态显示 Riot ID 信息卡，收起态只留头像。</param>
/// <param name="PaneWidth">当前侧边栏宽度（DIP，展开 210 / 收起 64）。卡片铺满栏宽，头像才能水平居中。</param>
/// <param name="OnOpen">点击卡片时触发（跳转到生涯页）。</param>
public sealed record SummonerPaneCardProps(
    SummonerProfile? Profile,
    bool IsConnected,
    bool IsPaneOpen,
    double PaneWidth,
    Action OnOpen);

/// <summary>
/// 侧边栏底部的召唤师信息卡片，对应原 Yuumi 的 <c>user-card</c>，点击进入生涯页。
///
/// 展开态：头像 + Riot ID 左对齐的信息卡（hover 变底色，对齐旧版 user-card）；
/// 收起态（图标栏）：只留居中头像，hover 用圆形光晕。
/// 名称/等级按产品决定不显示，大区名因新版客户端（RSO 架构）已不暴露所在大区而无法实现
/// ——旧数据源 <c>/lol-platform-config/</c> 的 <c>LoginPlatformLocalization</c> 命名空间
/// 被腾讯客户端移除，进程参数与其余接口只剩登录集群标识（如 TJ101）。
/// </summary>
public sealed class SummonerPaneCard : Component<SummonerPaneCardProps>
{
    /// <summary>头像边长（DIP，含描边）。与旧版 <c>.user-avatar</c> 的 32px 一致。</summary>
    private const double AvatarSize = 32;

    /// <summary>
    /// 头像描边粗细（DIP）。对齐旧版 <c>.user-avatar</c> 的 1.5px 边框语义（本项目布局一律整数像素，取 1）。
    /// </summary>
    private const double AvatarRingThickness = 1;

    /// <summary>头像内容边长（DIP）：外框减去描边后的可绘制区域。</summary>
    private const double AvatarContentSize = AvatarSize - (AvatarRingThickness * 2);

    /// <summary>卡片高度（DIP）。与旧版 <c>.user-card</c> 的 52px 一致。</summary>
    private const double CardHeight = 52;

    public override Element Render()
    {
        var (hovered, setHovered) = UseState(false);
        var profile = Props.Profile;

        Element avatar = profile is null
            ? Border(Body(Icons.PersonPlaceholder)
                    .FontFamily(Icons.FontFamily)
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center))
                .Width(AvatarContentSize)
                .Height(AvatarContentSize)
                .CornerRadius(AvatarContentSize / 2)
                .Background(Theme.CardBackground)
            : Component<LcuImage, LcuImageProps>(
                new LcuImageProps(
                    CareerVisuals.ProfileIcon(profile.ProfileIconId),
                    AvatarContentSize,
                    AvatarContentSize / 2));

        // 描边由外层负责（1px，对齐旧版 .user-avatar 的 border）：深色头像（实测 #180805）与浅色背景
        // 之间只有 1px 抗锯齿像素，32px 圆的几何阶梯在高对比下会被读成毛边，描边正好把这圈盖住。
        // 注意内容必须**自己裁圆**：Border 的 CornerRadius 只裁「超出边界」的部分，
        // 30px 的内容完全落在 32px 容器内时四角不会被裁，会显示成方形（踩过）。
        // 内容圆（半径 15）与外层描边圆（半径 16）不同心同径，不存在 α→α² 的叠加压薄问题。
        Element avatarWrap = Border(avatar)
            .Width(AvatarSize)
            .Height(AvatarSize)
            .CornerRadius(AvatarSize / 2)
            .WithBorder(Theme.CardStroke, AvatarRingThickness)
            .HorizontalContentAlignment(HorizontalAlignment.Center)
            .VerticalContentAlignment(VerticalAlignment.Center);

        var i18n = Composition.AppHost.Services?.I18n;
        var notLoggedIn = i18n?.T("home.notConnected", "未登录召唤师") ?? "未登录召唤师";
        var notConnected = i18n?.T("home.notConnected", "未连接客户端") ?? "未连接客户端";
        var viewCareer = i18n?.T("career.historyBtn", "点击进入生涯") ?? "点击进入生涯";

        // 卡片必须显式铺满栏宽再让内容居中：PaneFooter 的 VStack 会把子项收紧到
        // 内容宽度并忽略 HorizontalAlignment，不设宽度时整个卡片缩成 32px 贴左。
        // 内容居中用 HorizontalContentAlignment——旧版收起态就是这一组合，已验证。
        Element text = profile is null
            ? VStack(
                Body(Props.IsConnected ? notLoggedIn : notConnected)
                    .Foreground(Theme.PrimaryText),
                Caption(viewCareer)
                    .Foreground(Theme.SecondaryText))
            : Border(BodyStrong(profile.RiotId)
                    .Foreground(Theme.PrimaryText)
                    .TextTrimming(TextTrimming.CharacterEllipsis))
                .Height(AvatarSize)
                .VerticalContentAlignment(VerticalAlignment.Center);

        // hover 反馈：展开态改卡片底色/描边；收起态只在头像背后铺圆形光晕
        // （旧版同款处理——图标栏里给整张卡片画方框会显得像多画了个方块）。
        Element card = Props.IsPaneOpen
            ? Border(HStack(12, avatarWrap, text))
                .Width(Props.PaneWidth)
                .Height(CardHeight)
                .Padding(new Thickness(8, 0, 8, 0))
                .CornerRadius(YuumiTheme.ControlRadius)
                .Background(hovered ? Theme.SubtleFill : Theme.CardBackground)
                .WithBorder(hovered ? Theme.ControlStroke : Theme.CardStroke, 1)
                .VerticalContentAlignment(VerticalAlignment.Center)
            : Border(hovered
                ? avatarWrap.Background(Theme.SubtleFill)
                : avatarWrap)
                .Width(Props.PaneWidth)
                .Height(CardHeight)
                .HorizontalContentAlignment(HorizontalAlignment.Center)
                .VerticalContentAlignment(VerticalAlignment.Center);

        string hint = profile?.RiotId ?? (Props.IsConnected ? "未登录召唤师" : "未连接客户端");

        // ToolTip 必须等登录态（profile 到位后的重渲染）再挂：首次渲染就带 ToolTip
        // 会让 Reactor 消息循环静默退出（退出码 0、无异常无崩溃日志，实测复现）。
        if (profile is not null)
        {
            card = card.ToolTip(hint);
        }

        return card
            .IsTabStop(true)
            .AutomationName(profile is null ? $"召唤师信息（{hint}）" : $"召唤师信息：{hint}")
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
