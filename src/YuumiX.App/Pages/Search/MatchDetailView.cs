using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 对局详情面板，对应原 <c>MatchDetailPanel.vue</c>。
///
/// 结构对齐原版：头部（模式/地图/时间/时长/结果）→ 本人的核心数据（KDA、补刀、金币、伤害、装备、海克斯）
/// → 蓝红两方队伍块（队伍目标数 + 逐人数据）。
///
/// <b>数据来自 LCU 单局接口</b>（<c>/lol-match-history/v1/games/{gameId}</c>），10 名参与者齐全。
/// 列表接口每局只带本人 1 人，因此详情必须单独取一次——这也是面板要处理 <c>detail == null</c> 的原因：
/// 选中后到详情返回之间存在一个真实窗口，不能把上一局的名单继续挂在新选中项下面。
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
        }
        else
        {
            blocks.Add(Caption("正在读取该局详情；长时间无内容通常意味着对局已被客户端清理。"));
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
                // 按该队胜负取色：两队同色时，详情页会看不出谁赢了
                .Background(team.Win ? palette.WinCard : palette.LoseCard)
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
