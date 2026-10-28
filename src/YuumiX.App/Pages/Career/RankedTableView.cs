using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Career.Models;

namespace YuumiX.App.Pages.Career;

/// <summary>
/// 排位段位表，对应原 <c>MatchHistoryTab.vue</c> 的 <c>.rank-table</c>。
///
/// <para><b>列宽必须等分铺满，不能用固定宽度</b></para>
/// 旧版是 <c>&lt;table width:100%&gt;</c> + <c>table-layout:auto</c>，9 列在 1000 DIP 的正文列内
/// 自动均分（每列约 111）。此前实现写了 9 个固定宽度常量、合计 900，
/// 结果表格右端恒定空出约 100 DIP 且不随窗口宽度变化——这是「表格偏左、右侧留白」的直接原因。
/// 改为 <c>GridSize.Star()</c> ×9 后与旧版一致：等分、随正文列宽伸缩。
///
/// <para><b>行高</b></para>
/// 旧版 <c>.rank-table th/td</c>：<c>padding: 6px 16px</c> + 单元格字号 0.82rem（13.12），
/// 旧版截图实测表格卡片 305→407 共 102（三行 33 / 35 / 34）。这里用 <c>Body(14)</c>——语义层里最接近的字号档
/// （<c>Caption(12)</c> 会矮 5 DIP，13.12 无对应工厂）——并用 <see cref="RowMinHeight"/> 固定行高，
/// 保证行步进与旧版一致且不依赖字体行高的换算细节。
/// </summary>
internal static class RankedTableView
{
    /// <summary>旧版 9 列：类型 / 总场次 / 胜率 / 胜场 / 负场 / 段位 / 胜点 / 赛季最高 / 上赛季结算。</summary>
    private const int ColumnCount = 9;

    /// <summary>单元格内边距：旧版 <c>.rank-table th/td { padding: 6px 16px }</c>（6 属 1–7px 光学微调档）。</summary>
    private const double CellPadH = 16;
    private const double CellPadV = 6;

    /// <summary>
    /// 行步进 33：旧版截图实测三行 33 / 35 / 34（表头 305→338、数据行 338→373、373→407），
    /// 整表 102。本实现每行另带 1 DIP 分隔线与外框（+4），取 33 时整表 103，与旧版差 1。
    /// 取 34 会到 106，比旧版高 4。
    /// </summary>
    private const double RowMinHeight = 33;

    /// <summary>9 列等分：对应旧版 <c>table-layout:auto</c> 在固定宽度下的均分结果。</summary>
    private static readonly GridSize[] Columns = Enumerable.Repeat(GridSize.Star(), ColumnCount).ToArray();

    private static readonly GridSize[] Rows = [GridSize.Auto];

    private static Element At(Element content, int column) => content.Grid(row: 0, column: column);

    private static Element HeaderCell(string text) =>
        Body(text)
            .Foreground(Theme.SecondaryText)
            .TextAlignment(TextAlignment.Center)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Center)
            .Padding(CellPadH, CellPadV);

    private static Element StrongDataCell(string text) =>
        BodyStrong(text)
            .TextAlignment(TextAlignment.Center)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Center)
            .Padding(CellPadH, CellPadV);

    private static Element NumericDataCell(string text) =>
        BodyStrong(text)
            .TextAlignment(TextAlignment.Center)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Center)
            .Padding(CellPadH, CellPadV);

    private static Element NumericDataCell(string text, ThemeRef foreground) =>
        BodyStrong(text)
            .Foreground(foreground)
            .TextAlignment(TextAlignment.Center)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Center)
            .Padding(CellPadH, CellPadV);

    private static Element DataCell(string text, ThemeRef foreground) =>
        DataCell(text)
            .Foreground(foreground);

    private static Element DataCell(string text) =>
        Body(text)
            .TextAlignment(TextAlignment.Center)
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Center)
            .Padding(CellPadH, CellPadV);

    /// <summary>分隔线：1 DIP 的浅色横线。</summary>
    private static Element Divider() =>
        Border(Empty()).Height(1).Background(Theme.DividerStroke).HAlign(HorizontalAlignment.Stretch);

    public static Element Render(RankedStats stats, YuumiPalette palette)
    {
        var i18n = Composition.AppHost.CurrentI18n;

        Element header = Grid(Columns, Rows,
            [
                At(HeaderCell(i18n?.T("career.type", "类型") ?? "类型"), 0),
                At(HeaderCell(i18n?.T("career.totalGames", "总场次") ?? "总场次"), 1),
                At(HeaderCell(i18n?.T("career.winRate", "胜率") ?? "胜率"), 2),
                At(HeaderCell(i18n?.T("career.winsLabel", "胜场") ?? "胜场"), 3),
                At(HeaderCell(i18n?.T("career.lossesLabel", "负场") ?? "负场"), 4),
                At(HeaderCell(i18n?.T("career.tier", "段位") ?? "段位"), 5),
                At(HeaderCell(i18n?.T("career.lp", "胜点") ?? "胜点"), 6),
                At(HeaderCell(i18n?.T("career.highest", "赛季最高") ?? "赛季最高"), 7),
                At(HeaderCell(i18n?.T("career.prevSeason", "上赛季结算") ?? "上赛季结算"), 8),
            ])
            .MinHeight(RowMinHeight)
            .Background(Theme.SubtleFill);

        bool hasSolo = stats.SoloDuo is not null && (stats.SoloDuo.HasRank || stats.SoloDuo.TotalGames > 0);
        bool hasFlex = stats.Flex is not null && (stats.Flex.HasRank || stats.Flex.TotalGames > 0);

        if (!hasSolo && !hasFlex)
        {
            var unrankedText = i18n?.T("career.unrankedSeason", "本赛季未定级") ?? "本赛季未定级";
            return CareerVisuals.Card(
                VStack(0,
                    header,
                    Divider(),
                    Border(
                        Body(unrankedText)
                            .Foreground(Theme.SecondaryText)
                            .HAlign(HorizontalAlignment.Center)
                            .VAlign(VerticalAlignment.Center))
                        .MinHeight(RowMinHeight * 2 + 1)
                        .HAlign(HorizontalAlignment.Stretch)
                        .VAlign(VerticalAlignment.Center)),
                padding: 0);
        }

        return CareerVisuals.Card(
            VStack(0,
                header,
                Divider(),
                Row(i18n?.T("gameInfo.soloRank", "单双排位") ?? "单双排位", stats.SoloDuo, palette),
                Divider(),
                Row(i18n?.T("gameInfo.flexRank", "灵活排位") ?? "灵活排位", stats.Flex, palette)),
            // 旧版表格是满宽出血（wrapper 无内边距，留白全部来自单元格）
            padding: 0);
    }

    private static Element Row(string label, RankedQueueEntry? queue, YuumiPalette palette)
    {
        string games = (queue?.TotalGames ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string winRate = queue?.WinRateText ?? "--";
        string wins = (queue?.Wins ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string losses = (queue?.Losses ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string tier = queue?.TierText ?? "--";
        string leaguePoints = (queue?.LeaguePoints ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string highest = queue?.HighestTierText ?? "--";
        string previous = queue?.PreviousSeasonTierText ?? "--";

        Element winRateCell;
        if (queue is not null && queue.TotalGames > 0)
        {
            var winRateTheme = (queue.Wins * 2 >= queue.TotalGames)
                ? Theme.SystemSuccess
                : Theme.SystemCaution;
            winRateCell = NumericDataCell(winRate, winRateTheme);
        }
        else
        {
            winRateCell = NumericDataCell(winRate, Theme.SecondaryText);
        }

        return Grid(Columns, Rows,
            [
                At(StrongDataCell(label), 0),
                At(NumericDataCell(games), 1),
                At(winRateCell, 2),
                At(DataCell(wins), 3),
                At(DataCell(losses), 4),
                At(TierCell(queue, tier), 5),
                At(NumericDataCell(leaguePoints), 6),
                At(DataCell(highest, Theme.SecondaryText), 7),
                At(DataCell(previous, Theme.SecondaryText), 8),
            ])
            .MinHeight(RowMinHeight);
    }

    /// <summary>段位格：有徽标时图标 + 文字水平居中；无徽标时纯文字居中。</summary>
    private static Element TierCell(RankedQueueEntry? queue, string tier)
    {
        string? medalUri = CareerVisuals.RankedMedalUri(queue?.Tier);
        if (medalUri is null)
        {
            return StrongDataCell(tier).Foreground(Theme.Accent);
        }

        return HStack(6,
                CareerVisuals.RankedMedal(queue?.Tier, 20),
                BodyStrong(tier)
                    .Foreground(Theme.Accent)
                    .VAlign(VerticalAlignment.Center))
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center)
            .Padding(CellPadH, CellPadV);
    }
}
