using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 对局详情面板骨架，对应原 <c>MatchDetailPanel.vue</c>。
///
/// 结构对齐原版：头部（模式/地图/时间/时长/结果）→ 本人的核心数据（KDA、补刀、金币、伤害、装备、海克斯）
/// → 蓝红两方队伍块（队伍目标数 + 逐人数据）。
///
/// <b>当前数据是夹具，形态有限</b>：夹具来自 <c>matches</c> 列表接口，每局只有本人一名参与者，
/// 因此对手阵营为空。面板不为此造数据，而是显式说明——把空位伪装成"对手加载中"，
/// 接真实接口后反而分不清是没数据还是没渲染。
/// </summary>
internal static class MatchDetailView
{
    public static Element Render(
        MatchDetail? detail,
        MatchSummary? summary,
        GameDataCatalog catalog,
        YuumiPalette palette)
    {
        if (summary is null)
        {
            return Border(Caption("左侧选择一局以查看详情")).Padding(24)
                .CornerRadius(8)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke, 1);
        }

        var blocks = new List<Element> { Header(summary, detail, palette), SelfStats(summary, catalog) };

        if (detail is not null)
        {
            blocks.Add(HStack(12,
                TeamBlock("蓝方", detail.Blue, catalog, palette),
                TeamBlock("红方", detail.Red, catalog, palette)));
            blocks.Add(Caption("夹具来自 matches 列表接口，每局仅含本人参与者，因此对手阵营为空；接真实详情接口后会补满 5v5。"));
        }
        else
        {
            blocks.Add(Caption("未能解析该局详情（夹具缺失或载荷不完整）。"));
        }

        return ScrollView(
            VStack(14, blocks.ToArray()).Padding(20).HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch));
    }

    private static Element Header(MatchSummary summary, MatchDetail? detail, YuumiPalette palette)
    {
        string title = detail is null ? summary.QueueName : detail.QueueName;
        string map = detail is null ? summary.MapName : detail.MapName;

        return Border(VStack(6,
                HStack(10,
                    TextBlock(title).FontSize(18).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
                    TextBlock(SearchVisuals.ResultText(summary)).FontSize(13).Foreground(palette.Accent)),
                HStack(14,
                    SearchVisuals.Field("地图", map),
                    SearchVisuals.Field("时间", summary.TimeText),
                    SearchVisuals.Field("时长", summary.DurationText),
                    SearchVisuals.Field("模式 ID", summary.QueueId.ToString(System.Globalization.CultureInfo.InvariantCulture)))))
            .Padding(16)
            .CornerRadius(8)
            .Background(SearchVisuals.CardBackground(palette, summary))
            .WithBorder(palette.Accent, 1);
    }

    private static Element SelfStats(MatchSummary summary, GameDataCatalog catalog)
    {
        var items = new Element[SearchVisuals.ItemSlotCount];
        for (var slot = 0; slot < items.Length; slot++)
        {
            int itemId = slot < summary.ItemIds.Count ? summary.ItemIds[slot] : 0;
            items[slot] = SearchVisuals.ItemIcon(catalog, itemId, 26);
        }

        var augments = summary.AugmentIds
            .Select(id => SearchVisuals.AugmentIcon(catalog, id, 22))
            .ToArray();

        Element augmentRow = augments.Length == 0
            ? Caption("本局无海克斯强化")
            : HStack(4, augments);

        return Border(VStack(10,
                HStack(12,
                    SearchVisuals.Icon(SearchVisuals.ChampionIcon(summary.ChampionId), 56, 8),
                    VStack(4,
                        HStack(8,
                            TextBlock($"{summary.Kills} / {summary.Deaths} / {summary.Assists}").FontSize(16),
                            TextBlock($"KDA {summary.KdaText}").FontSize(12).Foreground(Theme.SecondaryText)),
                        HStack(14,
                            SearchVisuals.Field("补刀", summary.CreepScore.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            SearchVisuals.Field("金币", summary.Gold.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            SearchVisuals.Field("伤害", summary.DamageToChampions.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            SearchVisuals.Field("治疗", summary.TotalHeal.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                        HStack(10,
                            SearchVisuals.SpellIcon(catalog, summary.Spell1Id, 22),
                            SearchVisuals.SpellIcon(catalog, summary.Spell2Id, 22),
                            SearchVisuals.RuneIcon(catalog, summary.RuneId, 22),
                            TextBlock(SearchVisuals.PositionText(summary.Position) ?? "无分路").FontSize(11)
                                .Foreground(Theme.SecondaryText)))),
                HStack(3, items),
                augmentRow))
            .Padding(16)
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);
    }

    private static Element TeamBlock(
        string label,
        MatchDetailTeam team,
        GameDataCatalog catalog,
        YuumiPalette palette)
    {
        var rows = team.Players
            .Select(player => (Element)Border(HStack(8,
                    SearchVisuals.Icon(SearchVisuals.ChampionIcon(player.ChampionId), 24, 4),
                    VStack(2,
                        TextBlock(player.Name).FontSize(12),
                        TextBlock($"{player.Kills}/{player.Deaths}/{player.Assists} · 补刀 {player.CreepScore}").FontSize(11)
                            .Foreground(Theme.SecondaryText))))
                .Padding(6)
                .CornerRadius(4)
                .Background(palette.WinCard)
                .WithBorder(Theme.CardStroke, 1))
            .ToArray();

        Element content = rows.Length == 0
            ? Caption("无参与者数据")
            : VStack(6, rows);

        return Border(VStack(8,
                HStack(10,
                    TextBlock(label).FontSize(13).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
                    TextBlock($"击杀 {team.Kills}").FontSize(11).Foreground(Theme.SecondaryText),
                    TextBlock($"塔 {team.TowerKills} · 龙 {team.DragonKills} · 男爵 {team.BaronKills}").FontSize(11)
                        .Foreground(Theme.SecondaryText)),
                content))
            .Padding(14)
            .CornerRadius(8)
            .Width(300)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);
    }
}
