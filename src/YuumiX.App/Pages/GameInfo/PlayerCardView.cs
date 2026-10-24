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
using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.App.Pages.GameInfo;

public sealed record PlayerCardProps(
    PremadePlayerLike Player,
    GameInfoPlayer? PlayerData,
    int PremadeIdx,
    int Index,
    string? SelfPuuid = null,
    IReadOnlyDictionary<string, SavedPlayerMarker>? SavedMap = null,
    Action<string, SummonerProfile?>? OnNavigateCareer = null,
    Action<string, string?>? OnNavigateSearch = null);

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
        int xpProgress = d?.Info?.PercentCompleteForNextLevel ?? 0;
        string ringBorderColor = masteryDetail?.IsTopChampion == true
            ? "#FFFFD700"
            : (xpProgress > 0 ? "#443B82F6" : "#00000000");
        double ringBorderThickness = masteryDetail?.IsTopChampion == true ? 2.0 : (xpProgress > 0 ? 1.5 : 0.0);

        var avatarCore = Border(
            Component<LcuImage, LcuImageProps>(new LcuImageProps(avatarAsset, Size: 42, CornerRadius: 21))
                .Provide(AppHost.Context, services)
        )
        .Width(42)
        .Height(42)
        .CornerRadius(21)
        .WithBorder(ringBorderColor, ringBorderThickness);

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
            .VAlign(VerticalAlignment.Bottom)
            .WithToolTip(TextBlock("练英雄/新手状态 (熟练度≤1级或点数<5000，近期少用或初次尝试)"));

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
        string displayName = PlayerNameVisuals.GetDisplayName(p, d, Props.Index);
        string searchName = PlayerNameVisuals.GetSearchName(p, d);
        bool hasSearchTarget = !string.IsNullOrEmpty(searchName);

        bool isSelf = !string.IsNullOrEmpty(Props.SelfPuuid) &&
                      (!string.IsNullOrEmpty(p.Puuid) && p.Puuid == Props.SelfPuuid ||
                       d?.Info?.Puuid == Props.SelfPuuid);

        var nameText = TextBlock(displayName)
            .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
            .FontSize(12)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .VAlign(VerticalAlignment.Center);

        if (hasSearchTarget)
        {
            nameText = nameText
                .WithToolTip(TextBlock($"生涯 {searchName}"))
                .OnTapped((_, _) =>
                {
                    string puuid = d?.Info?.Puuid ?? p.Puuid ?? string.Empty;
                    if (!string.IsNullOrEmpty(puuid) && puuid != "00000000-0000-0000-0000-000000000000")
                    {
                        Props.OnNavigateCareer?.Invoke(puuid, d?.Info);
                    }
                    else
                    {
                        Props.OnNavigateSearch?.Invoke(searchName, null);
                    }
                });
        }

        var nameRowItems = new List<Element> { nameText };

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
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(TextBlock("战绩已隐藏")));
        }

        if (!string.IsNullOrEmpty(d?.FateFlag))
        {
            bool isAlly = d.FateFlag == "ally";
            string fateText = FateBadgeVisuals.GetBadgeText(d);
            string fateBg = isAlly ? "#2010B981" : "#20EF4444";
            string fateFg = isAlly ? "#FF10B981" : "#FFEF4444";

            nameRowItems.Add(
                Border(TextBlock(fateText).FontSize(8.5).Foreground(fateFg))
                    .Background(fateBg)
                    .CornerRadius(2.5)
                    .Padding(2.5, 0.5)
                    .Margin(3, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(FateBadgeVisuals.BuildToolTip(d)));
        }

        // 路人集徽标：有 tag 显示 tag 主色胶囊；无 tag 且无宿命徽标时显示灰「历史」；自身不显示（§2.2 / §4.8）
        var savedInfo = FateBadgeVisuals.ResolveSavedInfo(d, p, Props.SavedMap, Props.SelfPuuid);
        if (savedInfo is not null && FateBadgeVisuals.ShouldShowSavedBadge(savedInfo, d))
        {
            nameRowItems.Add(
                FateBadgeVisuals.BuildSavedBadge(savedInfo, 8.5)
                    .Margin(3, 0, 0, 0));
        }

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
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(TextBlock(d.Streak.IsWin ? $"{d.Streak.Count}连胜" : $"{d.Streak.Count}连败")));
        }

        var nameRow = HStack(0, nameRowItems.ToArray());

        // 段位展示（单双 / 灵活排位：双段位两行，单段位单行，均带 ToolTip，§2.2）
        bool hasSolo = d?.Solo?.HasRank == true;
        bool hasFlex = d?.Flex?.HasRank == true;

        Element rankRow;
        if (hasSolo && hasFlex)
        {
            var soloElement = HStack(3,
                CareerVisuals.RankedMedal(d!.Solo!.Tier, 13).VAlign(VerticalAlignment.Center),
                Border(TextBlock("单双").FontSize(8).Foreground("#FF3B82F6"))
                    .Background("#203B82F6")
                    .CornerRadius(2)
                    .Padding(2, 0.5)
                    .VAlign(VerticalAlignment.Center),
                TextBlock($"{d.Solo.TierText} {d.Solo.LeaguePoints} LP · {d.Solo.Wins}胜")
                    .FontSize(9.5)
                    .Foreground("#FFAAAAAA")
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .VAlign(VerticalAlignment.Center)
            ).WithToolTip(TextBlock(BuildRankTooltip("单双排", d.Solo)));

            var flexElement = HStack(3,
                CareerVisuals.RankedMedal(d.Flex!.Tier, 13).VAlign(VerticalAlignment.Center),
                Border(TextBlock("灵活").FontSize(8).Foreground("#FF10B981"))
                    .Background("#2010B981")
                    .CornerRadius(2)
                    .Padding(2, 0.5)
                    .VAlign(VerticalAlignment.Center),
                TextBlock($"{d.Flex.TierText} {d.Flex.LeaguePoints} LP · {d.Flex.Wins}胜")
                    .FontSize(9.5)
                    .Foreground("#FFAAAAAA")
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .VAlign(VerticalAlignment.Center)
            ).WithToolTip(TextBlock(BuildRankTooltip("灵活排", d.Flex)));

            rankRow = VStack(1, soloElement, flexElement);
        }
        else if (hasSolo)
        {
            rankRow = HStack(3,
                CareerVisuals.RankedMedal(d!.Solo!.Tier, 13).VAlign(VerticalAlignment.Center),
                Border(TextBlock("单双").FontSize(8).Foreground("#FF3B82F6"))
                    .Background("#203B82F6")
                    .CornerRadius(2)
                    .Padding(2, 0.5)
                    .VAlign(VerticalAlignment.Center),
                TextBlock($"{d.Solo.TierText} {d.Solo.LeaguePoints} LP · {d.Solo.Wins}胜")
                    .FontSize(9.5)
                    .Foreground("#FFAAAAAA")
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .VAlign(VerticalAlignment.Center)
            ).WithToolTip(TextBlock(BuildRankTooltip("单双排", d.Solo)));
        }
        else if (hasFlex)
        {
            rankRow = HStack(3,
                CareerVisuals.RankedMedal(d!.Flex!.Tier, 13).VAlign(VerticalAlignment.Center),
                Border(TextBlock("灵活").FontSize(8).Foreground("#FF10B981"))
                    .Background("#2010B981")
                    .CornerRadius(2)
                    .Padding(2, 0.5)
                    .VAlign(VerticalAlignment.Center),
                TextBlock($"{d.Flex.TierText} {d.Flex.LeaguePoints} LP · {d.Flex.Wins}胜")
                    .FontSize(9.5)
                    .Foreground("#FFAAAAAA")
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .VAlign(VerticalAlignment.Center)
            ).WithToolTip(TextBlock(BuildRankTooltip("灵活排", d.Flex)));
        }
        else
        {
            rankRow = TextBlock("无段位")
                .FontSize(9.5)
                .Foreground("#FF888888")
                .WithToolTip(TextBlock("单双排: 无段位\n灵活排: 无段位"));
        }

        // 胜率与 KDA（色阶修正：≥70 绿 / ≥50 蓝 / <50 灰，带 ToolTip，§2.2）
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
            string wrColor = wr >= 70 ? "#FF10B981" : wr >= 50 ? "#FF3B82F6" : "#FF94A3B8";
            string kdaStr = d.AvgKda.HasValue ? $"KDA {d.AvgKda.Value:F2}" : string.Empty;

            string kdaColor = d.AvgKda switch
            {
                >= 5.0 => "#FFFFD700",
                >= 4.0 => "#FF3B82F6",
                >= 3.0 => "#FF10B981",
                _ => "#FFCCCCCC",
            };

            int totalGames = (d.WinCount ?? 0) + (d.LossesCount ?? 0);
            if (totalGames == 0) totalGames = d.Matches?.Count ?? 0;
            string wrTip = $"近期胜率\n统计范围: 近 {totalGames} 场对局\n战绩详情: {d.WinCount ?? 0}胜 / {d.LossesCount ?? 0}负 ({wr}%)\n近期有效对局的胜负比例";

            var validMatches = d.Matches?.Where(m => !m.Remake).ToList();
            string kdaTip = $"近期场均 KDA\n综合数值: {(d.AvgKda.HasValue ? d.AvgKda.Value.ToString("F2") : "0.00")}\n计算公式: (击杀 + 助攻) ÷ 死亡";
            if (validMatches is { Count: > 0 })
            {
                double avgK = validMatches.Average(m => m.Kills);
                double avgD = validMatches.Average(m => m.Deaths);
                double avgA = validMatches.Average(m => m.Assists);
                kdaTip = $"近期场均 KDA\n场均数据: {avgK:F1} / {avgD:F1} / {avgA:F1}\n综合数值: {(d.AvgKda.HasValue ? d.AvgKda.Value.ToString("F2") : "0.00")}\n计算公式: (击杀 + 助攻) ÷ 死亡";
            }

            var wrBlock = TextBlock($"{wr}%")
                .FontSize(10.5)
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                .Foreground(wrColor)
                .WithToolTip(TextBlock(wrTip));

            var kdaBlock = TextBlock(kdaStr)
                .FontSize(9.5)
                .Foreground(kdaColor)
                .WithToolTip(TextBlock(kdaTip));

            var statsItems = new List<Element>
            {
                wrBlock,
                TextBlock(" · ").FontSize(9.5).Foreground("#FF888888"),
                kdaBlock,
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

        // 底栏：左侧熟练度 + 招牌/练英雄，右侧位置分布 + 常用英雄小头像（带 ToolTip）
        Element? bottomBar = null;
        if (masteryDetail is { Level: > 0 } || d?.Positions?.PrimaryPosition != null || d?.TopChampions is { Count: > 0 })
        {
            var leftItems = new List<Element>();
            if (masteryDetail is { Level: > 0 })
            {
                string masteryTip = $"英雄熟练度: Lv.{masteryDetail.Level} ({masteryDetail.Points:N0} 点数)";
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
                    .WithToolTip(TextBlock(masteryTip))
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
                        .WithToolTip(TextBlock("招牌/绝活英雄 (熟练度排名前三且≥1万分，或累计≥10万分)"))
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
                        .WithToolTip(TextBlock("练英雄/新手状态 (熟练度≤1级或点数<5000，近期少用或初次尝试)"))
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

                string posTip = $"近期位置分布 ({d.Positions.TotalValid}场)\n" +
                    string.Join("\n", d.Positions.Entries.Select(e => $"{e.Name}: {e.Count}场 · {e.Percentage}%"));

                var posPill = Border(
                    TextBlock(posStr)
                        .FontSize(8.5)
                        .Foreground("#FFAAAAAA")
                        .VAlign(VerticalAlignment.Center)
                )
                .Background("#12FFFFFF")
                .CornerRadius(3)
                .Padding(3, 1)
                .WithToolTip(TextBlock(posTip));

                rightItems.Add(posPill);
            }

            if (d?.TopChampions is { Count: > 0 })
            {
                foreach (var topChamp in d.TopChampions.Take(3))
                {
                    string champAsset = SearchVisuals.ChampionIcon(topChamp.ChampionId);
                    var champImg = Component<LcuImage, LcuImageProps>(new LcuImageProps(champAsset, Size: 16, CornerRadius: 3))
                        .Provide(AppHost.Context, services);

                    string posDesc = topChamp.PrimaryPosition is not null ? $" · {SearchVisuals.PositionText(topChamp.PrimaryPosition)}" : "";
                    string champTip = $"出场 {topChamp.Games} 次{posDesc}\n{topChamp.WinRate}% 胜率 · {topChamp.Wins}胜 {topChamp.Losses}负 · KDA {topChamp.Kda}";

                    var champBadge = Border(champImg)
                        .Margin(2, 0, 0, 0)
                        .VAlign(VerticalAlignment.Center)
                        .WithToolTip(TextBlock(champTip));

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
            .Background(bg)
            .WithBorder(border, 1)
            .CornerRadius(6)
            .Padding(10, 8);
    }

    private static string BuildRankTooltip(string queueName, RankedQueueEntry q)
    {
        int total = q.Wins + q.Losses;
        int wr = total > 0 ? (int)Math.Round((double)q.Wins / total * 100) : 0;
        string stats = total > 0 ? $" ({q.Wins}胜{q.Losses}负 · {wr}%胜率)" : "";
        return $"{queueName}: {q.TierText} {q.LeaguePoints} LP{stats}";
    }
}
