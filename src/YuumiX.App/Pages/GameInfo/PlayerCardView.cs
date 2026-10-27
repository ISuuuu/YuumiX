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
using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.App.Pages.GameInfo;

public sealed record PlayerCardProps(
    PremadePlayerLike Player,
    GameInfoPlayer? PlayerData,
    int PremadeIdx,
    int Index,
    YuumiPalette Palette,
    string? SelfPuuid = null,
    IReadOnlyDictionary<string, SavedPlayerMarker>? SavedMap = null,
    Action<string, SummonerProfile?>? OnNavigateCareer = null,
    Action<string, string?>? OnNavigateSearch = null,
    bool IsDemoData = false);

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
        var palette = Props.Palette;

        int champId = p.ChampionId > 0 ? p.ChampionId : (d?.ChampionId ?? 0);
        string avatarAsset = champId > 0
            ? $"/lol-game-data/assets/v1/champion-icons/{champId}.png"
            : $"/lol-game-data/assets/v1/profile-icons/{(p.ProfileIconId > 0 ? p.ProfileIconId : 29)}.png";

        var masteryDetail = ChampionMasteryDetail.Create(d?.Masteries, champId);

        var premadeColor = premadeIdx >= 0 ? PremadeColors.GetColor(premadeIdx) : null;

        // 头像区（42x42，圆形）
        int xpProgress = d?.Info?.PercentCompleteForNextLevel ?? 0;
        // 顶级英雄金描边；其余场景厚度 0 或 1.5 淡蓝（厚度为 0 时描边不参与绘制，色值无意义）
        string ringBorderColor = masteryDetail?.IsTopChampion == true
            ? AssetColors.PureGold
            : YuumiPalette.WithAlpha(AssetColors.TeamBlue, 0x44);
        double ringBorderThickness = masteryDetail?.IsTopChampion == true ? 2.0 : (xpProgress > 0 ? 1.5 : 0.0);

        var avatarCore = Border(
            Component<LcuImage, LcuImageProps>(new LcuImageProps(avatarAsset, Size: 42, CornerRadius: YuumiTheme.PillRadius))
                .Provide(AppHost.Context, services)
        )
        .Width(42)
        .Height(42)
        .CornerRadius(YuumiTheme.PillRadius)
        .WithBorder(ringBorderColor, ringBorderThickness);

        var avatarStack = new List<Element> { avatarCore };

        if (masteryDetail?.IsPracticing == true)
        {
            var practiceBadge = Border(
                Caption("练")
                    .Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush"))
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
            )
            .Width(13)
            .Height(13)
            .CornerRadius(YuumiTheme.PillRadius)
            .Background(AssetColors.BadgeRose)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Bottom)
            .WithToolTip(TextBlock("练英雄/新手状态 (熟练度≤1级或点数<5000，近期少用或初次尝试)"));

            avatarStack.Add(practiceBadge);
        }

        // 头像下方召唤师等级微标（如 622、883 等）
        if (d?.Info?.SummonerLevel > 0)
        {
            var levelBadge = Border(
                Caption($"{d.Info.SummonerLevel}")
                    .SemiBold()
                    .Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush"))
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
            )
            .Background(AssetColors.BadgeDark)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Padding(4, 1)
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

        var nameText = Caption(displayName)
            .Foreground(Theme.PrimaryText)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .VAlign(VerticalAlignment.Center);

        if (hasSearchTarget)
        {
            // 演示数据的 puuid 是占位值，跳转生涯/战绩查询必然查不到，直接不注册点击
            nameText = Props.IsDemoData
                ? nameText.WithToolTip(TextBlock("演示数据：不支持跳转生涯与战绩查询"))
                : nameText
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
                Badge.Create(Caption("我").Foreground(Theme.Accent), Theme.SubtleFill, padH: 2.5, padV: 0.5)
                    .Margin(4, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center));
        }

        if (d?.MatchHistoryHidden == true || d?.IsProfilePrivate == true)
        {
            nameRowItems.Add(
                Caption(Icons.View)
                    .FontFamily(Icons.FontFamily)
                    .Foreground(AssetColors.AmberDeep)
                    .Margin(4, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(TextBlock("战绩已隐藏")));
        }

        if (!string.IsNullOrEmpty(d?.FateFlag))
        {
            bool isAlly = d.FateFlag == "ally";
            string fateText = FateBadgeVisuals.GetBadgeText(d);
            string fateBg = isAlly ? palette.WinBg : palette.LoseBg;
            string fateFg = isAlly ? palette.WinText : palette.LoseText;

            nameRowItems.Add(
                Badge.Create(Caption(fateText).Foreground(fateFg), fateBg, padH: 2.5, padV: 0.5)
                    .Margin(4, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(FateBadgeVisuals.BuildToolTip(d)));
        }

        // 路人集徽标：有 tag 显示 tag 主色胶囊；无 tag 且无宿命徽标时显示灰「历史」；自身不显示（§2.2 / §4.8）
        var savedInfo = FateBadgeVisuals.ResolveSavedInfo(d, p, Props.SavedMap, Props.SelfPuuid);
        if (savedInfo is not null && FateBadgeVisuals.ShouldShowSavedBadge(savedInfo, d))
        {
            nameRowItems.Add(
                FateBadgeVisuals.BuildSavedBadge(savedInfo)
                    .Margin(4, 0, 0, 0));
        }

        if (d?.Streak is { Count: >= 2 })
        {
            string streakText = $"{d.Streak.Count}{(d.Streak.IsWin ? "胜" : "负")}";
            string streakFg = d.Streak.IsWin ? palette.WinText : palette.LoseText;
            string streakBg = d.Streak.IsWin ? palette.WinBg : palette.LoseBg;

            nameRowItems.Add(
                Badge.Create(Caption(streakText).Foreground(streakFg), streakBg, padH: 2.5, padV: 0.5)
                    .Margin(4, 0, 0, 0)
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
                Badge.Create(Caption("单双").Foreground(AssetColors.BlueEssence), AssetColors.BlueFaint, padH: 2, padV: 0.5)
                    .VAlign(VerticalAlignment.Center),
                Caption($"{d.Solo.TierText} {d.Solo.LeaguePoints} LP · {d.Solo.Wins}胜")
                    .Foreground(Theme.SecondaryText)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .VAlign(VerticalAlignment.Center)
            ).WithToolTip(TextBlock(BuildRankTooltip("单双排", d.Solo)));

            var flexElement = HStack(3,
                CareerVisuals.RankedMedal(d.Flex!.Tier, 13).VAlign(VerticalAlignment.Center),
                Badge.Create(Caption("灵活").Foreground(palette.WinText), palette.WinBg, padH: 2, padV: 0.5)
                    .VAlign(VerticalAlignment.Center),
                Caption($"{d.Flex.TierText} {d.Flex.LeaguePoints} LP · {d.Flex.Wins}胜")
                    .Foreground(Theme.SecondaryText)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .VAlign(VerticalAlignment.Center)
            ).WithToolTip(TextBlock(BuildRankTooltip("灵活排", d.Flex)));

            rankRow = VStack(1, soloElement, flexElement);
        }
        else if (hasSolo)
        {
            rankRow = HStack(3,
                CareerVisuals.RankedMedal(d!.Solo!.Tier, 13).VAlign(VerticalAlignment.Center),
                Badge.Create(Caption("单双").Foreground(AssetColors.BlueEssence), AssetColors.BlueFaint, padH: 2, padV: 0.5)
                    .VAlign(VerticalAlignment.Center),
                Caption($"{d.Solo.TierText} {d.Solo.LeaguePoints} LP · {d.Solo.Wins}胜")
                    .Foreground(Theme.SecondaryText)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .VAlign(VerticalAlignment.Center)
            ).WithToolTip(TextBlock(BuildRankTooltip("单双排", d.Solo)));
        }
        else if (hasFlex)
        {
            rankRow = HStack(3,
                CareerVisuals.RankedMedal(d!.Flex!.Tier, 13).VAlign(VerticalAlignment.Center),
                Badge.Create(Caption("灵活").Foreground(palette.WinText), palette.WinBg, padH: 2, padV: 0.5)
                    .VAlign(VerticalAlignment.Center),
                Caption($"{d.Flex.TierText} {d.Flex.LeaguePoints} LP · {d.Flex.Wins}胜")
                    .Foreground(Theme.SecondaryText)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .VAlign(VerticalAlignment.Center)
            ).WithToolTip(TextBlock(BuildRankTooltip("灵活排", d.Flex)));
        }
        else
        {
            rankRow = Caption("无段位")
                .Foreground(Theme.DisabledText)
                .WithToolTip(TextBlock("单双排: 无段位\n灵活排: 无段位"));
        }

        // 胜率与 KDA（色阶修正：≥70 绿 / ≥50 蓝 / <50 灰，带 ToolTip，§2.2）
        Element statsRow;
        if (d?.Loading == true)
        {
            statsRow = Caption("正在加载战绩...").Foreground(Theme.DisabledText);
        }
        else if (d?.MatchHistoryHidden == true || d?.IsProfilePrivate == true)
        {
            statsRow = Caption("战绩已隐藏").Foreground(Theme.DisabledText);
        }
        else if (d?.WinRate.HasValue == true)
        {
            int wr = (int)Math.Round(d.WinRate.Value);
            string kdaStr = d.AvgKda.HasValue ? $"KDA {d.AvgKda.Value:F2}" : string.Empty;

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

            // 胜率与 KDA 的着色横跨业务色（string）与语义令牌（ThemeRef），无法共存于同一
            // 三元/switch，按 CareerMatchRow 的既有做法在元素级分支。
            var wrBlock = Caption($"{wr}%").SemiBold();
            wrBlock = wr >= 70
                ? wrBlock.Foreground(BrushHelper.Parse(palette.WinText))
                : wr >= 50 ? wrBlock.Foreground(Theme.Accent) : wrBlock.Foreground(Theme.TertiaryText);

            // 色阶对齐旧版 PlayerCard.vue：≥5 强调色 / ≥4 段位蓝 / ≥3 胜利绿 / 其余灰。
            // 旧版最高档位用的是 --accent-color（不是纯金黄 #FFD700，那个在小字号下过于刺眼）。
            var kdaBlock = Caption(kdaStr);
            if (d.AvgKda is { } avgKda)
            {
                kdaBlock = avgKda switch
                {
                    >= 5.0 => kdaBlock.Foreground(Theme.Accent),
                    >= 4.0 => kdaBlock.Foreground(BrushHelper.Parse(CareerVisuals.IsDarkTheme ? AssetColors.BlueSoft : AssetColors.BlueEssence)),
                    >= 3.0 => kdaBlock.Foreground(BrushHelper.Parse(palette.WinText)),
                    _ => kdaBlock.Foreground(Theme.TertiaryText),
                };
            }
            else
            {
                kdaBlock = kdaBlock.Foreground(Theme.TertiaryText);
            }

            wrBlock = wrBlock.WithToolTip(TextBlock(wrTip));
            kdaBlock = kdaBlock.WithToolTip(TextBlock(kdaTip));

            var statsItems = new List<Element>
            {
                wrBlock,
                Caption(" · ").Foreground(Theme.DisabledText),
                kdaBlock,
            };

            statsRow = HStack(0, statsItems.ToArray());
        }
        else
        {
            statsRow = Caption("暂无战绩").Foreground(Theme.DisabledText);
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
                        Caption($"Lv.{masteryDetail.Level} {masteryDetail.FormattedPoints}")
                            .Foreground(Theme.SecondaryText)
                    )
                    .Background(Theme.SubtleFill)
                    .CornerRadius(YuumiTheme.ControlRadius)
                    .Padding(4, 1)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(TextBlock(masteryTip))
                );
            }

            if (masteryDetail?.IsTopChampion == true)
            {
                leftItems.Add(
                    Border(Caption("招牌").Foreground(AssetColors.Amber))
                        .Background(AssetColors.AmberFaint)
                        .CornerRadius(YuumiTheme.ControlRadius)
                        .Padding(4, 1)
                        .Margin(4, 0, 0, 0)
                        .VAlign(VerticalAlignment.Center)
                        .WithToolTip(TextBlock("招牌/绝活英雄 (熟练度排名前三且≥1万分，或累计≥10万分)"))
                );
            }
            else if (masteryDetail?.IsPracticing == true)
            {
                leftItems.Add(
                    Border(Caption("练英雄").Foreground(palette.LoseText))
                        .Background(palette.LoseBg)
                        .CornerRadius(YuumiTheme.ControlRadius)
                        .Padding(4, 1)
                        .Margin(4, 0, 0, 0)
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
                    Caption(posStr)
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)
                )
                .Background(Theme.SubtleFill)
                .CornerRadius(YuumiTheme.ControlRadius)
                .Padding(4, 1)
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
                        .Margin(4, 0, 0, 0)
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
            .Margin(0, 4, 0, 0);
        }

        Element fullContent = bottomBar is not null
            ? (Element)VStack(0, topContent, bottomBar)
            : topContent;

        var card = Border(fullContent)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Padding(8, 8);

        return premadeColor is null
            ? card.Background(Theme.SubtleFill).WithBorder(Theme.CardStroke, 1)
            : card.Background(premadeColor.Bg).WithBorder(premadeColor.Border, 1);
    }

    private static string BuildRankTooltip(string queueName, RankedQueueEntry q)
    {
        int total = q.Wins + q.Losses;
        int wr = total > 0 ? (int)Math.Round((double)q.Wins / total * 100) : 0;
        string stats = total > 0 ? $" ({q.Wins}胜{q.Losses}负 · {wr}%胜率)" : "";
        return $"{queueName}: {q.TierText} {q.LeaguePoints} LP{stats}";
    }
}
