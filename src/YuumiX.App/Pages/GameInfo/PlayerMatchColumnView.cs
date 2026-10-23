using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Pages.Career;
using YuumiX.App.Pages.Search;
using YuumiX.App.Theming;
using YuumiX.Core;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match.Models;

namespace YuumiX.App.Pages.GameInfo;

public sealed record PlayerMatchColumnProps(
    PremadePlayerLike Player,
    GameInfoPlayer? PlayerData,
    int PremadeIdx,
    int ViewColumns,
    YuumiPalette Palette,
    string? SelfPuuid = null,
    string Side = "ally",
    string? MapSide = null,
    bool IsTeamSeparator = false,
    bool IsLast = false);

/// <summary>
/// 战绩列组件（对应旧版 <c>PlayerMatchColumn.vue</c>）。
/// 展示单个玩家的头部概要与近期前 10 局对局卡片。
/// </summary>
public sealed class PlayerMatchColumnView : Component<PlayerMatchColumnProps>
{
    public override Element Render()
    {
        var services = UseContext(AppHost.Context) ?? AppHost.Services;
        var p = Props.Player;
        var d = Props.PlayerData;
        int premadeIdx = Props.PremadeIdx;
        bool isTenCol = Props.ViewColumns == 10;
        var palette = Props.Palette;

        var premadeColor = premadeIdx >= 0 ? PremadeColors.GetColor(premadeIdx) : null;
        string headerBg = premadeColor?.Bg ?? "#0DFFFFFF";

        // 阵营顶部色彩指示（根据 mapSide 蓝/红互换）
        string topBorderColor;
        if (Props.Side == "ally")
        {
            topBorderColor = Props.MapSide == "red" ? "#FFF43F5E" : "#FF3B82F6";
        }
        else
        {
            topBorderColor = Props.MapSide == "red" ? "#FF3B82F6" : "#FFF43F5E";
        }

        // 1. 头部构建
        var header = isTenCol
            ? RenderTenColHeader(p, d, headerBg, services)
            : RenderFiveColHeader(p, d, headerBg, services);

        // 2. 战绩卡片列表
        var matchesContent = RenderMatches(d, isTenCol, palette, services);

        var colContent = Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Star()],
            [
                header.Grid(row: 0),
                ScrollView(matchesContent).Grid(row: 1),
            ]
        );

        var columnBorder = Border(colContent)
            .Background("#00000000")
            .WithBorder(topBorderColor, 3); // 顶部 3px 阵营指示线

        if (Props.IsTeamSeparator)
        {
            // 双方队伍中间的突出分割线
            return Border(columnBorder)
                .WithBorder(topBorderColor, 0)
                .BorderThickness(new Thickness(0, 0, 2, 0))
                .BorderBrush("#30FFFFFF");
        }

        if (!Props.IsLast)
        {
            return Border(columnBorder)
                .BorderThickness(new Thickness(0, 0, 1, 0))
                .BorderBrush("#15FFFFFF");
        }

        return columnBorder;
    }

    private Element RenderFiveColHeader(
        PremadePlayerLike p,
        GameInfoPlayer? d,
        string bg,
        AppServices services)
    {
        int champId = p.ChampionId > 0 ? p.ChampionId : (d?.ChampionId ?? 0);
        string avatarAsset = champId > 0
            ? $"/lol-game-data/assets/v1/champion-icons/{champId}.png"
            : $"/lol-game-data/assets/v1/profile-icons/{(p.ProfileIconId > 0 ? p.ProfileIconId : 29)}.png";

        var masteryDetail = ChampionMasteryDetail.Create(d?.Masteries, champId);

        // 英雄头像（28x28，圆角 6）
        var avatarCore = Border(
            Component<LcuImage, LcuImageProps>(new LcuImageProps(avatarAsset, Size: 28, CornerRadius: 6))
                .Provide(AppHost.Context, services)
        )
        .Width(28)
        .Height(28)
        .CornerRadius(6)
        .WithBorder(masteryDetail?.IsTopChampion == true ? "#FFFFD700" : "#00000000", masteryDetail?.IsTopChampion == true ? 1.5 : 0);

        Element avatarElement = avatarCore;
        if (masteryDetail?.IsPracticing == true)
        {
            var practiceBadge = Border(
                TextBlock("练")
                    .FontSize(8)
                    .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                    .Foreground("#FFFFFFFF")
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
            )
            .Width(12)
            .Height(12)
            .CornerRadius(6)
            .Background("#FFF43F5E")
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Top)
            .Margin(0, -2, -2, 0);

            avatarElement = Grid([GridSize.Auto], [GridSize.Auto], [avatarCore, practiceBadge]);
        }
        else if (masteryDetail?.IsTopChampion == true)
        {
            var starBadge = Border(
                TextBlock("★")
                    .FontSize(8)
                    .Foreground("#FFFFFFFF")
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
            )
            .Width(12)
            .Height(12)
            .CornerRadius(6)
            .Background("#FFF59E0B")
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Top)
            .Margin(0, -2, -2, 0);

            avatarElement = Grid([GridSize.Auto], [GridSize.Auto], [avatarCore, starBadge]);
        }

        string displayName = d?.DisplayName ?? p.DisplayName ?? p.GameName ?? p.SummonerName ?? "玩家";

        // 第 1 行：玩家名 + 锁 + 徽标
        var nameRowItems = new List<Element>
        {
            TextBlock(displayName)
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                .FontSize(11.5)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .VAlign(VerticalAlignment.Center)
        };

        if (d?.MatchHistoryHidden == true || d?.IsProfilePrivate == true)
        {
            nameRowItems.Add(
                TextBlock("\uE72E")
                    .FontFamily("Segoe Fluent Icons")
                    .FontSize(10)
                    .Foreground("#FFD97706")
                    .Margin(3, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center));
        }

        if (!string.IsNullOrEmpty(d?.FateFlag))
        {
            bool isAlly = d.FateFlag == "ally";
            string fateText = isAlly ? "曾友" : "曾敌";
            string fateBg = isAlly ? "#2010B981" : "#20EF4444";
            string fateFg = isAlly ? "#FF10B981" : "#FFF87171";

            nameRowItems.Add(
                Border(TextBlock(fateText).FontSize(8.5).Foreground(fateFg))
                    .Background(fateBg)
                    .CornerRadius(2)
                    .Padding(2, 0.5)
                    .Margin(3, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center));
        }

        var savedBadge = Border(TextBlock("历史").FontSize(8.5).Foreground("#FFAAAAAA"))
            .Background("#1AFFFFFF")
            .CornerRadius(2)
            .Padding(2, 0.5)
            .Margin(3, 0, 0, 0)
            .VAlign(VerticalAlignment.Center);
        nameRowItems.Add(savedBadge);

        var nameRow = HStack(0, nameRowItems.ToArray());

        // 第 2 行：几胜几负 + 段位图标与短名 + 熟练度
        var summaryItems = new List<Element>();

        if (d?.WinCount.HasValue == true && d?.LossesCount.HasValue == true)
        {
            summaryItems.Add(TextBlock($"{d.WinCount}胜").FontSize(9).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground("#FF10B981"));
            summaryItems.Add(TextBlock($"{d.LossesCount}负").FontSize(9).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground("#FFF43F5E").Margin(2, 0, 4, 0));
        }
        else if (d?.WinRate.HasValue == true)
        {
            int wr = (int)Math.Round(d.WinRate.Value);
            string wrColor = wr >= 50 ? "#FF10B981" : "#FFF43F5E";
            summaryItems.Add(TextBlock($"{wr}%").FontSize(9).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground(wrColor).Margin(0, 0, 4, 0));
        }

        var activeRank = d?.PrimaryRank;
        if (activeRank?.HasRank == true)
        {
            summaryItems.Add(CareerVisuals.RankedMedal(activeRank.Tier, 12).VAlign(VerticalAlignment.Center));
            string shortRank = ShortRank(activeRank.Tier, activeRank.Division);
            summaryItems.Add(TextBlock(shortRank).FontSize(9).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).Foreground("#FFAAAAAA").Margin(2, 0, 4, 0).VAlign(VerticalAlignment.Center));
        }

        if (masteryDetail is { Level: > 0 })
        {
            summaryItems.Add(
                Border(TextBlock($"Lv.{masteryDetail.Level}").FontSize(8.5).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground("#FFCCCCCC"))
                    .Background("#20FFFFFF")
                    .WithBorder("#20FFFFFF", 1)
                    .CornerRadius(2)
                    .Padding(2, 0.5)
                    .VAlign(VerticalAlignment.Center));
        }

        var summaryRow = HStack(0, summaryItems.ToArray());

        var headerContent = Grid(
            [GridSize.Auto, GridSize.Star()],
            [GridSize.Auto],
            [
                avatarElement.Grid(row: 0, column: 0).Margin(0, 0, 6, 0).VAlign(VerticalAlignment.Center),
                VStack(2, nameRow, summaryRow)
                    .Grid(row: 0, column: 1)
                    .VAlign(VerticalAlignment.Center),
            ]
        );

        return Border(headerContent)
            .Height(48)
            .Background(bg)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .BorderBrush("#15FFFFFF")
            .Padding(6, 4);
    }

    private Element RenderTenColHeader(
        PremadePlayerLike p,
        GameInfoPlayer? d,
        string bg,
        AppServices services)
    {
        int champId = p.ChampionId > 0 ? p.ChampionId : (d?.ChampionId ?? 0);
        string avatarAsset = champId > 0
            ? $"/lol-game-data/assets/v1/champion-icons/{champId}.png"
            : $"/lol-game-data/assets/v1/profile-icons/{(p.ProfileIconId > 0 ? p.ProfileIconId : 29)}.png";

        var masteryDetail = ChampionMasteryDetail.Create(d?.Masteries, champId);

        // 头像左侧胜率竖条（26px 高，3px 宽）
        Element? wrBar = null;
        if (d?.WinRate.HasValue == true)
        {
            string wrColor = d.WinRate.Value >= 50 ? "#FF10B981" : "#FFF43F5E";
            wrBar = Border(Empty())
                .Width(3)
                .Height(24)
                .CornerRadius(1.5)
                .Background(wrColor)
                .VAlign(VerticalAlignment.Center)
                .Margin(0, 0, 2, 0);
        }

        // 英雄头像（26x26，圆角 5）
        var avatarCore = Border(
            Component<LcuImage, LcuImageProps>(new LcuImageProps(avatarAsset, Size: 26, CornerRadius: 5))
                .Provide(AppHost.Context, services)
        )
        .Width(26)
        .Height(26)
        .CornerRadius(5)
        .WithBorder(masteryDetail?.IsTopChampion == true ? "#FFFFD700" : "#00000000", masteryDetail?.IsTopChampion == true ? 1.5 : 0);

        Element avatarElement = avatarCore;
        if (masteryDetail?.IsPracticing == true)
        {
            var practiceBadge = Border(
                TextBlock("练")
                    .FontSize(7.5)
                    .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                    .Foreground("#FFFFFFFF")
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
            )
            .Width(11)
            .Height(11)
            .CornerRadius(5.5)
            .Background("#FFF43F5E")
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Top)
            .Margin(0, -2, -2, 0);

            avatarElement = Grid([GridSize.Auto], [GridSize.Auto], [avatarCore, practiceBadge]);
        }
        else if (masteryDetail?.IsTopChampion == true)
        {
            var starBadge = Border(
                TextBlock("★")
                    .FontSize(7.5)
                    .Foreground("#FFFFFFFF")
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
            )
            .Width(11)
            .Height(11)
            .CornerRadius(5.5)
            .Background("#FFF59E0B")
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Top)
            .Margin(0, -2, -2, 0);

            avatarElement = Grid([GridSize.Auto], [GridSize.Auto], [avatarCore, starBadge]);
        }

        // 头像区：含胜率竖条 + 头像，头像下方为胜负统计
        var avatarRow = HStack(0,
            wrBar is not null ? wrBar : Empty(),
            avatarElement);

        Element avatarColumn;
        if (d?.WinCount.HasValue == true && d?.LossesCount.HasValue == true)
        {
            avatarColumn = VStack(1,
                avatarRow,
                HStack(1,
                    TextBlock($"{d.WinCount}胜").FontSize(8).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground("#FF10B981"),
                    TextBlock($"{d.LossesCount}负").FontSize(8).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground("#FFF43F5E")
                ).HAlign(HorizontalAlignment.Center)
            );
        }
        else if (d?.WinRate.HasValue == true)
        {
            int wr = (int)Math.Round(d.WinRate.Value);
            string wrColor = wr >= 50 ? "#FF10B981" : "#FFF43F5E";
            avatarColumn = VStack(1,
                avatarRow,
                TextBlock($"{wr}%").FontSize(8).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground(wrColor).HAlign(HorizontalAlignment.Center)
            );
        }
        else
        {
            avatarColumn = avatarRow;
        }

        string displayName = d?.DisplayName ?? p.DisplayName ?? p.GameName ?? p.SummonerName ?? "玩家";

        // 信息区第 1 行：玩家名 + 锁
        var nameRowItems = new List<Element>
        {
            TextBlock(displayName)
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                .FontSize(10.5)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .VAlign(VerticalAlignment.Center)
        };

        if (d?.MatchHistoryHidden == true || d?.IsProfilePrivate == true)
        {
            nameRowItems.Add(
                TextBlock("\uE72E")
                    .FontFamily("Segoe Fluent Icons")
                    .FontSize(9.5)
                    .Foreground("#FFD97706")
                    .Margin(2, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center));
        }

        var nameRow = HStack(0, nameRowItems.ToArray());

        // 信息区第 2 行：熟练度 + 段位短名
        var rankMasteryItems = new List<Element>();
        if (masteryDetail is { Level: > 0 })
        {
            rankMasteryItems.Add(
                Border(TextBlock($"Lv.{masteryDetail.Level}").FontSize(8.5).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground("#FFCCCCCC"))
                    .Background("#20FFFFFF")
                    .CornerRadius(2)
                    .Padding(2, 0.5)
                    .Margin(0, 0, 3, 0)
                    .VAlign(VerticalAlignment.Center));
        }

        var activeRank = d?.PrimaryRank;
        if (activeRank?.HasRank == true)
        {
            rankMasteryItems.Add(CareerVisuals.RankedMedal(activeRank.Tier, 11).VAlign(VerticalAlignment.Center));
            string shortRank = ShortRank(activeRank.Tier, activeRank.Division);
            rankMasteryItems.Add(TextBlock(shortRank).FontSize(8.5).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).Foreground("#FFAAAAAA").Margin(2, 0, 0, 0).VAlign(VerticalAlignment.Center));
        }

        var rankMasteryRow = HStack(0, rankMasteryItems.ToArray());

        var topSection = Grid(
            [GridSize.Auto, GridSize.Star()],
            [GridSize.Auto],
            [
                avatarColumn.Grid(row: 0, column: 0).Margin(0, 0, 4, 0).VAlign(VerticalAlignment.Top),
                VStack(2, nameRow, rankMasteryRow)
                    .Grid(row: 0, column: 1)
                    .VAlign(VerticalAlignment.Top),
            ]
        );

        // 第 3 行：常玩位置 + 标识徽标
        string posText = "";
        if (d?.Positions?.PrimaryPosition != null)
        {
            string p1 = ShortPosition(d.Positions.PrimaryPosition);
            posText = $"{p1} {d.Positions.PrimaryPercentage}%";
            if (d.Positions.SecondaryPosition != null && d.Positions.SecondaryPercentage > 0)
            {
                string p2 = ShortPosition(d.Positions.SecondaryPosition);
                posText += $" / {p2} {d.Positions.SecondaryPercentage}%";
            }
        }

        var flagItems = new List<Element>();
        if (!string.IsNullOrEmpty(d?.FateFlag))
        {
            bool isAlly = d.FateFlag == "ally";
            string fateText = isAlly ? "曾友" : "曾敌";
            string fateBg = isAlly ? "#2010B981" : "#20EF4444";
            string fateFg = isAlly ? "#FF10B981" : "#FFF87171";

            flagItems.Add(
                Border(TextBlock(fateText).FontSize(8).Foreground(fateFg))
                    .Background(fateBg)
                    .CornerRadius(2)
                    .Padding(2, 0.5)
                    .Margin(0, 0, 2, 0)
                    .VAlign(VerticalAlignment.Center));
        }

        flagItems.Add(
            Border(TextBlock("历史").FontSize(8).Foreground("#FFAAAAAA"))
                .Background("#1AFFFFFF")
                .CornerRadius(2)
                .Padding(2, 0.5)
                .VAlign(VerticalAlignment.Center));

        var thirdRow = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                TextBlock(posText)
                    .FontSize(8)
                    .Foreground("#FF888888")
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 0),
                HStack(0, flagItems.ToArray())
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 1),
            ]
        );

        return Border(
            VStack(2, topSection, thirdRow)
        )
        .Height(58)
        .Background(bg)
        .BorderThickness(new Thickness(0, 0, 0, 1))
        .BorderBrush("#15FFFFFF")
        .Padding(4, 3);
    }

    private static Element RenderMatches(
        GameInfoPlayer? d,
        bool isTenCol,
        YuumiPalette palette,
        AppServices services)
    {
        if (d?.Loading == true)
        {
            return Border(
                TextBlock("正在加载...")
                    .FontSize(11)
                    .Foreground("#FF888888")
                    .HAlign(HorizontalAlignment.Center)
            )
            .Padding(0, 24, 0, 0);
        }

        if (d?.MatchHistoryHidden == true || d?.IsProfilePrivate == true)
        {
            return Border(
                VStack(4,
                    TextBlock("\uE72E")
                        .FontFamily("Segoe Fluent Icons")
                        .FontSize(18)
                        .Foreground("#FFD97706")
                        .HAlign(HorizontalAlignment.Center),
                    TextBlock("战绩隐藏")
                        .FontSize(10)
                        .Foreground("#FFAAAAAA")
                        .HAlign(HorizontalAlignment.Center)
                )
            )
            .Padding(0, 28, 0, 0);
        }

        if (d is null || d.Matches.Count == 0)
        {
            return Border(
                TextBlock("暂无战绩")
                    .FontSize(10.5)
                    .Foreground("#FF888888")
                    .HAlign(HorizontalAlignment.Center)
            )
            .Padding(0, 24, 0, 0);
        }

        var cards = d.Matches.Take(10).Select(m => RenderMatchCard(m, isTenCol, palette, services)).ToArray();

        return VStack(isTenCol ? 3 : 4, cards)
            .Padding(isTenCol ? 3 : 4);
    }

    private static Element RenderMatchCard(
        MatchSummary m,
        bool isTenCol,
        YuumiPalette palette,
        AppServices services)
    {
        string cardBg = m.Remake
            ? palette.RemakeCard
            : (m.Win ? palette.WinCard : palette.LoseCard);

        string cardBorder = m.Remake
            ? palette.RemakeAccent
            : (m.Win ? palette.WinAccent : palette.LoseAccent);
        cardBorder = SearchVisuals.WithAlpha(cardBorder, "55");

        string championAsset = $"/lol-game-data/assets/v1/champion-icons/{m.ChampionId}.png";

        double iconSize = isTenCol ? 28 : 32;
        double radius = isTenCol ? 5 : 6;

        // 英雄头像 + 等级微标
        var champImage = Component<LcuImage, LcuImageProps>(new LcuImageProps(championAsset, Size: iconSize, CornerRadius: radius))
            .Provide(AppHost.Context, services);

        var levelBadge = Border(
            TextBlock($"{m.ChampionLevel}")
                .FontSize(isTenCol ? 7 : 7.5)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .Foreground("#FFFFFFFF")
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
        )
        .Background("#E00F172A")
        .CornerRadius(2)
        .Padding(isTenCol ? 1.5 : 2, 0)
        .HAlign(HorizontalAlignment.Right)
        .VAlign(VerticalAlignment.Bottom)
        .Margin(0, 0, -1, -1);

        var champIconWithLevel = Grid(
            [GridSize.Auto],
            [GridSize.Auto],
            [champImage, levelBadge]
        );

        string posName = SearchVisuals.PositionText(m.Position) ?? "";

        // 卡片顶部：模式名称 + 位置 + 日期
        var topRowElements = new List<Element>
        {
            TextBlock(m.QueueName)
                .FontSize(isTenCol ? 8.5 : 9)
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                .Foreground("#FFAAAAAA")
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .Grid(row: 0, column: 0)
        };

        var topRowColumns = new List<GridSize> { GridSize.Star(), GridSize.Auto };

        if (!isTenCol && !string.IsNullOrEmpty(posName))
        {
            topRowColumns = [GridSize.Star(), GridSize.Auto, GridSize.Auto];
            topRowElements.Add(
                TextBlock(posName)
                    .FontSize(8.5)
                    .Foreground("#FFAAAAAA")
                    .Margin(3, 0, 3, 0)
                    .Grid(row: 0, column: 1));
            topRowElements.Add(
                TextBlock(m.ShortDateText)
                    .FontSize(8.5)
                    .Foreground("#FF888888")
                    .Grid(row: 0, column: 2));
        }
        else
        {
            topRowElements.Add(
                TextBlock(m.ShortDateText)
                    .FontSize(isTenCol ? 8 : 8.5)
                    .Foreground("#FF888888")
                    .Grid(row: 0, column: 1));
        }

        var topRow = Grid(topRowColumns.ToArray(), [GridSize.Auto], topRowElements.ToArray());

        // KDA 行（击杀/死亡/助攻，死亡红色加粗）
        var kdaItems = new List<Element>
        {
            TextBlock($"{m.Kills}").FontSize(isTenCol ? 10 : 11).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground("#FFFFFFFF"),
            TextBlock(isTenCol ? " " : " / ").FontSize(isTenCol ? 8.5 : 9.5).Foreground("#FF666666"),
            TextBlock($"{m.Deaths}").FontSize(isTenCol ? 10 : 11).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground("#FFEF4444"),
            TextBlock(isTenCol ? " " : " / ").FontSize(isTenCol ? 8.5 : 9.5).Foreground("#FF666666"),
            TextBlock($"{m.Assists}").FontSize(isTenCol ? 10 : 11).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground("#FFFFFFFF"),
        };

        var kdaLine = HStack(0, kdaItems.ToArray());

        Element statsBox;
        if (!isTenCol)
        {
            var (ratioText, ratioColor) = FormatKdaRatio(m);
            statsBox = VStack(1,
                kdaLine,
                TextBlock($"{ratioText} KDA")
                    .FontSize(8.5)
                    .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                    .Foreground(ratioColor)
            );
        }
        else
        {
            statsBox = kdaLine;
        }

        var mainRow = Grid(
            [GridSize.Auto, GridSize.Star()],
            [GridSize.Auto],
            [
                champIconWithLevel.Grid(row: 0, column: 0).Margin(0, 0, isTenCol ? 4 : 6, 0).VAlign(VerticalAlignment.Center),
                statsBox.Grid(row: 0, column: 1).VAlign(VerticalAlignment.Center),
            ]
        );

        return Border(
            VStack(isTenCol ? 1 : 2, topRow, mainRow)
        )
        .Background(cardBg)
        .WithBorder(cardBorder, 1)
        .CornerRadius(isTenCol ? 5 : 6)
        .Padding(isTenCol ? 3 : 5, isTenCol ? 2.5 : 3.5);
    }

    private static (string text, string color) FormatKdaRatio(MatchSummary m)
    {
        if (m.Deaths == 0)
        {
            return ("Perf", "#FFFFD700");
        }

        double ratio = (m.Kills + m.Assists) / (double)m.Deaths;
        string text = ratio >= 10
            ? ratio.ToString("0", CultureInfo.InvariantCulture)
            : ratio.ToString("0.0", CultureInfo.InvariantCulture);

        string color = ratio switch
        {
            >= 5.0 => "#FFFFD700",
            >= 3.0 => "#FF3B82F6",
            >= 2.0 => "#FF10B981",
            _ => "#FFAAAAAA",
        };

        return (text, color);
    }

    private static string ShortRank(string? tier, string? division)
    {
        if (string.IsNullOrEmpty(tier) || tier is "NONE" or "UNRANKED") return "无段位";
        string shortTier = tier switch
        {
            "IRON" => "黑铁",
            "BRONZE" => "黄铜",
            "SILVER" => "白银",
            "GOLD" => "黄金",
            "PLATINUM" => "铂金",
            "EMERALD" => "翡翠",
            "DIAMOND" => "钻石",
            "MASTER" => "大师",
            "GRANDMASTER" => "宗师",
            "CHALLENGER" => "王者",
            _ => tier,
        };

        return string.IsNullOrEmpty(division) || division is "NA" ? shortTier : $"{shortTier}{division}";
    }

    private static string ShortPosition(MatchPosition? position) => position switch
    {
        MatchPosition.Top => "上",
        MatchPosition.Jungle => "野",
        MatchPosition.Middle => "中",
        MatchPosition.Bottom => "下",
        MatchPosition.Utility => "辅",
        _ => "",
    };
}
