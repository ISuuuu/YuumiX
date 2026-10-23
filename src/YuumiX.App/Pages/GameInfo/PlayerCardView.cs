using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Pages.Career;
using YuumiX.App.Pages.Search;
using YuumiX.Core;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.GameInfo.Models;

namespace YuumiX.App.Pages.GameInfo;

public sealed record PlayerCardProps(
    PremadePlayerLike Player,
    GameInfoPlayer? PlayerData,
    int PremadeIdx,
    int Index,
    string? SelfPuuid = null);

/// <summary>
/// 左侧玩家卡片视图（对应旧版 <c>PlayerCard.vue</c>）。
/// 展示头像、召唤师名、段位、胜率、熟练度、宿命徽标与组队高亮。
/// </summary>
public sealed class PlayerCardView : Component<PlayerCardProps>
{
    public override Element Render()
    {
        var services = UseContext(AppHost.Context) ?? AppHost.Services;
        var p = Props.Player;
        var d = Props.PlayerData;
        int premadeIdx = Props.PremadeIdx;

        int champId = p.ChampionId > 0 ? p.ChampionId : (d?.ChampionId ?? 0);
        string avatarAsset = champId > 0
            ? $"/lol-game-data/assets/v1/champion-icons/{champId}.png"
            : $"/lol-game-data/assets/v1/profile-icons/{(p.ProfileIconId > 0 ? p.ProfileIconId : 29)}.png";

        var masteryDetail = ChampionMasteryDetail.Create(d?.Masteries, champId);

        var premadeColor = premadeIdx >= 0 ? PremadeColors.GetColor(premadeIdx) : null;
        string bg = premadeColor?.Bg ?? "#12FFFFFF";
        string border = premadeColor?.Border ?? "#18FFFFFF";

        // 头像区（42x42，圆形）
        var avatarCore = Border(
            Component<LcuImage, LcuImageProps>(new LcuImageProps(avatarAsset, Size: 42, CornerRadius: 21))
                .Provide(AppHost.Context, services)
        )
        .Width(42)
        .Height(42)
        .CornerRadius(21)
        .WithBorder(masteryDetail?.IsTopChampion == true ? "#FFFFD700" : "#00000000", masteryDetail?.IsTopChampion == true ? 2 : 0);

        var avatarStack = new List<Element> { avatarCore };

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
            .Width(13)
            .Height(13)
            .CornerRadius(6.5)
            .Background("#E0E06B6B")
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Bottom);

            avatarStack.Add(practiceBadge);
        }

        // 头像下方召唤师等级微标（如 622、883 等）
        if (d?.Info?.SummonerLevel > 0)
        {
            var levelBadge = Border(
                TextBlock($"{d.Info.SummonerLevel}")
                    .FontSize(8)
                    .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                    .Foreground("#FFFFFFFF")
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
            )
            .Background("#D00F172A")
            .CornerRadius(3)
            .Padding(3, 0.5)
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Bottom)
            .Margin(0, 0, 0, -4);

            avatarStack.Add(levelBadge);
        }

        var avatarElement = Grid([GridSize.Auto], [GridSize.Auto], avatarStack.ToArray());

        // 玩家名称与徽标
        string displayName = d?.DisplayName ?? p.DisplayName ?? p.GameName ?? p.SummonerName ?? "玩家";
        bool isSelf = !string.IsNullOrEmpty(Props.SelfPuuid) &&
                      (!string.IsNullOrEmpty(p.Puuid) && p.Puuid == Props.SelfPuuid ||
                       d?.Info?.Puuid == Props.SelfPuuid);

        var nameRowItems = new List<Element>
        {
            TextBlock(displayName)
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                .FontSize(12)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .VAlign(VerticalAlignment.Center)
        };

        if (isSelf)
        {
            nameRowItems.Add(
                Border(TextBlock("我").FontSize(8.5).Foreground("#FF3B82F6"))
                    .Background("#203B82F6")
                    .CornerRadius(2.5)
                    .Padding(2.5, 0.5)
                    .Margin(3, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center));
        }

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
            string fateFg = isAlly ? "#FF10B981" : "#FFEF4444";

            nameRowItems.Add(
                Border(TextBlock(fateText).FontSize(8.5).Foreground(fateFg))
                    .Background(fateBg)
                    .CornerRadius(2.5)
                    .Padding(2.5, 0.5)
                    .Margin(3, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center));
        }

        var savedBadge = Border(TextBlock("历史").FontSize(8.5).Foreground("#FFAAAAAA"))
            .Background("#1AFFFFFF")
            .CornerRadius(2.5)
            .Padding(2.5, 0.5)
            .Margin(3, 0, 0, 0)
            .VAlign(VerticalAlignment.Center);
        nameRowItems.Add(savedBadge);

        if (d?.Streak is { Count: >= 2 })
        {
            string streakText = $"{d.Streak.Count}{(d.Streak.IsWin ? "胜" : "负")}";
            string streakFg = d.Streak.IsWin ? "#FF10B981" : "#FFEF4444";
            string streakBg = d.Streak.IsWin ? "#2010B981" : "#20EF4444";

            nameRowItems.Add(
                Border(TextBlock(streakText).FontSize(8.5).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground(streakFg))
                    .Background(streakBg)
                    .CornerRadius(2.5)
                    .Padding(2.5, 0.5)
                    .Margin(3, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center));
        }

        var nameRow = HStack(0, nameRowItems.ToArray());

        // 段位展示（单双 / 灵活排位）
        Element rankRow;
        if (d?.Solo?.HasRank == true)
        {
            rankRow = HStack(3,
                CareerVisuals.RankedMedal(d.Solo.Tier, 13).VAlign(VerticalAlignment.Center),
                Border(TextBlock("单双").FontSize(8).Foreground("#FFAAAAAA"))
                    .Background("#1AFFFFFF")
                    .CornerRadius(2)
                    .Padding(2, 0.5)
                    .VAlign(VerticalAlignment.Center),
                TextBlock($"{d.Solo.TierText} {d.Solo.LeaguePoints} LP · {d.Solo.Wins}胜")
                    .FontSize(9.5)
                    .Foreground("#FFAAAAAA")
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .VAlign(VerticalAlignment.Center)
            );
        }
        else if (d?.Flex?.HasRank == true)
        {
            rankRow = HStack(3,
                CareerVisuals.RankedMedal(d.Flex.Tier, 13).VAlign(VerticalAlignment.Center),
                Border(TextBlock("灵活").FontSize(8).Foreground("#FFAAAAAA"))
                    .Background("#1AFFFFFF")
                    .CornerRadius(2)
                    .Padding(2, 0.5)
                    .VAlign(VerticalAlignment.Center),
                TextBlock($"{d.Flex.TierText} {d.Flex.LeaguePoints} LP · {d.Flex.Wins}胜")
                    .FontSize(9.5)
                    .Foreground("#FFAAAAAA")
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .VAlign(VerticalAlignment.Center)
            );
        }
        else
        {
            rankRow = TextBlock("无段位").FontSize(9.5).Foreground("#FF888888");
        }

        // 胜率与 KDA
        Element statsRow;
        if (d?.Loading == true)
        {
            statsRow = TextBlock("正在加载战绩...").FontSize(9.5).Foreground("#FF888888");
        }
        else if (d?.MatchHistoryHidden == true || d?.IsProfilePrivate == true)
        {
            statsRow = TextBlock("战绩已隐藏").FontSize(9.5).Foreground("#FF888888");
        }
        else if (d?.WinRate.HasValue == true)
        {
            int wr = (int)Math.Round(d.WinRate.Value);
            string wrColor = wr >= 70 ? "#FF10B981" : wr >= 50 ? "#FF059669" : "#FFEF4444";
            string kdaStr = d.AvgKda.HasValue ? $"KDA {d.AvgKda.Value:F2}" : string.Empty;

            string kdaColor = d.AvgKda switch
            {
                >= 5.0 => "#FFFFD700",
                >= 4.0 => "#FF3B82F6",
                >= 3.0 => "#FF10B981",
                _ => "#FFCCCCCC",
            };

            var statsItems = new List<Element>
            {
                TextBlock($"{wr}%").FontSize(10.5).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold).Foreground(wrColor),
                TextBlock(" · ").FontSize(9.5).Foreground("#FF888888"),
                TextBlock(kdaStr).FontSize(9.5).Foreground(kdaColor),
            };

            statsRow = HStack(0, statsItems.ToArray());
        }
        else
        {
            statsRow = TextBlock("暂无战绩").FontSize(9.5).Foreground("#FF888888");
        }

        var topContent = Grid(
            [GridSize.Auto, GridSize.Star()],
            [GridSize.Auto],
            [
                avatarElement.Grid(row: 0, column: 0).Margin(0, 0, 8, 0).VAlign(VerticalAlignment.Center),
                VStack(2, nameRow, rankRow, statsRow)
                    .Grid(row: 0, column: 1)
                    .VAlign(VerticalAlignment.Center),
            ]
        );

        // 底栏：左侧熟练度 + 招牌/练英雄，右侧位置分布 + 常用英雄小头像
        Element? bottomBar = null;
        if (masteryDetail is { Level: > 0 } || d?.Positions?.PrimaryPosition != null || d?.TopChampions is { Count: > 0 })
        {
            var leftItems = new List<Element>();
            if (masteryDetail is { Level: > 0 })
            {
                leftItems.Add(
                    Border(
                        TextBlock($"Lv.{masteryDetail.Level} {masteryDetail.FormattedPoints}")
                            .FontSize(8.5)
                            .Foreground("#FFAAAAAA")
                    )
                    .Background("#15FFFFFF")
                    .CornerRadius(3)
                    .Padding(3, 1)
                    .VAlign(VerticalAlignment.Center)
                );
            }

            if (masteryDetail?.IsTopChampion == true)
            {
                leftItems.Add(
                    Border(TextBlock("招牌").FontSize(8).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground("#FFF59E0B"))
                        .Background("#25F59E0B")
                        .CornerRadius(3)
                        .Padding(3, 1)
                        .Margin(3, 0, 0, 0)
                        .VAlign(VerticalAlignment.Center)
                );
            }
            else if (masteryDetail?.IsPracticing == true)
            {
                leftItems.Add(
                    Border(TextBlock("练英雄").FontSize(8).FontWeight(Microsoft.UI.Text.FontWeights.Bold).Foreground("#FFEF4444"))
                        .Background("#25EF4444")
                        .CornerRadius(3)
                        .Padding(3, 1)
                        .Margin(3, 0, 0, 0)
                        .VAlign(VerticalAlignment.Center)
                );
            }

            var rightItems = new List<Element>();
            if (d?.Positions?.PrimaryPosition != null)
            {
                string posStr = $"{SearchVisuals.PositionText(d.Positions.PrimaryPosition)} {d.Positions.PrimaryPercentage}%";
                if (d.Positions.SecondaryPosition != null && d.Positions.SecondaryPercentage > 0)
                {
                    posStr += $" / {d.Positions.SecondaryPercentage}%";
                }

                rightItems.Add(
                    TextBlock(posStr)
                        .FontSize(8.5)
                        .Foreground("#FFAAAAAA")
                        .VAlign(VerticalAlignment.Center)
                );
            }

            if (d?.TopChampions is { Count: > 0 })
            {
                foreach (var topChamp in d.TopChampions.Take(3))
                {
                    string champAsset = SearchVisuals.ChampionIcon(topChamp.ChampionId);
                    var champImg = Component<LcuImage, LcuImageProps>(new LcuImageProps(champAsset, Size: 16, CornerRadius: 3))
                        .Provide(AppHost.Context, services);

                    var champBadge = Border(champImg)
                        .Margin(2, 0, 0, 0)
                        .VAlign(VerticalAlignment.Center);

                    rightItems.Add(champBadge);
                }
            }

            bottomBar = Grid(
                [GridSize.Auto, GridSize.Star()],
                [GridSize.Auto],
                [
                    HStack(0, leftItems.ToArray()).Grid(row: 0, column: 0).VAlign(VerticalAlignment.Center),
                    HStack(0, rightItems.ToArray()).HAlign(HorizontalAlignment.Right).Grid(row: 0, column: 1).VAlign(VerticalAlignment.Center),
                ]
            )
            .Margin(0, 6, 0, 0);
        }

        Element fullContent = bottomBar is not null
            ? (Element)VStack(0, topContent, bottomBar)
            : topContent;

        return Border(fullContent)
            .Width(234)
            .Background(bg)
            .WithBorder(border, 1)
            .CornerRadius(8)
            .Padding(8)
            .Margin(0, 0, 0, 6);
    }
}
