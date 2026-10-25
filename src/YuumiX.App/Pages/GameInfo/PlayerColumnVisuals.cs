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

/// <summary>
/// 战绩列列头的复用视觉构件（头像、胜率竖条、名称/段位行与文案），
/// 对应旧版 <c>PlayerMatchColumn.vue</c> 的模板片段与计算属性。
/// </summary>
internal static class PlayerColumnVisuals
{
    /// <summary>胜率竖条宽度（旧版 3px 宽、与头像同高、自底向上填充）。</summary>
    private const double WinRateBarWidth = 3;

    /// <summary>英雄头像（招牌金环 + 右上角招牌/练英雄角标）。</summary>
    internal static Element BuildChampionAvatar(
        int champId,
        int profileIconId,
        AppServices services,
        ChampionMasteryDetail? masteryDetail,
        double size,
        double cornerRadius)
    {
        string avatarAsset = champId > 0
            ? SearchVisuals.ChampionIcon(champId)
            : CareerVisuals.ProfileIcon(profileIconId > 0 ? profileIconId : 29);

        var avatarCore = Border(
            Component<LcuImage, LcuImageProps>(new LcuImageProps(avatarAsset, Size: size, CornerRadius: cornerRadius))
                .Provide(AppHost.Context, services))
            .Width(size)
            .Height(size)
            .CornerRadius(cornerRadius)
            // 顶级英雄金描边；非顶级厚度为 0（描边不参与绘制，色值无意义）
            .WithBorder(masteryDetail?.IsTopChampion == true ? AssetColors.PureGold : AssetColors.TeamBlue, masteryDetail?.IsTopChampion == true ? 1.5 : 0);

        var layers = new List<Element> { avatarCore };

        if (masteryDetail?.IsPracticing == true)
        {
            layers.Add(
                Border(Caption("练")
                        .Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush"))
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center))
                    .Width(12)
                    .Height(12)
                    .CornerRadius(6)
                    .Background(AssetColors.LoseDark)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Top)
                    .Margin(0, -2, -2, 0)
                    .WithToolTip(TextBlock("练英雄/新手状态 (熟练度≤1级或点数<5000，近期少用或初次尝试)")));
        }
        else if (masteryDetail?.IsTopChampion == true)
        {
            layers.Add(
                Border(Caption("★")
                        .Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush"))
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center))
                    .Width(12)
                    .Height(12)
                    .CornerRadius(6)
                    .Background(AssetColors.Amber)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Top)
                    .Margin(0, -2, -2, 0)
                    .WithToolTip(TextBlock("招牌/绝活英雄 (熟练度排名前三且≥1万分，或累计≥10万分)")));
        }

        return Grid([GridSize.Auto], [GridSize.Auto], layers.ToArray());
    }

    /// <summary>头像左侧胜率竖条：50% 时几乎不填充，偏离越大填充越高（旧版 wr-bar）。</summary>
    internal static Element BuildWinRateBar(double? winRate, YuumiPalette palette, double height)
    {
        double fillHeight = winRate.HasValue
            ? Math.Max(height * Math.Abs(winRate.Value - 50) * 2 / 100, 2)
            : 0;

        var fill = Border(Empty())
            .Height(fillHeight)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Bottom)
            .CornerRadius(WinRateBarWidth / 2)
            .Background(winRate >= 50 ? palette.WinText : palette.LoseText);

        var bar = Border(
            Grid([GridSize.Star()], [GridSize.Star()], [fill]))
            .Width(WinRateBarWidth)
            .Height(height)
            .CornerRadius(WinRateBarWidth / 2)
            .Background(Theme.SubtleFill);

        return winRate.HasValue
            ? bar.WithToolTip(TextBlock($"近期胜率 {(int)Math.Round(winRate.Value)}%"))
            : bar;
    }

    /// <summary>召唤师名行：名称（点击查战绩）+ 战绩隐藏锁 + 宿命/路人集徽标（按需）。</summary>
    internal static Element BuildNameRow(
        PlayerMatchColumnProps props,
        YuumiPalette palette,
        bool showFateBadge,
        bool showSavedBadge,
        SavedPlayerMarker? savedInfo = null)
    {
        var p = props.Player;
        var d = props.PlayerData;

        string displayName = PlayerNameVisuals.GetDisplayName(p, d, 0);
        string searchName = PlayerNameVisuals.GetSearchName(p, d);

        var nameText = Caption(displayName)
            .Foreground(Theme.PrimaryText)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .VAlign(VerticalAlignment.Center);

        if (!string.IsNullOrEmpty(searchName))
        {
            // 演示数据的 puuid 是占位值，跳转战绩查询必然查不到，直接不注册点击
            nameText = props.IsDemoData
                ? nameText.WithToolTip(TextBlock("演示数据：不支持跳转生涯与战绩查询"))
                : nameText
                    .WithToolTip(TextBlock($"搜索 {searchName}"))
                    .OnTapped((_, _) =>
                    {
                        string puuid = d?.Info?.Puuid ?? p.Puuid ?? string.Empty;
                        props.OnNavigateSearch?.Invoke(searchName, puuid);
                    });
        }

        var items = new List<Element> { nameText };

        if (d?.MatchHistoryHidden == true || d?.IsProfilePrivate == true)
        {
            items.Add(
                Caption("\uE72E")
                    .FontFamily("Segoe Fluent Icons")
                    .Foreground(AssetColors.AmberDeep)
                    .Margin(3, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(TextBlock("战绩已隐藏")));
        }

        if (showFateBadge && !string.IsNullOrEmpty(d?.FateFlag))
        {
            bool isAlly = d.FateFlag == "ally";
            items.Add(
                Border(Caption(FateBadgeVisuals.GetBadgeText(d))
                        .Foreground(isAlly ? palette.WinText : palette.LoseText))
                    .Background(isAlly ? palette.WinBg : palette.LoseBg)
                    .CornerRadius(3)
                    .Padding(3, 0.5)
                    .Margin(3, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(FateBadgeVisuals.BuildToolTip(d)));
        }

        if (showSavedBadge && savedInfo is not null && FateBadgeVisuals.ShouldShowSavedBadge(savedInfo, d))
        {
            items.Add(FateBadgeVisuals.BuildSavedBadge(savedInfo).Margin(3, 0, 0, 0));
        }

        return HStack(0, items.ToArray());
    }

    /// <summary>段位行：可选前置熟练度徽章，随后是段位图标 + 短名；均无段位显示「无段位」。</summary>
    internal static Element BuildRankRow(
        GameInfoPlayer? d,
        ChampionMasteryDetail? masteryDetail,
        bool withMastery)
    {
        var items = new List<Element>();

        if (withMastery && masteryDetail is { Level: > 0 })
        {
            items.Add(BuildMasteryChip(masteryDetail).Margin(0, 0, 4, 0));
        }

        var activeRank = d?.PrimaryRank;
        if (activeRank?.HasRank == true)
        {
            items.Add(
                HStack(2,
                    CareerVisuals.RankedMedal(activeRank.Tier, 13).VAlign(VerticalAlignment.Center),
                    Caption(ShortRank(activeRank.Tier, activeRank.Division))
                        .Foreground(Theme.SecondaryText)
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .VAlign(VerticalAlignment.Center))
                .WithToolTip(TextBlock(BuildRankDetailedTooltip(d, masteryDetail))));
        }
        else
        {
            items.Add(Caption("无段位").Foreground(Theme.DisabledText).VAlign(VerticalAlignment.Center));
        }

        return HStack(0, items.ToArray());
    }

    internal static Element BuildMasteryChip(ChampionMasteryDetail masteryDetail)
    {
        return Border(
            Caption($"Lv.{masteryDetail.Level}")
                .SemiBold()
                .Foreground(Theme.SecondaryText))
            .Background(Theme.SubtleFill)
            .WithBorder(Theme.CardStroke, 1)
            .CornerRadius(3)
            .Padding(3, 0.5)
            .VAlign(VerticalAlignment.Center)
            .WithToolTip(TextBlock($"英雄熟练度: Lv.{masteryDetail.Level} ({masteryDetail.Points:N0} 点数)"));
    }

    // ─────────────────────────────────────────────────────────────
    // 文案与 ToolTip（沿用旧版措辞）
    // ─────────────────────────────────────────────────────────────

    internal static string ShortRank(string? tier, string? division)
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

    internal static string ShortPosition(MatchPosition? pos) => pos switch
    {
        MatchPosition.Top => "上",
        MatchPosition.Jungle => "野",
        MatchPosition.Middle => "中",
        MatchPosition.Bottom => "下",
        MatchPosition.Utility => "辅",
        _ => string.Empty,
    };

    internal static string BuildRankDetailedTooltip(GameInfoPlayer? d, ChampionMasteryDetail? mastery)
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

    internal static string BuildPosTooltip(GameInfoPlayer? d)
    {
        if (d?.Positions?.PrimaryPosition == null) return string.Empty;
        var pos = d.Positions;
        string? name = SearchVisuals.PositionText(pos.PrimaryPosition);
        var lines = new List<string>();

        if (pos.SecondaryPosition != null && pos.SecondaryPercentage > 0)
        {
            string? secName = SearchVisuals.PositionText(pos.SecondaryPosition);
            lines.Add($"常玩位置: 主选{name} ({pos.PrimaryPercentage}%) · 次选{secName} ({pos.SecondaryPercentage}%) · 近期 {pos.TotalValid} 场");
        }
        else
        {
            lines.Add($"常玩位置: {name} ({pos.PrimaryPercentage}%) · 近期 {pos.TotalValid} 场");
        }

        foreach (var entry in pos.Entries)
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

    /// <summary>分路小图标（11px 默认，带专属色位图）。</summary>
    internal static Element BuildPositionIcon(MatchPosition? pos, double size = 11)
    {
        string? uri = AppAssets.GetPositionIconUri(pos);
        if (string.IsNullOrEmpty(uri))
        {
            return Empty();
        }

        return Image(uri)
            .Width(size)
            .Height(size)
            .VAlign(VerticalAlignment.Center)
            .AccessibilityHidden();
    }

    /// <summary>
    /// 全部战绩列头常玩位置行：主选位置图标 + 简称与占比，次选位置图标 + 简称与占比（对齐旧版）。
    /// </summary>
    internal static Element BuildPositionsRow(GameInfoPlayer? d)
    {
        if (d?.Positions?.PrimaryPosition is not { } primary)
        {
            return Caption(" ").Foreground(Theme.DisabledText);
        }

        var items = new List<Element>
        {
            BuildPositionIcon(primary, 10).Margin(0, 0, 2, 0),
            Caption($"{ShortPosition(primary)} {d.Positions.PrimaryPercentage}%")
                .Foreground(Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center),
        };

        if (d.Positions.SecondaryPosition is { } secondary && d.Positions.SecondaryPercentage > 0)
        {
            items.Add(Caption(" / ").Foreground(Theme.DisabledText).VAlign(VerticalAlignment.Center));
            items.Add(BuildPositionIcon(secondary, 10).Margin(0, 0, 2, 0));
            items.Add(Caption($"{ShortPosition(secondary)} {d.Positions.SecondaryPercentage}%")
                .Foreground(Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center));
        }

        var row = HStack(0, items.ToArray()).HAlign(HorizontalAlignment.Center);
        string posTip = BuildPosTooltip(d);
        if (!string.IsNullOrEmpty(posTip))
        {
            row = row.WithToolTip(TextBlock(posTip));
        }

        return row;
    }
}
