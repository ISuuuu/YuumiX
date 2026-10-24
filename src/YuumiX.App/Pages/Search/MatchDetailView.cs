using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Pages.Career;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 对局详情面板，对应原 <c>MatchDetailPanel.vue</c>。
///
/// 结构对齐原版：结果横幅（胜负 / 地图 / 队列 / 时长 / 时间 / 对局 ID + 复制 ID + 回放）
/// → 双方各一张表（队伍目标彩色图标 + 逐人符文/强化/装备/ KDA / 补刀 / 金币 / 输出）。
///
/// <b>数据来自 LCU 单局接口</b>（<c>/lol-match-history/v1/games/{gameId}</c>），10 名参与者齐全。
/// 列表接口每局只带本人 1 人，因此详情必须单独取一次——这也是面板要处理 <c>detail == null</c> 的原因：
/// 选中后到详情返回之间存在一个真实窗口，不能把上一局的名单继续挂在新选中项下面。
/// </summary>
internal static class MatchDetailView
{
    /// <summary>选手行高度（含上下内边距）：48 − 6×2 = 36，正好容纳 36 DIP 的英雄头像。</summary>
    private const double RowHeight = 48;

    private static GridSize[] GetTableColumns(bool isAugment) =>
    [
        GridSize.Star(),
        isAugment ? GridSize.Px(320) : GridSize.Px(206),
        GridSize.Px(58),
        GridSize.Px(56),
        GridSize.Px(76),
        GridSize.Px(76),
    ];

    public static Element Render(
        MatchDetail? detail,
        MatchSummary? summary,
        GameDataCatalog catalog,
        YuumiPalette palette,
        string? currentSearchPuuid = null,
        Action<long>? onCopyGameId = null,
        Action<string, SummonerProfile?>? onClickPlayerPuuid = null,
        Action<long>? onClickPlayerSummonerId = null)
    {
        if (summary is null)
        {
            return EmptyPanel();
        }

        var blocks = new List<Element> { Banner(summary, detail, palette, onCopyGameId) };

        if (detail is null)
        {
            blocks.Add(PendingHint());
        }
        else
        {
            int queueId = detail.QueueId != 0 ? detail.QueueId : summary.QueueId;
            bool isAugment = SearchVisuals.IsAugmentQueue(queueId);
            GridSize[] columns = GetTableColumns(isAugment);

            // 胜方永远在上：两张表的配色与位置一致，扫一眼就能分辨哪边赢了
            foreach (MatchDetailTeam team in new[] { detail.Blue, detail.Red }.OrderByDescending(t => t.Win))
            {
                blocks.Add(TeamTable(
                    team,
                    catalog,
                    palette,
                    columns,
                    queueId,
                    isAugment,
                    currentSearchPuuid,
                    onClickPlayerPuuid,
                    onClickPlayerSummonerId));
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
    /// 右侧集成「复制 ID」与「回放」按钮。
    /// </summary>
    private static Element Banner(
        MatchSummary summary,
        MatchDetail? detail,
        YuumiPalette palette,
        Action<long>? onCopyGameId)
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

        Element copyBtn = Button("复制 ID", () => onCopyGameId?.Invoke(summary.GameId))
            .Height(30)
            .Padding(8, 0)
            .FontSize(11)
            .AutomationName("复制游戏 ID")
            .WithToolTip(TextBlock($"复制游戏 ID: {summary.GameId} 到剪贴板"));

        Element replayBtn = Component<ReplayButton, ReplayButtonProps>(
            new ReplayButtonProps(summary.GameId, Compact: false))
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);

        Element rightGroup = HStack(8, copyBtn, replayBtn)
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);

        Element bannerContent = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [info.Grid(row: 0, column: 0), rightGroup.Grid(row: 0, column: 1)]);

        return Border(bannerContent)
            .Padding(12)
            .CornerRadius(8)
            .Background(SearchVisuals.CardBackground(palette, summary))
            .WithBorder(SearchVisuals.ResultBorder(palette, summary), 1)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch);
    }

    /// <summary>一支队伍的表：表头（胜负 + 目标彩色图标 + 列名）→ 逐人数据行。</summary>
    private static Element TeamTable(
        MatchDetailTeam team,
        GameDataCatalog catalog,
        YuumiPalette palette,
        GridSize[] columns,
        int queueId,
        bool isAugment,
        string? currentSearchPuuid,
        Action<string, SummonerProfile?>? onClickPlayerPuuid,
        Action<long>? onClickPlayerSummonerId)
    {
        string accent = team.Win ? palette.WinAccent : palette.LoseAccent;
        string tint = team.Win ? palette.WinCard : palette.LoseCard;

        Element header = Border(
                Grid(
                    columns,
                    [GridSize.Auto],
                    [
                        TeamSummary(team, accent).Grid(row: 0, column: 0),
                        ColumnLabel(isAugment ? "强化 / 装备" : "装备", 1),
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

            rows.Add(PlayerRow(
                team.Players[i],
                team,
                catalog,
                palette,
                columns,
                queueId,
                isAugment,
                currentSearchPuuid,
                onClickPlayerPuuid,
                onClickPlayerSummonerId));
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

    /// <summary>单项队伍资源（彩色小图标 + 数字 + tooltip）。</summary>
    private static Element ResourceItem(string iconName, int count, string tooltip)
    {
        string? iconUri = AppAssets.GetMatchDetailIconUri(iconName);
        Element icon = iconUri is not null
            ? Image(iconUri).Width(16).Height(16).AutomationName(tooltip).VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center)
            : TextBlock(tooltip).FontSize(10).Foreground(Theme.SecondaryText).VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);

        return HStack(3,
                icon,
                TextBlock(count.ToString(CultureInfo.InvariantCulture))
                    .FontSize(11)
                    .Foreground(Theme.SecondaryText)
                    .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center))
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center)
            .WithToolTip(TextBlock(tooltip));
    }

    /// <summary>表头左侧：胜方/败方 + 击杀数 + 六项团队目标彩色图标（对齐旧版 D7）。</summary>
    private static Element TeamSummary(MatchDetailTeam team, string accent)
    {
        int teamId = team.TeamId; // 100 or 200

        return HStack(8,
            TextBlock(team.Win ? "胜方" : "败方")
                .FontSize(14)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .Foreground(accent)
                .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center),
            ResourceItem("kills.png", team.Kills, "击杀"),
            ResourceItem($"tower-{teamId}.png", team.TowerKills, "防御塔"),
            ResourceItem($"dragon-{teamId}.png", team.DragonKills, "巨龙"),
            ResourceItem($"baron-{teamId}.png", team.BaronKills, "纳什男爵"),
            ResourceItem($"herald-{teamId}.png", team.RiftHeraldKills, "峡谷先锋"),
            ResourceItem($"inhibitor-{teamId}.png", team.InhibitorKills, "水晶"));
    }

    /// <summary>列名：右对齐到各自数据列，保证表头与数字同一条基线。</summary>
    private static Element ColumnLabel(string text, int column) =>
        TextBlock(text)
            .FontSize(11)
            .Foreground(Theme.SecondaryText)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Right)
            .Grid(row: 0, column: column);

    private static Element PlayerRow(
        MatchDetailPlayer player,
        MatchDetailTeam team,
        GameDataCatalog catalog,
        YuumiPalette palette,
        GridSize[] columns,
        int queueId,
        bool isAugment,
        string? currentSearchPuuid,
        Action<string, SummonerProfile?>? onClickPlayerPuuid,
        Action<long>? onClickPlayerSummonerId)
    {
        bool isQueriedPlayer = !string.IsNullOrEmpty(currentSearchPuuid)
            && string.Equals(player.Puuid, currentSearchPuuid, StringComparison.Ordinal);

        Element portrait = SearchVisuals.Icon(
            SearchVisuals.ChampionIcon(player.ChampionId), 36, 6);

        // 召唤师技能竖排两格
        Element spells = VStack(1,
            SearchVisuals.SpellIcon(catalog, player.Spell1Id, 16),
            SearchVisuals.SpellIcon(catalog, player.Spell2Id, 16));

        // 基石符文：仅非强化队列显示，且 RuneId > 0 时渲染
        Element? rune = (!isAugment && player.RuneId > 0)
            ? SearchVisuals.RuneIcon(catalog, player.RuneId, 20).VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center)
            : null;

        Element spellsAndRune = rune is not null
            ? HStack(3, spells, rune).VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center)
            : spells;

        string displayName = player.Name.Contains('#', StringComparison.Ordinal)
            ? player.Name.Split('#')[0]
            : player.Name;

        bool hasPuuid = !string.IsNullOrEmpty(player.Puuid) && player.Puuid != "00000000-0000-0000-0000-000000000000";
        bool hasSummonerId = player.SummonerId > 0;

        Element nameText = TextBlock(displayName)
            .FontSize(12)
            .FontWeight(isQueriedPlayer ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.SemiBold);

        nameText = isQueriedPlayer
            ? nameText.Foreground(team.Win ? palette.WinAccent : palette.LoseAccent)
            : (hasPuuid || hasSummonerId ? nameText.Foreground(palette.Accent) : nameText.Foreground(Theme.PrimaryText));

        Element nameElement;
        if (hasPuuid)
        {
            nameElement = Button(
                    nameText,
                    () =>
                    {
                        var placeholder = CareerPage.PlaceholderSummoner(player.Puuid, player.Name, player.ChampionId);
                        onClickPlayerPuuid?.Invoke(player.Puuid, placeholder);
                    })
                .Padding(0)
                .Height(24)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardBackground, 0)
                .WithToolTip(TextBlock($"点击查看 {player.Name} 的生涯资料"))
                .AutomationName($"查看 {displayName} 生涯");
        }
        else if (hasSummonerId)
        {
            nameElement = Button(
                    nameText,
                    () => onClickPlayerSummonerId?.Invoke(player.SummonerId))
                .Padding(0)
                .Height(24)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardBackground, 0)
                .WithToolTip(TextBlock($"点击在战绩中搜索 {displayName}"))
                .AutomationName($"搜索 {displayName}");
        }
        else
        {
            nameElement = TextBlock(displayName)
                .FontSize(12)
                .Foreground(Theme.SecondaryText)
                .WithToolTip(TextBlock("机器人（不可查看战绩）"));
        }

        var identityChildren = new List<Element>
        {
            nameElement,
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

        Element playerCell = HStack(8, portrait, spellsAndRune, identity);

        // 装备与强化
        Element itemsCell;
        if (isAugment && player.AugmentIds.Count > 0)
        {
            var augElements = player.AugmentIds
                .Distinct()
                .Take(5)
                .Select(id => SearchVisuals.AugmentIcon(catalog, id, 22))
                .ToArray();

            itemsCell = HStack(6,
                    HStack(2, augElements).VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center),
                    SearchVisuals.ItemRow(catalog, player.ItemIds, SearchVisuals.DetailItemSize))
                .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);
        }
        else
        {
            itemsCell = SearchVisuals.ItemRow(catalog, player.ItemIds, SearchVisuals.DetailItemSize);
        }

        var rowBorder = Border(
                Grid(
                    columns,
                    [GridSize.Auto],
                    [
                        playerCell.Grid(row: 0, column: 0),
                        itemsCell.Grid(row: 0, column: 1),
                        SearchVisuals.Kda(player.Kills, player.Deaths, player.Assists, 12, palette)
                            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Right)
                            .Grid(row: 0, column: 2),
                        SearchVisuals.NumberCell(
                                player.CreepScore.ToString(CultureInfo.InvariantCulture), 56, strong: isQueriedPlayer)
                            .Grid(row: 0, column: 3),
                        SearchVisuals.NumberCell(SearchVisuals.Thousands(player.Gold), 76, strong: isQueriedPlayer)
                            .Grid(row: 0, column: 4),
                        SearchVisuals.NumberCell(
                                SearchVisuals.Thousands(player.DamageToChampions), 76, strong: true)
                            .Grid(row: 0, column: 5),
                    ]))
            .Height(RowHeight)
            .Padding(6)
            .CornerRadius(isQueriedPlayer ? 4 : 0)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch);

        rowBorder = isQueriedPlayer
            ? rowBorder.Background(SearchVisuals.WithAlpha(team.Win ? palette.WinCard : palette.LoseCard, "4D"))
            : rowBorder.Background(Theme.CardBackground);

        return isQueriedPlayer
            ? rowBorder.WithBorder(team.Win ? palette.WinAccent : palette.LoseAccent, 1)
            : rowBorder;
    }
}
