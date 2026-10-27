using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Tools;

/// <summary>
/// 分节渲染的公共上下文：页面持有全部状态与副作用，分节文件只读快照 + 回调写回（R14 拆分约定）。
/// 每次渲染由 ToolsPage 重建，分节不得缓存。
/// </summary>
internal sealed record ToolsUi(
    AppConfig Config,
    YuumiPalette Palette,
    IToastService Toast,
    IToolsService Tools,
    IFriendCleanerService FriendCleaner,
    LcuConnection Connection,
    ILogger Logger,
    bool IsLcuConnected,
    ISet<string> ExpandedKeys,
    Action<string> ToggleExpanded,
    Action<string> Commit);

/// <summary>跨分节共享的二次确认开关（快速重启客户端 / 卸下勋章 / 卸下头像框）。</summary>
internal sealed record ConfirmUi(bool RestartClient, bool ClearBadges, bool ClearBorder);

/// <summary>工具箱页共享视觉构件：卡片壳、行布局、开关与横幅（R14，禁止复制到第二个页面）。</summary>
internal static class ToolsVisuals
{
    // 与设置页同一字阶：页面标题 Title(28) / 分组 Subtitle(20) / 卡片标题 Body(14) / 说明 Caption(12)。
    // 卡片标题与状态一律不加字重——雅黑没有 SemiBold，600 会被吸附到 Bold，小字号下笔画粘连、发糊。
    public static Element GroupHeader(string title) =>
        Subtitle(title)
            .HeadingLevel(AutomationHeadingLevel.Level2)
            .Margin(new Thickness(4, 16, 0, 6));

    public static Element CardRow(string label, Element control) =>
        Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                Body(label)
                    .Foreground(Theme.SecondaryText)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 0),
                control.VAlign(VerticalAlignment.Center).HAlign(HorizontalAlignment.Right).Grid(row: 0, column: 1),
            ])
        .Padding(new Thickness(0, 8, 0, 8));

    // 离线守卫：客户端没开时开关一律禁用。旧系统是整页 opacity 0.5 + pointer-events:none，
    // 本版改为按控件禁用——半透明叠在 Mica + 半透明卡片上会把 12~14px 中文压成灰雾（见 OfflineBanner 注释）。
    public static Element CleanToggle(ToolsUi ui, bool value, Action<bool> onChange, string? automationName = null) =>
        ToggleSwitch(value, v => onChange(v), header: null, onContent: null, offContent: null)
            .MinWidth(0)
            .HAlign(HorizontalAlignment.Right)
            .IsEnabled(ui.IsLcuConnected)
            .AutomationName(automationName ?? string.Empty);

    // 离线守卫：客户端没开时右侧控件一律禁用（改了也不会生效，只会留下脏配置）。
    //
    // IsEnabled 的语义是「设置 Control.IsEnabled」，只对真正的控件有效；
    // 传进来的若是一组按钮的容器（StackPanel），它没有这个属性，
    // 这类组合控件必须由调用方逐个按钮再禁一次（见 ClientSection 的重启卡片 / ProfileSection 的在线状态卡片）。
    public static Element Guard(ToolsUi ui, Element control) =>
        control is StackElement ? control : control.IsEnabled(ui.IsLcuConnected);

    /// <summary>单行直接操作卡片（不折叠，右侧直接展示操作按钮或状态开关）。</summary>
    public static Element ActionCard(ToolsUi ui, string title, string description, Element control) =>
        Border(
            Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [
                    VStack(2,
                        Body(title).Foreground(Theme.PrimaryText),
                        Body(description)
                            .Foreground(Theme.SecondaryText)
                            .Margin(new Thickness(0, 3, 0, 0))
                    ).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 0),

                    Guard(ui, control)
                        .VAlign(VerticalAlignment.Center)
                        .HAlign(HorizontalAlignment.Right)
                        .Grid(row: 0, column: 1),
                ]
            ).Padding(new Thickness(16, 12, 16, 12))
        )
        .CornerRadius(YuumiTheme.OverlayRadius)
        .Background(Theme.CardBackground)
        .WithBorder(Theme.CardStroke, 1)
        .HAlign(HorizontalAlignment.Stretch);

    /// <summary>
    /// 可折叠工具卡片：头部按钮切换展开态，展开后渲染 innerContent。
    /// 离线守卫：未连接时既不展开、也不接受展开点击——只禁按钮不够，
    /// 已经展开的卡片会继续渲染里面的开关与输入框，所以连展开态本身一起按连接态夹紧，
    /// 展开记忆（ExpandedKeys）保留，客户端连上后自动恢复原来的展开状态。
    /// 状态点颜色只由调用方给出的布尔决定（禁止从文案反推，见 AGENTS.md §4）。
    /// </summary>
    public static Element ToolCard(
        ToolsUi ui,
        string key,
        string title,
        string description,
        string statusText,
        bool isStatusEnabled,
        Element innerContent)
    {
        var isExpanded = ui.IsLcuConnected && ui.ExpandedKeys.Contains(key);
        var statusColor = isStatusEnabled ? Theme.Accent : Theme.SecondaryText;

        var headerContent = Grid(
            [GridSize.Star(), GridSize.Auto, GridSize.Auto],
            [GridSize.Auto],
            [
                // 标题与说明
                VStack(2,
                    Body(title).Foreground(Theme.PrimaryText),
                    Body(description)
                        .Foreground(Theme.SecondaryText)
                        .Margin(new Thickness(0, 3, 0, 0))
                ).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 0),

                // 右侧状态摘要
                Caption(statusText)
                    .Foreground(statusColor)
                    .VAlign(VerticalAlignment.Center)
                    .Margin(new Thickness(12, 0, 8, 0))
                    .Grid(row: 0, column: 1),

                // 展开指示器（下箭头 / 右箭头）
                Caption(isExpanded ? Icons.ChevronDown : Icons.ChevronRight)
                    .FontFamily(Icons.FontFamily)
                    .Foreground(Theme.SecondaryText)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 2),
            ]
        ).HAlign(HorizontalAlignment.Stretch);

        var headerButton = Button(headerContent, () => ui.ToggleExpanded(key))
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
            .BorderThickness(0)
            .Padding(new Thickness(16, 12, 16, 12))
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .IsEnabled(ui.IsLcuConnected)
            .AutomationName(title);

        Element body;
        if (isExpanded)
        {
            var divider = Border(Empty())
                .Height(1)
                .Background(Theme.CardStroke)
                .Margin(new Thickness(16, 0, 16, 4));

            var paddedContent = Border(innerContent)
                .Padding(new Thickness(16, 4, 16, 12));

            body = VStack(0, headerButton, divider, paddedContent);
        }
        else
        {
            body = headerButton;
        }

        return Border(body)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(HorizontalAlignment.Stretch);
    }

    /// <summary>
    /// 离线提示横幅。离线时不整页降透明度：0.6 叠在 Mica + 半透明卡片上会把正文压成一片灰雾，
    /// 12~14px 中文几乎不可读；禁用提示由本横幅承担，卡片保持正常对比度。
    /// </summary>
    public static Element OfflineBanner() =>
        Border(
            HStack(8,
                Body(Icons.Warning)
                    .FontFamily(Icons.FontFamily)
                    .Foreground(Theme.Ref("SystemFillColorCriticalBrush"))
                    .VAlign(VerticalAlignment.Center),
                Caption("LCU 客户端未连接，本页所有开关与操作已停用，配置不可修改；启动并登录英雄联盟后自动恢复。")
                    .Foreground(Theme.Ref("SystemFillColorCriticalBrush"))
                    .VAlign(VerticalAlignment.Center)
            )
        )
        .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
        .BorderBrush(Theme.Ref("SystemFillColorCriticalBrush"))
        .BorderThickness(1)
        .CornerRadius(YuumiTheme.OverlayRadius)
        .Padding(12, 8)
        .Margin(new Thickness(0, 0, 0, 8));
}

/// <summary>AsyncValue 到列表的适配与加载态判定（Tools 页目录/挑战资源共用）。</summary>
internal static class AsyncValueListExtensions
{
    public static IReadOnlyList<T> AsList<T>(this AsyncValue<IReadOnlyList<T>> value) => value switch
    {
        AsyncValue<IReadOnlyList<T>>.Data d => d.Value,
        AsyncValue<IReadOnlyList<T>>.Reloading r => r.Previous,
        _ => Array.Empty<T>(),
    };

    public static bool IsLoading<T>(this AsyncValue<IReadOnlyList<T>> value) =>
        value is AsyncValue<IReadOnlyList<T>>.Loading;
}
