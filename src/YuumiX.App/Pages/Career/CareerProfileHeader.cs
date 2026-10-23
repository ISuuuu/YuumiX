using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.App.Pages.Career;

/// <summary>
/// 生涯页顶部资料卡，对应原 <c>Career.vue</c> 的 <c>.summoner-header</c>。
/// 严格对齐 docs/pic/生涯.png 视觉：
/// 中间：经验环 + 圆形头像 + 等级徽章 + 召唤师名 + Tag + 复制按钮
/// 右侧：刷新 / 历史战绩操作按钮
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
        Action onHistory)
    {
        const double RingSize = 96;
        const double AvatarSize = 72;

        Element ring = ProgressRing(Math.Clamp(summoner.PercentCompleteForNextLevel, 0, 100))
            .Width(RingSize)
            .Height(RingSize)
            .Foreground(palette.Accent)
            .AutomationName($"等级 {summoner.SummonerLevel}，经验 {summoner.PercentCompleteForNextLevel}%");

        // 正圆头像：AvatarSize/2 作为圆角
        Element avatar = Component<LcuImage, LcuImageProps>(
            new LcuImageProps(
                CareerVisuals.ProfileIcon(summoner.ProfileIconId),
                AvatarSize,
                AvatarSize / 2));

        // 环底部中心悬浮等级徽章
        Element levelBadge = Border(TextBlock(summoner.SummonerLevel.ToString(System.Globalization.CultureInfo.InvariantCulture))
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

        // 右侧操作按钮：刷新 / 历史战绩
        Element actions = VStack(8,
            Button("刷新", onRefresh).Width(ActionWidth).Height(32),
            Button("历史战绩", onHistory).Width(ActionWidth).Height(32))
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
}
