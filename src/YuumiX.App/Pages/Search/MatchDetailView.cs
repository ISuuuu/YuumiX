using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Pages.Career;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 对局详情面板，对应原 <c>MatchDetailPanel.vue</c>。
///
/// 结构对齐原版：结果横幅（胜负 / 地图 / 队列 / 时长 / 时间 / 对局 ID）
/// → 双方各一张表（队伍目标 + 逐人装备/ KDA / 补刀 / 金币 / 输出）。
///
/// <b>数据来自 LCU 单局接口</b>（<c>/lol-match-history/v1/games/{gameId}</c>），10 名参与者齐全。
/// 列表接口每局只带本人 1 人，因此详情必须单独取一次——这也是面板要处理 <c>detail == null</c> 的原因：
/// 选中后到详情返回之间存在一个真实窗口，不能把上一局的名单继续挂在新选中项下面。
/// </summary>
internal static class MatchDetailView
{
    /// <summary>表格列宽。玩家信息列吃剩余空间，其余定宽以保证多行之间末位对齐。</summary>
    private static readonly GridSize[] TableColumns =
    [
        GridSize.Star(),
        GridSize.Px(206),
        GridSize.Px(58),
        GridSize.Px(56),
        GridSize.Px(76),
        GridSize.Px(76),
    ];

    /// <summary>选手行高度（含上下内边距）：48 − 6×2 = 36，正好容纳 36 DIP 的英雄头像。</summary>
    private const double RowHeight = 48;

    public static Element Render(
        MatchDetail? detail,
        MatchSummary? summary,
        GameDataCatalog catalog,
        YuumiPalette palette)
    {
        if (summary is null)
        {
            return EmptyPanel();
        }

        var blocks = new List<Element> { Banner(summary, detail, palette) };

        if (detail is null)
        {
            blocks.Add(PendingHint());
        }
        else
        {
            // 胜方永远在上：两张表的配色与位置一致，扫一眼就能分辨哪边赢了
            foreach (MatchDetailTeam team in new[] { detail.Blue, detail.Red }.OrderByDescending(t => t.Win))
            {
                blocks.Add(TeamTable(team, catalog, palette));
            }
        }

        return Border(
                ScrollView(VStack(10, blocks.ToArray()).Padding(12))
                    .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch))
            .CornerRadius(10)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch);
    }

    /// <summary>未选中任何对局时的占位：保持面板存在，避免整页在「有/无选中」之间跳动。</summary>
    private static Element EmptyPanel() =>
        Border(Caption("在左侧选择一局以查看详情").FontSize(12))
            .Padding(24)
            .CornerRadius(10)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch);

    /// <summary>详情请求进行中的占位。</summary>
    private static Element PendingHint() =>
        Border(Caption("正在读取该局详情…").FontSize(12)).Padding(16).CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch);

    /// <summary>
    /// 结果横幅。日期用 <c>yyyy/MM/dd HH:mm</c>（与客户端战绩详情一致），
    /// 而不是列表里的 <c>MM-dd</c>——详情里需要能区分跨年的对局。
    /// </summary>
    private static Element Banner(MatchSummary summary, MatchDetail? detail, YuumiPalette palette)
    {
        string accent = SearchVisuals.ResultAccent(palette, summary);
        string map = detail?.MapName ?? summary.MapName;
        string queue = detail?.QueueName ?? summary.QueueName;

        string meta = string.Join(
            " · ",
            map,
            queue,
            summary.DurationText,
            summary.DetailDateText,
            $"游戏 ID: {summary.GameId.ToString(CultureInfo.InvariantCulture)}");

        Element info = HStack(10,
            Border(Empty()).Width(4).Height(34).CornerRadius(2).Background(accent),
            VStack(3,
                TextBlock(SearchVisuals.ResultText(summary))
                    .FontSize(16)
                    .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                    .Foreground(accent),
                TextBlock(meta).FontSize(11).Foreground(Theme.SecondaryText)))
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);

        Element replayBtn = Component<ReplayButton, ReplayButtonProps>(
            new ReplayButtonProps(summary.GameId, Compact: false))
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);

        Element bannerContent = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [info.Grid(row: 0, column: 0), replayBtn.Grid(row: 0, column: 1)]);

        return Border(bannerContent)
            .Padding(12)
            .CornerRadius(8)
            .Background(SearchVisuals.CardBackground(palette, summary))
            .WithBorder(SearchVisuals.ResultBorder(palette, summary), 1)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch);
    }

    /// <summary>一支队伍的表：表头（胜负 + 目标数 + 列名）→ 逐人数据行。</summary>
    private static Element TeamTable(
        MatchDetailTeam team,
        GameDataCatalog catalog,
        YuumiPalette palette)
    {
        string accent = team.Win ? palette.WinAccent : palette.LoseAccent;
        string tint = team.Win ? palette.WinCard : palette.LoseCard;

        Element header = Border(
                Grid(
                    TableColumns,
                    [GridSize.Auto],
                    [
                        TeamSummary(team, accent).Grid(row: 0, column: 0),
                        ColumnLabel("装备", 1),
                        ColumnLabel("KDA", 2),
                        ColumnLabel("补刀", 3),
                        ColumnLabel("金币", 4),
                        ColumnLabel("输出", 5),
                    ]))
            .Padding(6)
            .Background(tint)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch);

        var rows = new List<Element>(team.Players.Count * 2);
        for (var i = 0; i < team.Players.Count; i++)
        {
            if (i > 0)
            {
                rows.Add(SearchVisuals.Divider(tint));
            }

            rows.Add(PlayerRow(team.Players[i], catalog, palette));
        }

        Element body = team.Players.Count == 0
            ? Border(Caption("无参与者数据").FontSize(12)).Padding(12)
            : VStack(0, rows.ToArray());

        return Border(VStack(0, header, body))
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(SearchVisuals.WithAlpha(tint, "66"), 1)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch);
    }

    /// <summary>表头左侧：胜方/败方 + 击杀数 + 五项团队目标。</summary>
    private static Element TeamSummary(MatchDetailTeam team, string accent) =>
        HStack(8,
            TextBlock(team.Win ? "胜方" : "败方")
                .FontSize(14)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .Foreground(accent),
            TextBlock($"击杀 {team.Kills.ToString(CultureInfo.InvariantCulture)}")
                .FontSize(11)
                .Foreground(Theme.SecondaryText),
            TextBlock(string.Join(
                    " · ",
                    $"塔 {team.TowerKills}",
                    $"龙 {team.DragonKills}",
                    $"男爵 {team.BaronKills}",
                    $"先锋 {team.RiftHeraldKills}",
                    $"水晶 {team.InhibitorKills}"))
                .FontSize(11)
                .Foreground(Theme.SecondaryText));

    /// <summary>列名：右对齐到各自数据列，保证表头与数字同一条基线。</summary>
    private static Element ColumnLabel(string text, int column) =>
        TextBlock(text)
            .FontSize(11)
            .Foreground(Theme.SecondaryText)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Right)
            .Grid(row: 0, column: column);

    private static Element PlayerRow(
        MatchDetailPlayer player,
        GameDataCatalog catalog,
        YuumiPalette palette)
    {
        Element portrait = SearchVisuals.Icon(
            SearchVisuals.ChampionIcon(player.ChampionId), 36, 6);

        // 召唤师技能竖排两格，与原版一致：横排会挤占名字的宽度
        Element spells = VStack(1,
            SearchVisuals.SpellIcon(catalog, player.Spell1Id, 16),
            SearchVisuals.SpellIcon(catalog, player.Spell2Id, 16));

        var identityChildren = new List<Element>
        {
            TextBlock(player.Name).FontSize(12),
            TextBlock($"Lv{player.ChampionLevel.ToString(CultureInfo.InvariantCulture)}")
                .FontSize(10)
                .Foreground(Theme.SecondaryText),
        };

        if (player.IsPrivate)
        {
            identityChildren.Add(
                TextBlock("🔒")
                    .FontSize(10)
                    .Foreground(CareerVisuals.GoldColor)
                    .WithToolTip(TextBlock("战绩已隐藏")));
        }

        if (!string.IsNullOrEmpty(player.Tier))
        {
            identityChildren.Add(
                CareerVisuals.RankedMedal(player.Tier, 16)
                    .WithToolTip(TextBlock(player.RankText ?? player.Tier)));
        }

        Element identity = HStack(6, identityChildren.ToArray())
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);

        Element playerCell = HStack(8, portrait, spells, identity);

        return Grid(
                TableColumns,
                [GridSize.Auto],
                [
                    playerCell.Grid(row: 0, column: 0),
                    SearchVisuals.ItemRow(catalog, player.ItemIds, SearchVisuals.DetailItemSize)
                        .Grid(row: 0, column: 1),
                    SearchVisuals.Kda(player.Kills, player.Deaths, player.Assists, 12, palette)
                        .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Right)
                        .Grid(row: 0, column: 2),
                    SearchVisuals.NumberCell(
                            player.CreepScore.ToString(CultureInfo.InvariantCulture), 56)
                        .Grid(row: 0, column: 3),
                    SearchVisuals.NumberCell(SearchVisuals.Thousands(player.Gold), 76)
                        .Grid(row: 0, column: 4),
                    SearchVisuals.NumberCell(
                            SearchVisuals.Thousands(player.DamageToChampions), 76, strong: true)
                        .Grid(row: 0, column: 5),
                ])
            .Height(RowHeight)
            .Padding(6)
            .Background(Theme.CardBackground)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch);
    }
}
