using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Pages.Career;
using YuumiX.App.Theming;
using YuumiX.Core.Assets;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 详情表格中的玩家姓名链接：非本人行默认 TextPrimary，悬停变 Accent；本人行加粗且显示胜负文字色。
/// </summary>
internal sealed record PlayerNameLinkProps(
    string DisplayName,
    string FullName,
    string? Puuid,
    long SummonerId,
    int ChampionId,
    bool IsQueriedPlayer,
    bool TeamWin,
    YuumiPalette Palette,
    Action<string, SummonerProfile?>? OnClickPlayerPuuid,
    Action<long>? OnClickPlayerSummonerId);

internal sealed class PlayerNameLink : Component<PlayerNameLinkProps>
{
    public override Element Render()
    {
        var (hovered, setHovered) = UseState(false);
        var p = Props;
        bool hasPuuid = !string.IsNullOrEmpty(p.Puuid) && p.Puuid != "00000000-0000-0000-0000-000000000000";
        bool hasSummonerId = p.SummonerId > 0;
        bool canClick = hasPuuid || hasSummonerId;

        // 玩家名着色：查询者走胜负面板业务色；其余按可点击/悬停走语义令牌。
        // string 业务色与 ThemeRef 令牌无法共存于同一三元，因此按 CareerMatchRow 的既有做法在元素级分支。
        var text = Caption(p.DisplayName);
        if (p.IsQueriedPlayer)
        {
            text = text.Foreground(p.TeamWin ? p.Palette.WinText : p.Palette.LoseText);
        }
        else if (canClick && hovered)
        {
            text = text.Foreground(Theme.Accent);
        }
        else if (canClick)
        {
            text = text.Foreground(Theme.PrimaryText);
        }
        else
        {
            text = text.Foreground(Theme.SecondaryText);
        }

        if (hasPuuid)
        {
            void Trigger()
            {
                var placeholder = Career.CareerLoaders.PlaceholderSummoner(p.Puuid!, p.FullName, p.ChampionId);
                p.OnClickPlayerPuuid?.Invoke(p.Puuid!, placeholder);
            }

            return Border(text)
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .IsTabStop(true)
                .OnPointerEntered((_, _) => setHovered(true))
                .OnPointerExited((_, _) => setHovered(false))
                .OnTapped((_, _) => Trigger())
                .OnKeyDown((_, args) =>
                {
                    if (args.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
                    {
                        Trigger();
                        args.Handled = true;
                    }
                })
                .WithToolTip(TextBlock($"点击查看 {p.FullName} 的生涯资料"))
                .AutomationName($"查看 {p.DisplayName} 生涯");
        }

        if (hasSummonerId)
        {
            void Trigger() => p.OnClickPlayerSummonerId?.Invoke(p.SummonerId);

            return Border(text)
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .IsTabStop(true)
                .OnPointerEntered((_, _) => setHovered(true))
                .OnPointerExited((_, _) => setHovered(false))
                .OnTapped((_, _) => Trigger())
                .OnKeyDown((_, args) =>
                {
                    if (args.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
                    {
                        Trigger();
                        args.Handled = true;
                    }
                })
                .WithToolTip(TextBlock($"点击在战绩中搜索 {p.DisplayName}"))
                .AutomationName($"搜索 {p.DisplayName}");
        }

        return Caption(p.DisplayName)
            .Foreground(Theme.SecondaryText)
            .WithToolTip(TextBlock("机器人（不可查看战绩）"));
    }
}

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
        GridSize.Star(),                                    // 0: 选手信息
        isAugment ? GridSize.Px(310) : GridSize.Px(204),   // 1: 强化 / 装备
        GridSize.Px(84),                                    // 2: KDA（居中）
        GridSize.Px(60),                                    // 3: 补刀（居中）
        GridSize.Px(76),                                    // 4: 金币（右对齐）
        GridSize.Px(76),                                    // 5: 输出（右对齐）
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
            return EmptyPanel(palette);
        }

        var blocks = new List<Element> { Banner(summary, detail, palette, onCopyGameId) };

        if (detail is null)
        {
            // 详情加载中：直接铺两张表格骨架（对齐双方队伍），尺寸与终态 1:1 完全对齐，
            // 消除「小提示卡片 → 两张表」的高度跳变与闪现，数据到达时原地填充。
            bool isAugment = SearchVisuals.IsAugmentQueue(summary.QueueId);
            blocks.Add(SkeletonTable(isAugment));
            blocks.Add(SkeletonTable(isAugment));
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
                ScrollViewer(VStack(12, blocks.ToArray()).Padding(12))
                    .IsTabStop(true)
                    .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch)
                    .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Stretch))
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch)
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Stretch);
    }

    /// <summary>未选中任何对局时的占位：保持面板存在，避免整页在「有/无选中」之间跳动。</summary>
    private static Element EmptyPanel(YuumiPalette palette)
    {
        var i18n = Composition.AppHost.Services?.I18n;
        var text = i18n?.CurrentLanguage == YuumiX.Core.Localization.SupportedLanguages.EnUs
            ? "Select a match on the left to view details"
            : "在左侧选择一局以查看详情";

        return Border(Caption(text).Foreground(Theme.SecondaryText).HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
            .Padding(24)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch)
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Stretch);
    }

    /// <summary>骨架里的灰块：统一底色与圆角，尺寸由调用方给。</summary>
    private static Element Block(double width, double height, double radius = 4) =>
        Border(Empty())
            .Width(width)
            .Height(height)
            .CornerRadius(radius)
            .Background(Theme.SubtleFill);

    /// <summary>
    /// 详情表格骨架：与 <see cref="TeamTable"/> 同结构（表头 + 5 行 48 高选手行），
    /// 因此详情到达时面板高度不变——真实表格是「表头 + 5 行」，
    /// 用一句提示文案占位会在详情返回的瞬间把面板从 1 屏撑到 2 屏。
    /// </summary>
    private static Element SkeletonTable(bool isAugment)
    {
        GridSize[] columns = GetTableColumns(isAugment);

        Element header = Border(
                Grid(
                    columns,
                    [GridSize.Auto],
                    [
                        Block(140, 14).Grid(row: 0, column: 0),
                        Block(60, 12).Grid(row: 0, column: 1),
                        Block(30, 12).Grid(row: 0, column: 2),
                        Block(30, 12).Grid(row: 0, column: 3),
                        Block(30, 12).Grid(row: 0, column: 4),
                        Block(30, 12).Grid(row: 0, column: 5),
                    ]))
            .Padding(6);

        // 选手行数按标准局 5 人：详情到达后真实表格也是 5 行，行数一致才不会跳。
        var rows = new List<Element>(5);
        for (int i = 0; i < 5; i++)
        {
            rows.Add(Border(Empty())
                .Height(RowHeight)
                .Background(Theme.SubtleFill));
        }

        return Border(VStack(0, header, VStack(0, rows.ToArray())))
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch);
    }

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
        string accent = summary.Remake
            ? palette.RemakeText
            : (summary.Win ? palette.WinText : palette.LoseText);
        string bg = summary.Remake
            ? palette.RemakeBg
            : (summary.Win ? palette.WinBg : palette.LoseBg);
        string border = summary.Remake
            ? palette.RemakeBorder
            : (summary.Win ? palette.WinBorder : palette.LoseBorder);

        string map = detail?.MapName ?? summary.MapName;
        string queue = detail?.QueueName ?? summary.QueueName;

        string meta = string.Join(
            " · ",
            map,
            queue,
            summary.DurationText,
            summary.DetailDateText,
            $"游戏 ID: {summary.GameId.ToString(CultureInfo.InvariantCulture)}");

        Element info = HStack(12,
            Border(Empty()).Width(4).Height(34).CornerRadius(YuumiTheme.PillRadius).Background(accent),
            VStack(3,
                BodyLarge(SearchVisuals.ResultText(summary))
                    .SemiBold()
                    .Foreground(accent),
                Caption(meta).Foreground(Theme.SecondaryText)))
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);

        Element replayBtn = Component<ReplayButton, ReplayButtonProps>(
            new ReplayButtonProps(summary.GameId, Compact: false))
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);

        Element copyBtn = Button(
                Caption(Icons.Copy)
                    .FontFamily(Icons.FontFamily)
                    .Foreground(Theme.SecondaryText),
                () => onCopyGameId?.Invoke(summary.GameId))
            .Width(28)
            .Height(28)
            .Padding(0)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .AutomationName("复制游戏 ID")
            .WithToolTip(TextBlock($"复制游戏 ID: {summary.GameId}"));

        Element rightGroup = HStack(8, copyBtn, replayBtn)
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);

        Element bannerContent = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [info.Grid(row: 0, column: 0), rightGroup.Grid(row: 0, column: 1)]);

        return Border(bannerContent)
            .Padding(12)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(bg)
            .WithBorder(border, 1)
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
        string accent = team.Win ? palette.WinText : palette.LoseText;
        string tint = team.Win ? palette.WinBg : palette.LoseBg;
        string border = team.Win ? palette.WinBorder : palette.LoseBorder;

        var i18n = Composition.AppHost.Services?.I18n;
        var isEn = i18n?.CurrentLanguage == YuumiX.Core.Localization.SupportedLanguages.EnUs;

        var itemsHeader = isAugment
            ? (isEn ? "Augments / Items" : "强化 / 装备")
            : (isEn ? "Items" : "装备");
        var csHeader = isEn ? "CS" : "补刀";
        var goldHeader = isEn ? "Gold" : "金币";
        var dmgHeader = isEn ? "Damage" : "输出";

        Element header = Border(
                Grid(
                    columns,
                    [GridSize.Auto],
                    [
                        TeamSummary(team, accent, palette, isEn).Grid(row: 0, column: 0),
                        ColumnLabel(itemsHeader, 1, palette, HorizontalAlignment.Left),
                        ColumnLabel("KDA", 2, palette, HorizontalAlignment.Center),
                        ColumnLabel(csHeader, 3, palette, HorizontalAlignment.Center),
                        ColumnLabel(goldHeader, 4, palette, HorizontalAlignment.Right),
                        ColumnLabel(dmgHeader, 5, palette, HorizontalAlignment.Right),
                    ]))
            .Padding(6)
            .Background(tint)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch);

        var rows = new List<Element>(team.Players.Count * 2);
        for (var i = 0; i < team.Players.Count; i++)
        {
            if (i > 0)
            {
                rows.Add(SearchVisuals.Divider(Theme.DividerStroke));
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
            ? Border(Caption("无参与者数据").Foreground(Theme.SecondaryText)).Padding(12)
            : VStack(0, rows.ToArray());

        return Border(VStack(0, header, body))
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(border, 1)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch);
    }

    /// <summary>单项队伍资源（彩色小图标 + 数字 + tooltip）。</summary>
    private static Element ResourceItem(string iconName, int count, string tooltip, YuumiPalette palette)
    {
        string? iconUri = AppAssets.GetMatchDetailIconUri(iconName);
        Element icon = iconUri is not null
            ? Image(iconUri).Width(16).Height(16).AutomationName(tooltip).VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center)
            : Caption(tooltip).Foreground(Theme.SecondaryText).VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);

        return HStack(3,
                icon,
                Caption(count.ToString(CultureInfo.InvariantCulture))
                    .Foreground(Theme.SecondaryText)
                    .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center))
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center)
            .WithToolTip(TextBlock(tooltip));
    }

    /// <summary>表头左侧：胜方/败方 + 击杀数 + 六项团队目标彩色图标（对齐旧版 D7）。</summary>
    private static Element TeamSummary(MatchDetailTeam team, string accent, YuumiPalette palette, bool isEn = false)
    {
        int teamId = team.TeamId; // 100 or 200
        var winText = isEn ? (team.Win ? "Victory" : "Defeat") : (team.Win ? "胜方" : "败方");

        return HStack(8,
            BodyStrong(winText)
                .Foreground(accent)
                .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center),
            ResourceItem("kills.png", team.Kills, isEn ? "Kills" : "击杀", palette),
            ResourceItem($"tower-{teamId}.png", team.TowerKills, isEn ? "Towers" : "防御塔", palette),
            ResourceItem($"dragon-{teamId}.png", team.DragonKills, isEn ? "Dragons" : "巨龙", palette),
            ResourceItem($"baron-{teamId}.png", team.BaronKills, isEn ? "Baron" : "纳什男爵", palette),
            ResourceItem($"herald-{teamId}.png", team.RiftHeraldKills, isEn ? "Rift Herald" : "峡谷先锋", palette),
            ResourceItem($"inhibitor-{teamId}.png", team.InhibitorKills, isEn ? "Inhibitors" : "水晶", palette));
    }

    /// <summary>列名：按指定对齐方式对齐到各自数据列，保证表头与数字同一条基线。</summary>
    private static Element ColumnLabel(string text, int column, YuumiPalette palette, HorizontalAlignment alignment) =>
        Caption(text)
            .Foreground(Theme.SecondaryText)
            .HAlign(alignment)
            .TextAlignment(alignment switch
            {
                HorizontalAlignment.Center => TextAlignment.Center,
                HorizontalAlignment.Left => TextAlignment.Left,
                _ => TextAlignment.Right,
            })
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

        Element avatarCore = SearchVisuals.Icon(
            SearchVisuals.ChampionIcon(player.ChampionId), 36, 6);

        Element levelBadge = Border(
                Caption(player.ChampionLevel.ToString(CultureInfo.InvariantCulture))
                    .SemiBold()
                    .Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush"))
                    .TextAlignment(TextAlignment.Center)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center))
            .Width(15)
            .Height(15)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Background(AssetColors.BadgeDark)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Bottom);

        Element portrait = Grid([GridSize.Auto], [GridSize.Auto], [avatarCore, levelBadge])
            .Width(36)
            .Height(36);

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

        Element nameElement = Component<PlayerNameLink, PlayerNameLinkProps>(
            new PlayerNameLinkProps(
                DisplayName: displayName,
                FullName: player.Name,
                Puuid: player.Puuid,
                SummonerId: player.SummonerId,
                ChampionId: player.ChampionId,
                IsQueriedPlayer: isQueriedPlayer,
                TeamWin: team.Win,
                Palette: palette,
                OnClickPlayerPuuid: onClickPlayerPuuid,
                OnClickPlayerSummonerId: onClickPlayerSummonerId));

        var identityChildren = new List<Element>
        {
            nameElement,
        };

        if (player.IsPrivate)
        {
            identityChildren.Add(
                Caption("🔒")
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

        string queriedColor = team.Win ? palette.WinText : palette.LoseText;

        Element kdaCell = isQueriedPlayer
            ? SearchVisuals.Kda(player.Kills, player.Deaths, player.Assists, palette, strong: true, uniformColor: queriedColor)
            : SearchVisuals.Kda(player.Kills, player.Deaths, player.Assists, palette, strong: false, uniformColor: null);

        Element csCell = isQueriedPlayer
            ? SearchVisuals.NumberCell(
                player.CreepScore.ToString(CultureInfo.InvariantCulture),
                60,
                strong: true,
                foreground: queriedColor,
                alignment: TextAlignment.Center)
            : SearchVisuals.NumberCell(
                player.CreepScore.ToString(CultureInfo.InvariantCulture),
                60,
                strong: false,
                foreground: Theme.SecondaryText,
                alignment: TextAlignment.Center);

        Element goldCell = isQueriedPlayer
            ? SearchVisuals.NumberCell(
                SearchVisuals.Thousands(player.Gold),
                76,
                strong: true,
                foreground: queriedColor,
                alignment: TextAlignment.Right)
            : SearchVisuals.NumberCell(
                SearchVisuals.Thousands(player.Gold),
                76,
                strong: false,
                foreground: Theme.SecondaryText,
                alignment: TextAlignment.Right);

        Element damageCell = isQueriedPlayer
            ? SearchVisuals.NumberCell(
                SearchVisuals.Thousands(player.DamageToChampions),
                76,
                strong: true,
                foreground: queriedColor,
                alignment: TextAlignment.Right)
            : SearchVisuals.NumberCell(
                SearchVisuals.Thousands(player.DamageToChampions),
                76,
                strong: true,
                foreground: Theme.PrimaryText,
                alignment: TextAlignment.Right);

        var rowBorder = Border(
                Grid(
                    columns,
                    [GridSize.Auto],
                    [
                        playerCell.Grid(row: 0, column: 0),
                        itemsCell.Grid(row: 0, column: 1),
                        kdaCell
                            .HAlign(HorizontalAlignment.Center)
                            .Grid(row: 0, column: 2),
                        csCell.Grid(row: 0, column: 3),
                        goldCell.Grid(row: 0, column: 4),
                        damageCell.Grid(row: 0, column: 5),
                    ]))
            .Height(RowHeight)
            .Padding(6)
            .CornerRadius(isQueriedPlayer ? 4 : 0)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch);

        rowBorder = isQueriedPlayer
            ? rowBorder.Background(team.Win ? palette.WinBg : palette.LoseBg)
                       .WithBorder(team.Win ? palette.WinBorder : palette.LoseBorder, 1)
            : rowBorder.Background(Theme.CardBackground);

        return rowBorder;
    }
}
