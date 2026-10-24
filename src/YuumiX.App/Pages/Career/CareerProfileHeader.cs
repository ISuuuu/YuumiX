using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
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
    private const double ActionWidth = 96;

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
        Element ring = GaugeRing(summoner.PercentCompleteForNextLevel, palette)
            .AutomationName($"等级 {summoner.SummonerLevel}，经验 {summoner.PercentCompleteForNextLevel}%");

        // 正圆头像
        Element avatar = Component<LcuImage, LcuImageProps>(
            new LcuImageProps(
                CareerVisuals.ProfileIcon(summoner.ProfileIconId),
                AvatarSize,
                AvatarSize / 2));

        // 环底部中心悬浮等级徽章
        Element levelBadge = Border(TextBlock(summoner.SummonerLevel.ToString(CultureInfo.InvariantCulture))
                .FontSize(11)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .Foreground(Theme.PrimaryText)
                .HAlign(HorizontalAlignment.Center))
            .Padding(8, 1)
            .CornerRadius(9)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);

        Element avatarBlock = Grid(
                [GridSize.Auto],
                [GridSize.Auto],
                [
                    ring.HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center),
                    avatar.HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center),
                    levelBadge.HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Bottom).Margin(0, 0, 0, -6),
                ])
            .Width(RingSize + 4)
            .Height(RingSize + 4);

        string mainName = !string.IsNullOrWhiteSpace(summoner.GameName)
            ? summoner.GameName
            : (!string.IsNullOrWhiteSpace(summoner.DisplayName)
                ? summoner.DisplayName
                : (summoner.RiotId.Contains('#', StringComparison.Ordinal)
                    ? summoner.RiotId.Split('#')[0]
                    : summoner.RiotId));

        // 复制按钮 / 已复制提示
        Element copySlot = copied
            ? Border(HStack(4,
                    TextBlock("✓").FontSize(11).Foreground(CareerVisuals.WinText),
                    TextBlock("已复制").FontSize(11).Foreground(CareerVisuals.WinText)))
                .Padding(8, 3)
                .CornerRadius(4)
                .Background(CareerVisuals.WithAlpha(CareerVisuals.WinText, 0x1F))
            : Button("复制", onCopy)
                .Height(26)
                .Padding(8, 0)
                .FontSize(11)
                .AutomationName("复制 Riot ID");

        Element nameRow = HStack(8,
            TextBlock(mainName)
                .FontSize(22)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .VAlign(VerticalAlignment.Center),
            copySlot.VAlign(VerticalAlignment.Center));

        var nameColumn = new List<Element> { nameRow };
        if (!string.IsNullOrEmpty(summoner.TagLine))
        {
            nameColumn.Add(TextBlock($"# {summoner.TagLine}")
                .FontSize(13)
                .Foreground(Theme.SecondaryText));
        }

        if (string.Equals(summoner.Privacy, "PRIVATE", StringComparison.OrdinalIgnoreCase))
        {
            nameColumn.Add(TextBlock("🔒 对局记录已设为私密")
                .FontSize(11)
                .Foreground(Theme.SecondaryText));
        }

        // 居中主体：头像 + 召唤师信息
        Element centerProfile = HStack(18,
            avatarBlock.VAlign(VerticalAlignment.Center),
            VStack(4, nameColumn.ToArray()).VAlign(VerticalAlignment.Center))
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);

        // 右侧操作按钮：返回我的生涯（查看他人时，§2.8） / 刷新 / 历史战绩
        var actionButtons = new List<Element>();
        if (isViewingOther && onBackToSelf is not null)
        {
            actionButtons.Add(Button("返回我的生涯", onBackToSelf)
                .Width(ActionWidth)
                .Height(32)
                .FontSize(11)
                .AutomationName("返回我的生涯"));
        }

        actionButtons.Add(Button("刷新", onRefresh).Width(ActionWidth).Height(32));
        actionButtons.Add(Button("历史战绩", onHistory).Width(ActionWidth).Height(32));

        Element actions = VStack(8, actionButtons.ToArray())
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center);

        // 左侧对称占位块（与右侧 actions 保持相同宽度，确保 centerProfile 视觉上绝对居中）
        Element leftPlaceholder = Border(Empty())
            .Width(ActionWidth)
            .VAlign(VerticalAlignment.Center);

        // 使用 HStack 摆放三栏，杜绝 Grid 重叠问题
        const double TotalHeaderWidth = 920;
        double centerWidth = TotalHeaderWidth - (ActionWidth * 2);

        Element headerContent = HStack(0,
            leftPlaceholder,
            centerProfile.Width(centerWidth),
            actions.Width(ActionWidth));

        return Border(headerContent)
            .Padding(20)
            .CornerRadius(10)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);
    }

    private const double RingSize = 96;
    private const double AvatarSize = 72;

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
        string trackColor = CareerVisuals.IsDarkTheme ? "#FF334155" : "#FFE2E8F0";

        var children = new List<Element>
        {
            GaugeArc(ArcData(GaugeStartAngle, GaugeSweepMax), trackColor),
        };

        if (sweep > 0.1)
        {
            children.Add(GaugeArc(ArcData(GaugeStartAngle, sweep), palette.Accent));
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

    private static Element GaugeArc(string data, string strokeColor) => new PathElement
    {
        PathDataString = data,
        Stroke = BrushHelper.Parse(strokeColor),
        StrokeThickness = GaugeStroke,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    }
        .Width(GaugeCanvas)
        .Height(GaugeCanvas);
}
