using System.Globalization;
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
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Match.Models;

namespace YuumiX.App.Pages.GameInfo;

public sealed record PlayerMatchColumnProps(
    PremadePlayerLike Player,
    GameInfoPlayer? PlayerData,
    int PremadeIdx,
    int ViewColumns,
    YuumiPalette Palette,
    string? SelfPuuid = null,
    string Side = "ally",
    string? MapSide = null,
    bool IsTeamSeparator = false,
    bool IsLast = false,
    IReadOnlyDictionary<string, SavedPlayerMarker>? SavedMap = null,
    Action<string, string?>? OnNavigateSearch = null,
    bool IsDemoData = false);

/// <summary>
/// 战绩列组件（对应旧版 <c>PlayerMatchColumn.vue</c>）。
/// 展示单个玩家的头部概要与近期前 10 局对局卡片。
/// </summary>
public sealed class PlayerMatchColumnView : Component<PlayerMatchColumnProps>
{
    /// <summary>KDA 比例最高档位色（旧版 <c>.kda-gold</c> 的 #f59e0b，非纯金黄。</summary>
    private const string KdaGold = AssetColors.Amber;

    /// <summary>
    /// 战绩卡之间的纵向间距。旧版 <c>.col-matches-list { gap: 6px; padding: 6px 6px }</c>：
    /// 10 张卡按 <c>calc((100% - 54px) / 10)</c> 均分（54 = 6 × 9），单卡最小 44 高。
    /// 此前取 2 是「更紧凑」的主观收紧，实际观感比旧版密一档，本次按旧版还原为 6。
    /// </summary>
    private const double MatchCardGap = 6;

    /// <summary>战绩列表四周内边距：旧版 <c>.col-matches-list { padding: 6px 6px }</c>。</summary>
    private const double MatchListPadding = 6;

    /// <summary>单卡最小高度：旧版 <c>.col-match { min-height: 44px }</c>。</summary>
    private const double MinMatchCardHeight = 44;

    public override Element Render()
    {
        var services = UseContext(AppHost.Context) ?? AppHost.Services;
        var d = Props.PlayerData;
        bool isTenCol = Props.ViewColumns == 10;
        var palette = Props.Palette;

        // 战绩列表可视高度：旧版 `.col-match` 用 `flex: 0 0 calc((100% - 54px) / 10)`
        // 把 10 张卡按列高均分（54 = gap 6 × 9），卡片因此铺满整列、底部不留空。
        // Reactor 的 StackPanel 做不到「按容器高反算子项高」，所以先量出列表高度，
        // 再按同一公式把卡片高度算出来：这是唯一能既均分又可滚动（空间不足时）的写法。
        var (listHeight, setListHeight) = UseState(0.0, threadSafe: true);

        // 阵营顶部色彩指示（根据 mapSide 蓝/红互换）
        string topBorderColor;
        if (Props.Side == "ally")
        {
            topBorderColor = Props.MapSide == "red" ? AssetColors.TeamRed : AssetColors.TeamBlue;
        }
        else
        {
            topBorderColor = Props.MapSide == "red" ? AssetColors.TeamBlue : AssetColors.TeamRed;
        }

        // 路人集标记：列头徽标与战绩卡「同局」标记共用（§4.8）
        var savedInfo = FateBadgeVisuals.ResolveSavedInfo(d, Props.Player, Props.SavedMap, Props.SelfPuuid);

        // 构造该玩家曾同局的 gameId 集合（encounteredGameIds 全集，空则 lastEncounteredGameId 单条）
        HashSet<long>? encounteredGameIds = null;
        if (savedInfo?.EncounteredGameIds is { Count: > 0 })
        {
            encounteredGameIds = new HashSet<long>(savedInfo.EncounteredGameIds);
        }
        else if (savedInfo?.LastEncounteredGameId is { } lastGid && lastGid > 0)
        {
            encounteredGameIds = [lastGid];
        }

        // 顶部概要头部（5 列单行 / 10 列三行，见 PlayerColumnHeader）
        var header = PlayerColumnHeader.Render(Props, services, palette, isTenCol);

        // 战绩列表（前 10 局）
        var matchesList = RenderMatchesList(d, isTenCol, palette, services, encounteredGameIds, listHeight, setListHeight);

        var topBar = Border(Empty())
            .Height(3)
            .Background(topBorderColor);

        // 列体用 Grid 而非 VStack：战绩列表要吃掉剩余高度（Star 行）才能内部滚动，
        // 否则 StackPanel 按内容高度排布、超出部分被直接裁掉（最后一场看不见）。
        var colContent = Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Auto, GridSize.Star()],
            [
                topBar.Grid(row: 0),
                header.Grid(row: 1),
                matchesList.Grid(row: 2),
            ]);

        int rightBorder = Props.IsTeamSeparator ? 2 : (Props.IsLast ? 0 : 1);
        var columnBorder = Border(colContent)
            .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
            .BorderThickness(new Thickness(0, 0, rightBorder, 0))
            .BorderBrush(Theme.CardStroke);

        return columnBorder;
    }

    private static Element RenderMatchesList(
        GameInfoPlayer? d,
        bool isTenCol,
        YuumiPalette palette,
        AppServices services,
        IReadOnlySet<long>? encounteredGameIds,
        double listHeight,
        Action<double> onListHeightChanged)
    {
        if (d?.Loading == true)
        {
            return VStack(0,
                Border(Caption("· · ·").Foreground(Theme.DisabledText).HAlign(HorizontalAlignment.Center))
                    .Margin(0, 32, 0, 0)
            );
        }

        if (d?.MatchHistoryHidden == true || d?.IsProfilePrivate == true)
        {
            return Border(Empty()).Padding(0, 24, 0, 0);
        }

        if (d?.Matches == null || d.Matches.Count == 0)
        {
            return VStack(4,
                Body("📭").HAlign(HorizontalAlignment.Center),
                Caption("暂无对局记录").Foreground(Theme.DisabledText).HAlign(HorizontalAlignment.Center)
            )
            .Padding(0, 24, 0, 0);
        }

        var matches = d.Matches.Take(10).ToArray();

        // 旧版卡片高度 =（列高 − 上下内边距 − 9 个 gap）/ 10，且不低于 44（`.col-match` 的 min-height）。
        // 首帧还没量到高度时先按最小高渲染，OnSizeChanged 回来后会重排一次。
        double cardHeight = ComputeCardHeight(listHeight, matches.Length);

        var cards = matches
            .Select(m => RenderMatchCard(m, isTenCol, palette, services, encounteredGameIds, cardHeight))
            .ToArray();

        // 间距与内边距对齐旧版 .col-matches-list（gap 6 / padding 6），
        // 再包一层 ScrollViewer 对齐旧版 .col-matches-list 的 overflow-y:auto：
        // 列高装不下 10 张（窄窗堆叠时）仍可滚动，而不是把最后一场压扁或裁掉。
        return ScrollViewer(VStack(MatchCardGap, cards).Padding(MatchListPadding))
            .HorizontalScrollMode(Microsoft.UI.Xaml.Controls.ScrollMode.Disabled)
            .OnSizeChanged((_, e) =>
            {
                if (Math.Abs(e.NewSize.Height - listHeight) >= 1)
                {
                    onListHeightChanged(e.NewSize.Height);
                }
            });
    }

    /// <summary>
    /// 按可视高度均分单卡高度（对齐旧版 <c>flex: 0 0 calc((100% - 54px) / 10)</c>）。
    /// 54 = gap 6 × 9。列表高度未知（首帧）或空间不足时退化为 <see cref="MinMatchCardHeight"/>。
    /// </summary>
    private static double ComputeCardHeight(double listHeight, int count)
    {
        if (listHeight <= 0 || count <= 0)
        {
            return MinMatchCardHeight;
        }

        double usable = listHeight - MatchListPadding * 2 - MatchCardGap * (count - 1);
        return Math.Max(MinMatchCardHeight, usable / count);
    }

    private static Element RenderMatchCard(
        MatchSummary m,
        bool isTenCol,
        YuumiPalette palette,
        AppServices services,
        IReadOnlySet<long>? encounteredGameIds,
        double cardHeight)
    {
        string cardBg = m.Remake
            ? palette.RemakeBg
            : (m.Win ? palette.WinBg : palette.LoseBg);

        string cardBorder = m.Remake
            ? palette.RemakeBorder
            : (m.Win ? palette.WinBorder : palette.LoseBorder);

        string championAsset = $"/lol-game-data/assets/v1/champion-icons/{m.ChampionId}.png";

        double iconSize = isTenCol ? 28 : 32;
        double radius = isTenCol ? 5 : 6;

        // 英雄头像 + 等级微标
        var champImage = Component<LcuImage, LcuImageProps>(new LcuImageProps(championAsset, Size: iconSize, CornerRadius: radius))
            .Provide(AppHost.Context, services);

        var levelBadge = Badge.Create(
                Caption($"{m.ChampionLevel}")
                    .SemiBold()
                    .Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush"))
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center),
                AssetColors.BadgeDark,
                padH: isTenCol ? 1.5 : 2,
                padV: 0)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Bottom)
            .Margin(0, 0, 0, 0);

        var champIconWithLevel = Grid(
            [GridSize.Auto],
            [GridSize.Auto],
            [champImage, levelBadge]
        );

        var inferredPos = m.Position ?? MatchStatsCalculator.InferMatchPosition(m);

        // 卡片顶部：模式名称 + 位置 + 「同局」标记 + 日期（带 ToolTip，§2.3.3）
        var topRowElements = new List<Element>
        {
            Caption(m.QueueName)
                .Foreground(Theme.SecondaryText)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .Grid(row: 0, column: 0)
        };

        var topRowColumns = new List<GridSize> { GridSize.Star() };
        int topCol = 1;

        if (inferredPos.HasValue)
        {
            topRowColumns.Add(GridSize.Auto);
            topRowElements.Add(
                PlayerColumnVisuals.BuildPositionIcon(inferredPos, 11).Margin(2, 0, 2, 0)
                    .Grid(row: 0, column: topCol));
            topCol++;
        }

        // 「同局」标记：该局 gameId 在路人集相遇记录中时显示（title「你曾参与本场对局」，§4.8）
        if (encounteredGameIds is not null && encounteredGameIds.Contains(m.GameId))
        {
            topRowColumns.Add(GridSize.Auto);
            topRowElements.Add(
                Border(Caption("同局")
                        .Foreground(Theme.Accent))
                    .Background(Theme.SubtleFill)
                    .WithBorder(Theme.CardStroke, 1)
                    .CornerRadius(YuumiTheme.ControlRadius)
                    .Padding(4, 1)
                    .Margin(3, 0, 3, 0)
                    .VAlign(VerticalAlignment.Center)
                    .WithToolTip(TextBlock("你曾参与本场对局"))
                    .Grid(row: 0, column: topCol));
            topCol++;
        }

        topRowColumns.Add(GridSize.Auto);
        topRowElements.Add(
            Caption(m.ShortDateText)
                .Foreground(Theme.DisabledText)
                .Grid(row: 0, column: topCol));

        var topRow = Grid(topRowColumns.ToArray(), [GridSize.Auto], topRowElements.ToArray())
            .WithToolTip(TextBlock(GetMatchTopTooltip(m)));

        // KDA 行（击杀/死亡/助攻，死亡红色加粗）
        var kdaItems = new List<Element>
        {
            Caption($"{m.Kills}").SemiBold().Foreground(Theme.PrimaryText),
            Caption(" / ").Foreground(Theme.DisabledText),
            Caption($"{m.Deaths}").SemiBold().Foreground(palette.DeathText),
            Caption(" / ").Foreground(Theme.DisabledText),
            Caption($"{m.Assists}").SemiBold().Foreground(Theme.PrimaryText),
        };

        var kdaLine = HStack(0, kdaItems.ToArray());

        Element statsBox;
        if (!isTenCol)
        {
            var (ratioText, ratioColor) = FormatKdaRatio(m, palette);
            var ratioLine = Caption($"{ratioText} KDA");
            statsBox = VStack(1,
                kdaLine,
                ratioColor is null
                    ? ratioLine.Foreground(Theme.SecondaryText)
                    : ratioLine.Foreground(ratioColor)
            );
        }
        else
        {
            statsBox = kdaLine;
        }

        var mainRow = Grid(
            [GridSize.Auto, GridSize.Star()],
            [GridSize.Auto],
            [
                champIconWithLevel.Grid(row: 0, column: 0).Margin(0, 0, 4, 0).VAlign(VerticalAlignment.Center),
                statsBox.Grid(row: 0, column: 1).VAlign(VerticalAlignment.Center),
            ]
        );

        // 旧版 `.col-match`：`flex-direction: column; justify-content: space-between`——
        // 顶部信息贴顶、KDA/头像行贴底，卡片被均分拉高时中间留白自然产生。
        // 用「Auto 行 + Star 行」表达同样的分配：主行 VAlign(Bottom) 即贴底。
        Element cardBody = Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Star()],
            [
                topRow.Grid(row: 0).VAlign(VerticalAlignment.Top),
                mainRow.Grid(row: 1).VAlign(VerticalAlignment.Bottom),
            ]);

        return Border(cardBody)
        .Background(cardBg)
        .WithBorder(cardBorder, 1)
        // 旧版 `.col-match { padding: 4px 7px; border-radius: 8px }`：横向 7 让头像与右边缘
        // 各留 7，10 列视图也沿用同一圆角（旧版没有按列数改圆角）。
        .CornerRadius(8)
        .Height(cardHeight)
        .Padding(7, 4);
    }

    private static (string text, string? color) FormatKdaRatio(MatchSummary m, YuumiPalette palette)
    {
        // 金色取旧版 PlayerMatchColumn.vue 的 #f59e0b（琥珀），纯金黄 #FFD700 在小字号下过亮
        if (m.Deaths == 0)
        {
            return ("Perf", KdaGold);
        }

        double ratio = (m.Kills + m.Assists) / (double)m.Deaths;
        string text = ratio >= 10
            ? ratio.ToString("0", CultureInfo.InvariantCulture)
            : ratio.ToString("0.0", CultureInfo.InvariantCulture);

        // color 为 null 表示走次要文本（ThemeRef 令牌），由调用方在元素级分支消费。
        string? color = ratio switch
        {
            >= 5.0 => KdaGold,
            >= 3.0 => CareerVisuals.IsDarkTheme ? AssetColors.BlueSoft : AssetColors.BlueEssence,
            >= 2.0 => palette.WinText,
            _ => null,
        };

        return (text, color);
    }

    private static string GetMatchTopTooltip(MatchSummary m)
    {
        var lines = new List<string> { $"游戏模式: {m.QueueName}" };
        var inferredPos = m.Position ?? MatchStatsCalculator.InferMatchPosition(m);
        string? pos = SearchVisuals.PositionText(inferredPos);
        if (!string.IsNullOrEmpty(pos))
        {
            lines.Add($"位置: {pos}");
        }
        string date = FateBadgeVisuals.FormatTimestamp(m.GameCreation);
        if (!string.IsNullOrEmpty(date))
        {
            lines.Add($"日期: {date}");
        }
        return string.Join("\n", lines);
    }

}
