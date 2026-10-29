using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.App.Pages.Career;

/// <summary>
/// 生涯页顶部资料卡，对应原 <c>Career.vue</c> 的 <c>.summoner-header</c>。
/// 中间：经验环 + 圆形头像 + 等级徽章 + 召唤师名 + Tag + 复制按钮
/// 右侧：返回我的生涯（查看他人时） / 刷新 / 历史战绩操作按钮
///
/// 经验环（裁决 D3）：对齐旧版的 270° 弧形进度环——SVG viewBox 100×100、
/// r=45、stroke-width 5、周长 282.74、满弧 235.62、整体 rotate 120°。
/// 这里把旋转烘焙进起始角（120° 起、顺时针最多扫 270°），用 <c>Path</c> 弧段实现，
/// 进度变化时重算终点坐标（<see cref="GaugeRing"/>）。
/// </summary>
internal static class CareerProfileHeader
{
    public static Element Render(
        SummonerProfile summoner,
        YuumiPalette palette,
        bool copied,
        Action onCopy,
        Action onRefresh,
        Action onHistory,
        bool isViewingOther = false,
        Action? onBackToSelf = null)
    {
        Element ring = Viewbox(GaugeRing(summoner.PercentCompleteForNextLevel, palette))
            .Width(RingSize)
            .Height(RingSize)
            .Stretch(Stretch.Uniform)
            .AutomationName($"等级 {summoner.SummonerLevel}，经验 {summoner.PercentCompleteForNextLevel}%");

        // 正圆头像
        Element avatar = Component<LcuImage, LcuImageProps>(
            new LcuImageProps(
                CareerVisuals.ProfileIcon(summoner.ProfileIconId),
                AvatarSize,
                AvatarSize / 2));

        // 环底部中心悬浮等级徽章
        Element levelBadge = Badge.Create(
            Caption(summoner.SummonerLevel.ToString(CultureInfo.InvariantCulture))
                .SemiBold()
                .Foreground(Theme.PrimaryText)
                .HAlign(HorizontalAlignment.Center),
            Theme.CardBackground,
            Theme.CardStroke,
            padH: 8,
            padV: 1);

        Element avatarBlock = Grid(
                [GridSize.Auto],
                [GridSize.Auto],
                [
                    ring.HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center),
                    avatar.HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center),
                    levelBadge.HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Bottom).Margin(0, 0, 0, -4),
                ])
            .Width(RingSize)
            .Height(RingSize);

        string mainName = !string.IsNullOrWhiteSpace(summoner.GameName)
            ? summoner.GameName
            : (!string.IsNullOrWhiteSpace(summoner.DisplayName)
                ? summoner.DisplayName
                : (summoner.RiotId.Contains('#', StringComparison.Ordinal)
                    ? summoner.RiotId.Split('#')[0]
                    : summoner.RiotId));

        var i18n = Composition.AppHost.Services?.I18n;
        var copiedText = i18n?.T("career.copied", "✓ 已复制") ?? "✓ 已复制";
        var backToMeText = i18n?.T("career.backToMe", "返回我的生涯") ?? "返回我的生涯";
        var refreshText = i18n?.T("career.refresh", "刷新") ?? "刷新";
        var historyBtnText = i18n?.T("career.historyBtn", "历史战绩") ?? "历史战绩";

        // 复制按钮 / 已复制提示
        Element copySlot = HStack(6,
            Button(
                Caption(Icons.Copy)
                    .FontFamily(Icons.FontFamily)
                    .Foreground(Theme.SecondaryText),
                onCopy)
                .Width(28)
                .Height(28)
                .Padding(0)
                .CornerRadius(YuumiTheme.OverlayRadius)
                .AutomationName("复制 Riot ID"),
            copied
                ? Caption(copiedText)
                    .Foreground(CareerVisuals.WinText)
                    .VAlign(VerticalAlignment.Center)
                : Empty());

        var nameRowItems = new List<Element>
        {
            Title(mainName)
                .HeadingLevel(AutomationHeadingLevel.Level2)
                .VAlign(VerticalAlignment.Center),
        };

        // 私密档案：名字旁只留锁图标，说明文案沉到悬停提示（与对局信息页同一形态）
        if (string.Equals(summoner.Privacy, "PRIVATE", StringComparison.OrdinalIgnoreCase))
        {
            nameRowItems.Add(
                BodyLarge(Icons.View)
                    .FontFamily(Icons.FontFamily)
                    .Foreground(Theme.SecondaryText)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(TextBlock("对局记录已设为私密")));
        }

        nameRowItems.Add(copySlot.VAlign(VerticalAlignment.Center));

        Element nameRow = HStack(8, nameRowItems.ToArray());

        var nameColumn = new List<Element> { nameRow };
        if (!string.IsNullOrEmpty(summoner.TagLine))
        {
            nameColumn.Add(Body($"# {summoner.TagLine}")
                .Foreground(Theme.SecondaryText));
        }

        // 居中主体：头像 + 召唤师信息
        Element centerProfile = HStack(20,
            avatarBlock.VAlign(VerticalAlignment.Center),
            VStack(4, nameColumn.ToArray()).VAlign(VerticalAlignment.Center))
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);

        // 右侧操作按钮：返回我的生涯（查看他人时，§2.8） / 刷新 / 历史战绩
        // 尺寸对齐旧版 `.action-btn`：padding 6×16、高 ≈28、宽度随文案（不再统一 96 宽，
        // 旧版「刷新」约 58、「历史战绩」约 84，右对齐成一列）。
        var actionButtons = new List<Element>();
        if (isViewingOther && onBackToSelf is not null)
        {
            actionButtons.Add(Button(Body(backToMeText), onBackToSelf)
                .Height(32)
                .Padding(16, 4)
                .CornerRadius(YuumiTheme.OverlayRadius)
                .AutomationName(backToMeText));
        }

        actionButtons.Add(Button(Body(refreshText), onRefresh)
            .AutomationName(refreshText)
            .Height(32)
            .Padding(16, 4)
            .CornerRadius(YuumiTheme.OverlayRadius));
        actionButtons.Add(Button(Body(historyBtnText), onHistory)
            .AutomationName(historyBtnText)
            .Height(32)
            .Padding(16, 4)
            .CornerRadius(YuumiTheme.OverlayRadius));

        Element actions = VStack(8, actionButtons.ToArray())
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center);

        // Grid 三列自适应布局：[Star, Auto, Star]，头像+名字永远居中，按钮贴右
        Element headerContent = Grid(
            [GridSize.Star(), GridSize.Auto, GridSize.Star()],
            [GridSize.Auto],
            [
                centerProfile.Grid(row: 0, column: 1),
                actions.Grid(row: 0, column: 2),
            ]);

        return Border(headerContent)
            // 旧版 `.summoner-header` 的 padding 是 1.5rem（24）
            .Padding(24)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);
    }

    /// <summary>经验环外框：旧版 `.profile-icon-wrapper` 104×104（SVG viewBox 100 被整体缩放到 104）。</summary>
    private const double RingSize = 104;

    /// <summary>圆形头像：旧版 `.avatar-container` 为父级 104 减去 inset 13 两侧 = 78。</summary>
    private const double AvatarSize = 78;

    /// <summary>弧形经验环：坐标系 100×100、r=45、描边 5，与旧版 SVG 几何一致。</summary>
    private const double GaugeCanvas = 100;
    private const double GaugeRadius = 45;
    private const double GaugeStroke = 5;

    /// <summary>旧版环形起点：120°（屏幕坐标系顺时针角，0° 为 3 点钟方向），满弧 270°。</summary>
    private const double GaugeStartAngle = 120;
    private const double GaugeSweepMax = 270;

    /// <summary>
    /// 270° 弧形进度环（裁决 D3）：底层轨道弧 + 顶层进度弧，进度驱动扫过角度。
    /// 返回固定 100×100 的画布，调用方按需缩放摆放。
    /// </summary>
    private static Element GaugeRing(int percent, YuumiPalette palette)
    {
        double clamped = Math.Clamp(percent, 0, 100);
        double sweep = GaugeSweepMax * clamped / 100.0;

        // 轨道色取旧版 --gauge-track-color 的深浅两套：浅 #E2E8F0 / 深 #334155
        string trackColor = CareerVisuals.IsDarkTheme ? AssetColors.TrackDark : AssetColors.TrackLight;

        var children = new List<Element>
        {
            GaugeArc(ArcData(GaugeStartAngle, GaugeSweepMax), BrushHelper.Parse(trackColor)),
        };

        if (sweep > 0.1)
        {
            // PathElement.Stroke 只收 Brush（无 ThemeRef 重载），这里急切解析强调色资源；
            // 系统强调色不随深浅主题变化，急切取值不会产生主题漂移。
            children.Add(GaugeArc(ArcData(GaugeStartAngle, sweep), ThemeResource.Brush("AccentFillColorDefaultBrush")));
        }

        return Grid(
                [GridSize.Auto],
                [GridSize.Auto],
                children.ToArray())
            .Width(GaugeCanvas)
            .Height(GaugeCanvas);
    }

    /// <summary>按屏幕角度（顺时针、0° 为 3 点钟方向）计算圆弧的 SVG Path 数据。</summary>
    private static string ArcData(double startAngleDeg, double sweepDeg)
    {
        double startRad = startAngleDeg * Math.PI / 180.0;
        double endRad = (startAngleDeg + sweepDeg) * Math.PI / 180.0;
        double centerX = GaugeCanvas / 2;
        double centerY = GaugeCanvas / 2;

        double startX = centerX + GaugeRadius * Math.Cos(startRad);
        double startY = centerY + GaugeRadius * Math.Sin(startRad);
        double endX = centerX + GaugeRadius * Math.Cos(endRad);
        double endY = centerY + GaugeRadius * Math.Sin(endRad);

        // sweep > 180° 时大弧标志必须为 1，否则 ArcSegment 会走短弧
        int largeArc = sweepDeg > 180 ? 1 : 0;
        return string.Format(
            CultureInfo.InvariantCulture,
            "M {0:F2} {1:F2} A {2:F2} {2:F2} 0 {3} 1 {4:F2} {5:F2}",
            startX, startY, GaugeRadius, largeArc, endX, endY);
    }

    private static Element GaugeArc(string data, Brush strokeColor) => new PathElement
    {
        PathDataString = data,
        Stroke = strokeColor,
        StrokeThickness = GaugeStroke,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    }
        .Width(GaugeCanvas)
        .Height(GaugeCanvas);
}
