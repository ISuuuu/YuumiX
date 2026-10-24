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
    bool IsLast = false,
    IReadOnlyDictionary<string, SavedPlayerMarker>? SavedMap = null,
    Action<string, string?>? OnNavigateSearch = null);

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

        // 路人集标记：列头徽标与战绩卡「同局」标记共用（§4.8）
        var savedInfo = FateBadgeVisuals.ResolveSavedInfo(d, p, Props.SavedMap, Props.SelfPuuid);

        // 构造该玩家曾同局的 gameId 集合（encounteredGameIds 全集，空则 lastEncounteredGameId 单条）
        HashSet<long>? encounteredGameIds = null;
        if (savedInfo?.EncounteredGameIds is { Count: > 0 })
        {
            encounteredGameIds = new HashSet<long>(savedInfo.EncounteredGameIds);
        }
        else if (savedInfo?.LastEncounteredGameId is { } lastGid && lastGid > 0)
        {
            encounteredGameIds = [lastGid];
        }

        // 顶部概要头部（5 列模式与 10 列模式高度和排布不同）
        var header = isTenCol
            ? RenderTenColHeader(p, d, headerBg, services, savedInfo)
            : RenderFiveColHeader(p, d, headerBg, services, savedInfo);

        // 战绩列表（前 10 局）
        var matchesList = RenderMatchesList(d, isTenCol, palette, services, encounteredGameIds);

        var colContent = VStack(0, header, matchesList);

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
        AppServices services,
        SavedPlayerMarker? savedInfo)
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
            .Margin(0, -2, -2, 0)
            .WithToolTip(TextBlock("练英雄/新手状态 (熟练度≤1级或点数<5000，近期少用或初次尝试)"));

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
            .Margin(0, -2, -2, 0)
            .WithToolTip(TextBlock("招牌/绝活英雄 (熟练度排名前三且≥1万分，或累计≥10万分)"));

            avatarElement = Grid([GridSize.Auto], [GridSize.Auto], [avatarCore, starBadge]);
        }

        string displayName = PlayerNameVisuals.GetDisplayName(p, d, 0);
        string searchName = PlayerNameVisuals.GetSearchName(p, d);
        bool hasSearchTarget = !string.IsNullOrEmpty(searchName);

        var nameText = TextBlock(displayName)
            .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
            .FontSize(11.5)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .VAlign(VerticalAlignment.Center);

        if (hasSearchTarget)
        {
            nameText = nameText
                .WithToolTip(TextBlock($"搜索 {searchName}"))
                .OnTapped((_, _) =>
                {
                    string puuid = d?.Info?.Puuid ?? p.Puuid ?? string.Empty;
                    Props.OnNavigateSearch?.Invoke(searchName, puuid);
                });
        }

        // 第 1 行：玩家名 + 锁 + 徽标
        var nameRowItems = new List<Element> { nameText };

        if (d?.MatchHistoryHidden == true || d?.IsProfilePrivate == true)
        {
            nameRowItems.Add(
                TextBlock("\uE72E")
                    .FontFamily("Segoe Fluent Icons")
                    .FontSize(10)
                    .Foreground("#FFD97706")
                    .Margin(3, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(TextBlock("战绩已隐藏")));
        }

        if (!string.IsNullOrEmpty(d?.FateFlag))
        {
            bool isAlly = d.FateFlag == "ally";
            string fateText = FateBadgeVisuals.GetBadgeText(d);
            string fateBg = isAlly ? "#2010B981" : "#20EF4444";
            string fateFg = isAlly ? "#FF10B981" : "#FFF87171";

            nameRowItems.Add(
                Border(TextBlock(fateText).FontSize(8.5).Foreground(fateFg))
                    .Background(fateBg)
                    .CornerRadius(2)
                    .Padding(2, 0.5)
                    .Margin(3, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(FateBadgeVisuals.BuildToolTip(d)));
        }

        // 路人集徽标：有 tag 显示 tag；无 tag 且无宿命徽标时显示灰「历史」（§2.3.1 / §4.8）
        if (savedInfo is not null && FateBadgeVisuals.ShouldShowSavedBadge(savedInfo, d))
        {
            nameRowItems.Add(
                FateBadgeVisuals.BuildSavedBadge(savedInfo, 8.5)
                    .Margin(3, 0, 0, 0));
        }

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
            string shortRank = ShortRank(activeRank.Tier, activeRank.Division);
            var rankBlock = HStack(2,
                CareerVisuals.RankedMedal(activeRank.Tier, 12).VAlign(VerticalAlignment.Center),
                TextBlock(shortRank).FontSize(9).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).Foreground("#FFAAAAAA").VAlign(VerticalAlignment.Center)
            )
            .Margin(0, 0, 4, 0)
            .WithToolTip(TextBlock(BuildRankDetailedTooltip(d, masteryDetail)));

            summaryItems.Add(rankBlock);
        }

        if (masteryDetail is { Level: > 0 })
        {
            string masteryTip = $"英雄熟练度: Lv.{masteryDetail.Level} ({masteryDetail.Points:N0} 点数)";
            summaryItems.Add(
                Border(TextBlock($"Lv.{masteryDetail.Level}").FontSize(8.5).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground("#FFCCCCCC"))
                    .Background("#20FFFFFF")
                    .WithBorder("#20FFFFFF", 1)
                    .CornerRadius(2)
                    .Padding(2, 0.5)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(TextBlock(masteryTip)));
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

        string premadeTip = Props.PremadeIdx >= 0 ? $"组队 {Props.PremadeIdx + 1}" : string.Empty;
        var headerBorder = Border(headerContent)
            .Height(48)
            .Background(bg)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .BorderBrush("#15FFFFFF")
            .Padding(6, 4);

        if (!string.IsNullOrEmpty(premadeTip))
        {
            headerBorder = headerBorder.WithToolTip(TextBlock(premadeTip));
        }

        return headerBorder;
    }

    private Element RenderTenColHeader(
        PremadePlayerLike p,
        GameInfoPlayer? d,
        string bg,
        AppServices services,
        SavedPlayerMarker? savedInfo)
    {
        int champId = p.ChampionId > 0 ? p.ChampionId : (d?.ChampionId ?? 0);
        string avatarAsset = champId > 0
            ? $"/lol-game-data/assets/v1/champion-icons/{champId}.png"
            : $"/lol-game-data/assets/v1/profile-icons/{(p.ProfileIconId > 0 ? p.ProfileIconId : 29)}.png";

        var masteryDetail = ChampionMasteryDetail.Create(d?.Masteries, champId);

        // 头像左侧胜率竖条（动态自底向上填充，透明度渐变，§2.3.2）
        Element? wrBar = null;
        if (d?.WinRate.HasValue == true)
        {
            double rate = d.WinRate.Value;
            double diff = Math.Abs(rate - 50.0);
            double strength = diff / 50.0;
            double opacity = Math.Round(0.3 + strength * 0.7, 2);
            double fillHeight = Math.Max(2, Math.Round(24.0 * (diff * 2.0 / 100.0)));
            string barColor = rate >= 50 ? "#FF10B981" : "#FFF43F5E";

            wrBar = Border(
                Border(Empty())
                    .Width(3)
                    .Height(fillHeight)
                    .CornerRadius(1.5)
                    .Background(barColor)
                    .Opacity(opacity)
                    .VAlign(VerticalAlignment.Bottom)
            )
            .Width(3)
            .Height(24)
            .CornerRadius(1.5)
            .Background("#1AFFFFFF")
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, 3, 0)
            .WithToolTip(TextBlock($"胜率 {rate:F0}%"));
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
            .Margin(0, -2, -2, 0)
            .WithToolTip(TextBlock("练英雄/新手状态"));

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
            .Margin(0, -2, -2, 0)
            .WithToolTip(TextBlock("招牌/绝活英雄"));

            avatarElement = Grid([GridSize.Auto], [GridSize.Auto], [avatarCore, starBadge]);
        }

        // 头像区：含胜率竖条 + 头像，头像下方为胜负统计（带常玩位置分析 ToolTip，§2.3.2）
        var avatarRow = HStack(0,
            wrBar is not null ? wrBar : Empty(),
            avatarElement);

        string posTooltip = BuildPosTooltip(d);

        Element avatarColumn;
        if (d?.WinCount.HasValue == true && d?.LossesCount.HasValue == true)
        {
            var countsRow = HStack(1,
                TextBlock($"{d.WinCount}胜").FontSize(8).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground("#FF10B981"),
                TextBlock($"{d.LossesCount}负").FontSize(8).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground("#FFF43F5E")
            ).HAlign(HorizontalAlignment.Center);

            if (!string.IsNullOrEmpty(posTooltip))
            {
                countsRow = countsRow.WithToolTip(TextBlock(posTooltip));
            }

            avatarColumn = VStack(1, avatarRow, countsRow);
        }
        else if (d?.WinRate.HasValue == true)
        {
            int wr = (int)Math.Round(d.WinRate.Value);
            string wrColor = wr >= 50 ? "#FF10B981" : "#FFF43F5E";
            var wrBlock = TextBlock($"{wr}%").FontSize(8).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground(wrColor).HAlign(HorizontalAlignment.Center);
            if (!string.IsNullOrEmpty(posTooltip))
            {
                wrBlock = wrBlock.WithToolTip(TextBlock(posTooltip));
            }
            avatarColumn = VStack(1, avatarRow, wrBlock);
        }
        else
        {
            avatarColumn = avatarRow;
        }

        string displayName = PlayerNameVisuals.GetDisplayName(p, d, 0);
        string searchName = PlayerNameVisuals.GetSearchName(p, d);
        bool hasSearchTarget = !string.IsNullOrEmpty(searchName);

        var nameText = TextBlock(displayName)
            .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
            .FontSize(11)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .VAlign(VerticalAlignment.Center);

        if (hasSearchTarget)
        {
            nameText = nameText
                .WithToolTip(TextBlock($"搜索 {searchName}"))
                .OnTapped((_, _) =>
                {
                    string puuid = d?.Info?.Puuid ?? p.Puuid ?? string.Empty;
                    Props.OnNavigateSearch?.Invoke(searchName, puuid);
                });
        }

        // 信息区第 1 行：玩家名 + 锁
        var nameRowItems = new List<Element> { nameText };

        if (d?.MatchHistoryHidden == true || d?.IsProfilePrivate == true)
        {
            nameRowItems.Add(
                TextBlock("\uE72E")
                    .FontFamily("Segoe Fluent Icons")
                    .FontSize(9)
                    .Foreground("#FFD97706")
                    .Margin(2, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(TextBlock("战绩已隐藏")));
        }

        var nameRow = HStack(0, nameRowItems.ToArray());

        // 信息区第 2 行：熟练度微徽章（左）+ 排位段位（右）（带详细 ToolTip，§2.3.2）
        var rankMasteryItems = new List<Element>();
        if (masteryDetail is { Level: > 0 })
        {
            rankMasteryItems.Add(
                Border(TextBlock($"Lv.{masteryDetail.Level}").FontSize(8).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground("#FFCCCCCC"))
                    .Background("#20FFFFFF")
                    .WithBorder("#20FFFFFF", 1)
                    .CornerRadius(2)
                    .Padding(2, 0.5)
                    .Margin(0, 0, 2, 0)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(TextBlock($"英雄熟练度: Lv.{masteryDetail.Level} ({masteryDetail.Points:N0} 点数)")));
        }

        var activeRank = d?.PrimaryRank;
        if (activeRank?.HasRank == true)
        {
            string shortRank = ShortRank(activeRank.Tier, activeRank.Division);
            var rankBlock = HStack(2,
                CareerVisuals.RankedMedal(activeRank.Tier, 11).VAlign(VerticalAlignment.Center),
                TextBlock(shortRank).FontSize(8.5).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).Foreground("#FFAAAAAA").VAlign(VerticalAlignment.Center)
            )
            .Margin(2, 0, 0, 0)
            .WithToolTip(TextBlock(BuildRankDetailedTooltip(d, masteryDetail)));

            rankMasteryItems.Add(rankBlock);
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

        // 第 3 行：常玩位置 + 标识徽标（带 ToolTip，§2.3.2）
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
            string fateText = FateBadgeVisuals.GetBadgeText(d);
            string fateBg = isAlly ? "#2010B981" : "#20EF4444";
            string fateFg = isAlly ? "#FF10B981" : "#FFF87171";

            flagItems.Add(
                Border(TextBlock(fateText).FontSize(8).Foreground(fateFg))
                    .Background(fateBg)
                    .CornerRadius(2)
                    .Padding(2, 0.5)
                    .Margin(0, 0, 2, 0)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(FateBadgeVisuals.BuildToolTip(d)));
        }

        // 路人集徽标：10 列第三行（有 tag 显示 tag；无 tag 且无宿命徽标时显示灰「历史」，§2.3.2 / §4.8）
        if (savedInfo is not null && FateBadgeVisuals.ShouldShowSavedBadge(savedInfo, d))
        {
            flagItems.Add(
                FateBadgeVisuals.BuildSavedBadge(savedInfo, 8)
                    .Margin(0, 0, 2, 0));
        }

        var posBlock = TextBlock(posText)
            .FontSize(8)
            .Foreground("#FF888888")
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .VAlign(VerticalAlignment.Center);

        if (!string.IsNullOrEmpty(posTooltip))
        {
            posBlock = posBlock.WithToolTip(TextBlock(posTooltip));
        }

        var thirdRow = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                posBlock.Grid(row: 0, column: 0),
                HStack(0, flagItems.ToArray()).Grid(row: 0, column: 1).VAlign(VerticalAlignment.Center),
            ]
        );

        string premadeTip = Props.PremadeIdx >= 0 ? $"组队 {Props.PremadeIdx + 1}" : string.Empty;
        var headerBorder = Border(
            VStack(1, topSection, thirdRow)
        )
        .Height(58)
        .Background(bg)
        .BorderThickness(new Thickness(0, 0, 0, 1))
        .BorderBrush("#15FFFFFF")
        .Padding(4, 3);

        if (!string.IsNullOrEmpty(premadeTip))
        {
            headerBorder = headerBorder.WithToolTip(TextBlock(premadeTip));
        }

        return headerBorder;
    }

    private static Element RenderMatchesList(
        GameInfoPlayer? d,
        bool isTenCol,
        YuumiPalette palette,
        AppServices services,
        IReadOnlySet<long>? encounteredGameIds)
    {
        if (d?.Loading == true)
        {
            return VStack(0,
                Border(TextBlock("· · ·").FontSize(11).Foreground("#FF666666").HAlign(HorizontalAlignment.Center))
                    .Margin(0, 32, 0, 0)
            );
        }

        if (d?.MatchHistoryHidden == true || d?.IsProfilePrivate == true)
        {
            return Border(Empty()).Padding(0, 24, 0, 0);
        }

        if (d?.Matches == null || d.Matches.Count == 0)
        {
            return VStack(4,
                TextBlock("📭").FontSize(14).HAlign(HorizontalAlignment.Center),
                TextBlock("暂无对局记录").FontSize(8.5).Foreground("#FF666666").HAlign(HorizontalAlignment.Center)
            )
            .Padding(0, 24, 0, 0);
        }

        var cards = d.Matches.Take(10)
            .Select(m => RenderMatchCard(m, isTenCol, palette, services, encounteredGameIds))
            .ToArray();

        return VStack(isTenCol ? 3 : 4, cards)
            .Padding(isTenCol ? 3 : 4);
    }

    private static Element RenderMatchCard(
        MatchSummary m,
        bool isTenCol,
        YuumiPalette palette,
        AppServices services,
        IReadOnlySet<long>? encounteredGameIds)
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

        // 卡片顶部：模式名称 + 位置 + 「同局」标记 + 日期（带 ToolTip，§2.3.3）
        var topRowElements = new List<Element>
        {
            TextBlock(m.QueueName)
                .FontSize(isTenCol ? 8.5 : 9)
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                .Foreground("#FFAAAAAA")
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .Grid(row: 0, column: 0)
        };

        var topRowColumns = new List<GridSize> { GridSize.Star() };
        int topCol = 1;

        if (!isTenCol && !string.IsNullOrEmpty(posName))
        {
            topRowColumns.Add(GridSize.Auto);
            topRowElements.Add(
                TextBlock(posName)
                    .FontSize(8.5)
                    .Foreground("#FFAAAAAA")
                    .Margin(3, 0, 3, 0)
                    .Grid(row: 0, column: topCol));
            topCol++;
        }

        // 「同局」标记：该局 gameId 在路人集相遇记录中时显示（title「你曾参与本场对局」，§4.8）
        if (encounteredGameIds is not null && encounteredGameIds.Contains(m.GameId))
        {
            topRowColumns.Add(GridSize.Auto);
            topRowElements.Add(
                Border(TextBlock("同局")
                        .FontSize(8)
                        .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                        .Foreground("#FF3B82F6"))
                    .Background("#263B82F6")
                    .WithBorder("#663B82F6", 1)
                    .CornerRadius(3)
                    .Padding(3, 0.5)
                    .Margin(3, 0, 3, 0)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(TextBlock("你曾参与本场对局"))
                    .Grid(row: 0, column: topCol));
            topCol++;
        }

        topRowColumns.Add(GridSize.Auto);
        topRowElements.Add(
            TextBlock(m.ShortDateText)
                .FontSize(isTenCol ? 8 : 8.5)
                .Foreground("#FF888888")
                .Grid(row: 0, column: topCol));

        var topRow = Grid(topRowColumns.ToArray(), [GridSize.Auto], topRowElements.ToArray())
            .WithToolTip(TextBlock(GetMatchTopTooltip(m)));

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

    private static string GetMatchTopTooltip(MatchSummary m)
    {
        var lines = new List<string> { $"游戏模式: {m.QueueName}" };
        string? pos = SearchVisuals.PositionText(m.Position);
        if (!string.IsNullOrEmpty(pos))
        {
            lines.Add($"位置: {pos}");
        }
        string date = FateBadgeVisuals.FormatTimestamp(m.GameCreation);
        if (!string.IsNullOrEmpty(date))
        {
            lines.Add($"日期: {date}");
        }
        return string.Join("\n", lines);
    }

    private static string BuildRankDetailedTooltip(GameInfoPlayer? d, ChampionMasteryDetail? mastery)
    {
        var lines = new List<string>();
        if (d?.Solo?.HasRank == true)
        {
            int total = d.Solo.Wins + d.Solo.Losses;
            int wr = total > 0 ? (int)Math.Round((double)d.Solo.Wins / total * 100) : 0;
            lines.Add($"单双排: {d.Solo.TierText} {d.Solo.LeaguePoints}LP ({d.Solo.Wins}胜{d.Solo.Losses}负 {wr}%)");
        }
        else
        {
            lines.Add("单双排: 无段位");
        }

        if (d?.Flex?.HasRank == true)
        {
            int total = d.Flex.Wins + d.Flex.Losses;
            int wr = total > 0 ? (int)Math.Round((double)d.Flex.Wins / total * 100) : 0;
            lines.Add($"灵活排: {d.Flex.TierText} {d.Flex.LeaguePoints}LP ({d.Flex.Wins}胜{d.Flex.Losses}负 {wr}%)");
        }
        else
        {
            lines.Add("灵活排: 无段位");
        }

        if (mastery is not null && mastery.Level > 0)
        {
            lines.Add($"熟练度: Lv.{mastery.Level} ({mastery.Points:N0}点)");
        }

        return string.Join("\n", lines);
    }

    private static string BuildPosTooltip(GameInfoPlayer? d)
    {
        if (d?.Positions?.PrimaryPosition == null) return string.Empty;
        var p = d.Positions;
        string? name = SearchVisuals.PositionText(p.PrimaryPosition);
        var lines = new List<string>();
        if (p.SecondaryPosition != null && p.SecondaryPercentage > 0)
        {
            string? secName = SearchVisuals.PositionText(p.SecondaryPosition);
            lines.Add($"常玩位置: 主选{name} ({p.PrimaryPercentage}%) · 次选{secName} ({p.SecondaryPercentage}%) · 近期 {p.TotalValid} 场");
        }
        else
        {
            lines.Add($"常玩位置: {name} ({p.PrimaryPercentage}%) · 近期 {p.TotalValid} 场");
        }

        foreach (var entry in p.Entries)
        {
            if (entry.Count > 0)
            {
                lines.Add($"{entry.Name}: {entry.Count}场 ({entry.Percentage}%)");
            }
        }

        if (d.TopChampions is { Count: > 0 })
        {
            string champNames = string.Join("、", d.TopChampions.Take(3).Select(c => $"{c.ChampionName} ({c.Games}场 {c.WinRate}%)"));
            lines.Add($"常用英雄: {champNames}");
        }

        return string.Join("\n", lines);
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

    private static string ShortPosition(MatchPosition? pos) => pos switch
    {
        MatchPosition.Top => "上",
        MatchPosition.Jungle => "野",
        MatchPosition.Middle => "中",
        MatchPosition.Bottom => "下",
        MatchPosition.Utility => "辅",
        _ => string.Empty,
    };
}
