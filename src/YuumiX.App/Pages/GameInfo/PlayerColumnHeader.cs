using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Pages.Career;
using YuumiX.App.Theming;
using YuumiX.Core;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match.Models;

namespace YuumiX.App.Pages.GameInfo;

/// <summary>
/// 战绩列列头布局（对应旧版 <c>PlayerMatchColumn.vue</c> 的 <c>.col-header</c> 两种形态）：
/// <list type="bullet">
///   <item>按队伍（5 列）：单行 —— 头像 + 名称 + 胜负数/段位/熟练度横向一行；</item>
///   <item>全部战绩（10 列）：竖排四段，整体居中 —— ①头像（含左侧胜率竖条），②召唤师名，
///         ③等级与段位，④几胜几负；
///         标识徽标（曾同局/历史）浮在卡片右上角。</item>
/// </list>
/// 抽出独立文件是为了让 <see cref="PlayerMatchColumnView"/> 只保留战绩列表装配；
/// 复用构件见 <see cref="PlayerColumnVisuals"/>。
/// </summary>
internal static class PlayerColumnHeader
{
    /// <summary>10 列头像尺寸（旧版紧凑模式 26px，此处取 28 保证 12px 文字旁留白）。</summary>
    private const double TenColumnAvatarSize = 28;

    /// <summary>胜率竖条宽度与高度（旧版 3px 宽、与头像同高、自底向上填充）。</summary>
    private const double WinRateBarWidth = 3;

    public static Element Render(
        PlayerMatchColumnProps props,
        AppServices services,
        YuumiPalette palette,
        bool isTenColumn)
    {
        return isTenColumn
            ? RenderTenColumn(props, services, palette)
            : RenderFiveColumn(props, services, palette);
    }

    // ─────────────────────────────────────────────────────────────
    // 全部战绩（10 列）：头像靠左，右侧名称/段位，头像下胜负，末行位置与标识
    // ─────────────────────────────────────────────────────────────

    private static Element RenderTenColumn(PlayerMatchColumnProps props, AppServices services, YuumiPalette palette)
    {
        var p = props.Player;
        var d = props.PlayerData;
        var premadeColor = props.PremadeIdx >= 0 ? PremadeColors.GetColor(props.PremadeIdx) : null;
        var savedInfo = FateBadgeVisuals.ResolveSavedInfo(d, p, props.SavedMap, props.SelfPuuid);

        int champId = p.ChampionId > 0 ? p.ChampionId : (d?.ChampionId ?? 0);
        var masteryDetail = ChampionMasteryDetail.Create(d?.Masteries, champId);

        // ── ①头像（含左侧胜率竖条）→ ②召唤师名 → ③等级与段位 → ④几胜几负，全部居中 ──
        var avatar = HStack(3,
            PlayerColumnVisuals.BuildWinRateBar(d?.WinRate, palette, TenColumnAvatarSize),
            PlayerColumnVisuals.BuildChampionAvatar(champId, p.ProfileIconId, services, masteryDetail, TenColumnAvatarSize, 6))
            .HAlign(HorizontalAlignment.Center);

        var nameRow = PlayerColumnVisuals.BuildNameRow(props, palette, showFateBadge: false, showSavedBadge: false);
        var rankRow = PlayerColumnVisuals.BuildRankRow(d, masteryDetail, withMastery: true);
        var recordRow = BuildRecordLine(d, palette).HAlign(HorizontalAlignment.Center);
        var positionsRow = PlayerColumnVisuals.BuildPositionsRow(d);

        // 名称/段位包一层单列 Star 的 Grid：VStack 里 StackPanel 会按无限宽度测量，
        // 长昵称不会触发 TextTrimming 而撑破列宽；Grid 的 Star 列能给出确定宽度。
        var content = VStack(2,
            avatar,
            Grid([GridSize.Star()], [GridSize.Auto], [nameRow.Grid(row: 0, column: 0).HAlign(HorizontalAlignment.Center)]),
            Grid([GridSize.Star()], [GridSize.Auto], [rankRow.Grid(row: 0, column: 0).HAlign(HorizontalAlignment.Center)]),
            recordRow,
            positionsRow)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Top);

        // 标识徽标（宿命/曾同局/路人集历史）浮在卡片右上角：与主体同处一个 Grid 单元格叠加，
        // 不再单独占一行，末行因此可整行显示位置信息。
        var body = Grid(
            [GridSize.Star()],
            [GridSize.Auto],
            [
                content.Grid(row: 0, column: 0),
                BuildBadgeGroup(d, savedInfo, palette)
                    .Grid(row: 0, column: 0)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Top),
            ]);

        // 不设固定高度：各列内容段数一致，且段位/胜负/位置缺失时都有占位文本，
        // 自适应高度既能让十列自然等高（战绩列表首行横向对齐），也不会在底部留出空行。
        var header = Border(body)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .BorderBrush(Theme.CardStroke)
            .Padding(5, 6);

        return premadeColor is null
            ? header.Background(Theme.SubtleFill)
            : header.Background(premadeColor.Bg);
    }

    /// <summary>头像正下方「N胜 M负」。缺失统计时降级为胜率或占位，悬停展示常玩位置与英雄分析。</summary>
    private static Element BuildRecordLine(GameInfoPlayer? d, YuumiPalette palette)
    {
        Element el;
        if (d?.WinCount.HasValue == true && d.LossesCount.HasValue)
        {
            el = HStack(3,
                Caption($"{d.WinCount}胜")
                    .Foreground(palette.WinText)
                    .VAlign(VerticalAlignment.Center),
                Caption($"{d.LossesCount}负")
                    .Foreground(palette.LoseText)
                    .VAlign(VerticalAlignment.Center));
        }
        else if (d?.WinRate.HasValue == true)
        {
            int wr = (int)Math.Round(d.WinRate.Value);
            el = Caption($"{wr}% 胜率")
                .Foreground(wr >= 50 ? palette.WinText : palette.LoseText);
        }
        else
        {
            el = Caption(d?.Loading == true ? "加载中" : " ").Foreground(Theme.DisabledText);
        }

        string posTip = PlayerColumnVisuals.BuildPosTooltip(d);
        if (!string.IsNullOrEmpty(posTip))
        {
            el = el.WithToolTip(TextBlock(posTip));
        }

        return el;
    }

    /// <summary>右上角标识徽标：宿命（友/敌/曾）+ 路人集历史标记；没有时返回空元素（浮层不占位）。</summary>
    private static Element BuildBadgeGroup(GameInfoPlayer? d, SavedPlayerMarker? savedInfo, YuumiPalette palette)
    {
        var items = new List<Element>();

        if (!string.IsNullOrEmpty(d?.FateFlag))
        {
            bool isAlly = d.FateFlag == "ally";
            items.Add(
                Border(Caption(FateBadgeVisuals.GetBadgeText(d)).Foreground(isAlly ? palette.WinText : palette.LoseText))
                    .Background(isAlly ? palette.WinBg : palette.LoseBg)
                    .CornerRadius(YuumiTheme.ControlRadius)
                    .Padding(3, 0.5)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(FateBadgeVisuals.BuildToolTip(d)));
        }

        if (savedInfo is not null && FateBadgeVisuals.ShouldShowSavedBadge(savedInfo, d))
        {
            items.Add(FateBadgeVisuals.BuildSavedBadge(savedInfo));
        }

        return items.Count > 0 ? HStack(3, items.ToArray()) : Empty();
    }

    // ─────────────────────────────────────────────────────────────
    // 按队伍（5 列）：单行头像 + 名称 + 胜负数/段位/熟练度
    // ─────────────────────────────────────────────────────────────

    private static Element RenderFiveColumn(PlayerMatchColumnProps props, AppServices services, YuumiPalette palette)
    {
        var p = props.Player;
        var d = props.PlayerData;
        var premadeColor = props.PremadeIdx >= 0 ? PremadeColors.GetColor(props.PremadeIdx) : null;
        var savedInfo = FateBadgeVisuals.ResolveSavedInfo(d, p, props.SavedMap, props.SelfPuuid);

        int champId = p.ChampionId > 0 ? p.ChampionId : (d?.ChampionId ?? 0);
        var masteryDetail = ChampionMasteryDetail.Create(d?.Masteries, champId);

        var avatarElement = PlayerColumnVisuals.BuildChampionAvatar(champId, p.ProfileIconId, services, masteryDetail, 28, 6);

        var nameRow = PlayerColumnVisuals.BuildNameRow(props, palette, showFateBadge: true, showSavedBadge: true, savedInfo: savedInfo);

        // 第 2 行：几胜几负 + 段位图标与短名 + 熟练度
        var summaryItems = new List<Element>();

        if (d?.WinCount.HasValue == true && d.LossesCount.HasValue)
        {
            summaryItems.Add(Caption($"{d.WinCount}胜").Foreground(palette.WinText));
            summaryItems.Add(Caption($"{d.LossesCount}负").Foreground(palette.LoseText).Margin(2, 0, 4, 0));
        }
        else if (d?.WinRate.HasValue == true)
        {
            int wr = (int)Math.Round(d.WinRate.Value);
            summaryItems.Add(
                Caption($"{wr}%")
                    .SemiBold()
                    .Foreground(wr >= 50 ? palette.WinText : palette.LoseText)
                    .Margin(0, 0, 4, 0));
        }

        var activeRank = d?.PrimaryRank;
        if (activeRank?.HasRank == true)
        {
            summaryItems.Add(
                HStack(2,
                    CareerVisuals.RankedMedal(activeRank.Tier, 12).VAlign(VerticalAlignment.Center),
                    Caption(PlayerColumnVisuals.ShortRank(activeRank.Tier, activeRank.Division))
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center))
                .Margin(0, 0, 4, 0)
                .WithToolTip(TextBlock(PlayerColumnVisuals.BuildRankDetailedTooltip(d, masteryDetail))));
        }

        if (masteryDetail is { Level: > 0 })
        {
            summaryItems.Add(PlayerColumnVisuals.BuildMasteryChip(masteryDetail));
        }

        var summaryRow = HStack(0, summaryItems.ToArray());

        var headerContent = Grid(
            [GridSize.Auto, GridSize.Star()],
            [GridSize.Auto],
            [
                avatarElement.Grid(row: 0, column: 0).Margin(0, 0, 4, 0).VAlign(VerticalAlignment.Center),
                VStack(2, nameRow, summaryRow)
                    .Grid(row: 0, column: 1)
                    .VAlign(VerticalAlignment.Center),
            ]);

        var headerBorder = Border(headerContent)
            .Height(48)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .BorderBrush(Theme.CardStroke)
            .Padding(6, 4);

        headerBorder = premadeColor is null
            ? headerBorder.Background(Theme.SubtleFill)
            : headerBorder.Background(premadeColor.Bg);

        string premadeTip = props.PremadeIdx >= 0 ? $"组队 {props.PremadeIdx + 1}" : string.Empty;
        return string.IsNullOrEmpty(premadeTip)
            ? headerBorder
            : headerBorder.WithToolTip(TextBlock(premadeTip));
    }
}
