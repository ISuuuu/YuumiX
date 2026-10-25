using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Assets;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 战绩页的纯展示辅助：把契约模型里的 ID 变成可渲染的元素。
///
/// 页面里不再出现"拼接 icon 路径"或"判断胜负该用哪个颜色"的散装代码——
/// 这类规则一旦分散到各组件，改主题色或改图标约定就要满页找。
///
/// 与改造前的差别：所有宽度/字号/颜色都对齐原 Vue 实现的战绩页（左列表 + 右详情表格），
/// 因此这里补了「实色胜负色」「带色死亡数 KDA」「六格装备 + 守卫」三类高频组合。
/// </summary>
internal static class SearchVisuals
{
    /// <summary>装备格数（含守卫格）。</summary>
    public const int ItemSlotCount = 7;

    /// <summary>详情表格里装备图标的边长。</summary>
    public const double DetailItemSize = 26;

    /// <summary>英雄图标路径。LCU 的约定路径，与 <c>GameDataCatalog</c> 的 champions 表无关。</summary>
    public static string ChampionIcon(int championId) =>
        $"{AssetPathMapper.LcuGameDataPrefix}v1/champion-icons/{championId}.png";

    /// <summary>
    /// 渲染图标；路径解析不到时给占位方块。
    /// 占位是刻意的：客户端未启动时映射表为空，一片空白会让人以为布局坏了。
    /// </summary>
    public static Element Icon(string? assetPath, double size, double cornerRadius = 4) =>
        string.IsNullOrEmpty(assetPath)
            ? Border(Empty())
                .Width(size)
                .Height(size)
                .CornerRadius(cornerRadius)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke, 1)
            : Component<LcuImage, LcuImageProps>(new LcuImageProps(assetPath, size, cornerRadius));

    /// <summary>装备图标（ID → 映射表路径）。</summary>
    public static Element ItemIcon(GameDataCatalog catalog, int itemId, double size)
    {
        string? path = itemId > 0 && catalog.Items.TryGetValue(itemId, out string? iconPath) ? iconPath : null;
        return Icon(path, size, 3);
    }

    /// <summary>召唤师技能图标。</summary>
    public static Element SpellIcon(GameDataCatalog catalog, int spellId, double size)
    {
        string? path = spellId > 0 && catalog.Spells.TryGetValue(spellId, out string? iconPath) ? iconPath : null;
        return Icon(path, size, 3);
    }

    /// <summary>符文图标。</summary>
    public static Element RuneIcon(GameDataCatalog catalog, int runeId, double size)
    {
        string? path = runeId > 0 && catalog.Runes.TryGetValue(runeId, out string? iconPath) ? iconPath : null;
        return Icon(path, size, size / 2);
    }

    /// <summary>海克斯强化背景色（旧版 .augment-slot / .row-augment-slot: rgba(147, 51, 234, 0.12)）。</summary>
    public const string AugmentBackgroundColor = AssetColors.AugmentBackground;

    /// <summary>海克斯强化边框色（旧版 .augment-slot / .row-augment-slot: rgba(168, 85, 247, 0.45)）。</summary>
    public const string AugmentBorderColor = AssetColors.AugmentBorder;

    /// <summary>海克斯强化图标（仅 2400/2450 队列）。带名称提示与海克斯背景边框。</summary>
    public static Element AugmentIcon(GameDataCatalog catalog, int augmentId, double size)
    {
        string? iconPath = null;
        string name = "海克斯强化";
        if (catalog.Augments.TryGetValue(augmentId, out CherryAugmentDetail? aug))
        {
            iconPath = aug.IconPath;
            name = aug.Name;
        }

        Element image = string.IsNullOrEmpty(iconPath)
            ? Border(Empty())
            : Component<LcuImage, LcuImageProps>(new LcuImageProps(iconPath, size, 4));

        return Border(image)
            .Width(size)
            .Height(size)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Background(AugmentBackgroundColor)
            .WithBorder(AugmentBorderColor, 1)
            .WithToolTip(Caption(name))
            .AutomationName($"海克斯强化：{name}");
    }

    /// <summary>是否为带海克斯强化的队列（2400 海克斯大乱斗 / 2450 经典海斗）。</summary>
    public static bool IsAugmentQueue(int queueId) => queueId is 2400 or 2450;

    /// <summary>
    /// 装备行：前六格连排 + 固定空档 + 守卫格。
    ///
    /// 空档是原实现的固定布局：守卫与装备之间留 8 DIP，避免玩家把眼位误读成第七件装备。
    /// 空槽同样渲染占位块（而不是跳过），否则装备少的行会把后面的数字整体左移，列就歪了。
    /// </summary>
    public static Element ItemRow(GameDataCatalog catalog, IReadOnlyList<int> itemIds, double size)
    {
        var slots = new List<Element>(ItemSlotCount + 1);

        for (var slot = 0; slot < ItemSlotCount - 1; slot++)
        {
            slots.Add(ItemIcon(catalog, ItemAt(itemIds, slot), size));
        }

        slots.Add(Spacer(8));
        slots.Add(ItemIcon(catalog, ItemAt(itemIds, ItemSlotCount - 1), size));

        return HStack(2, slots.ToArray());
    }

    /// <summary>取第 <paramref name="slot"/> 格装备 ID；越界视为未出装。</summary>
    private static int ItemAt(IReadOnlyList<int> itemIds, int slot) =>
        slot < itemIds.Count ? itemIds[slot] : 0;

    /// <summary>定宽透明占位，用于把相邻两段拉开固定距离（HStack 只能设统一间距）。</summary>
    public static Element Spacer(double width) => Border(Empty()).Width(width).Height(1);

    /// <summary>定高透明占位：列表项之间的纵向间隙（ListView 的项容器自身不带外边距）。</summary>
    public static Element VerticalGap(double height) => Border(Empty()).Height(height).Width(1);

    /// <summary>胜负文案：重开优先于胜负（重开局也有 win 字段，显示"胜利"会误导）。</summary>
    public static string ResultText(MatchSummary summary) =>
        summary.Remake ? "重开" : summary.Win ? "胜利" : "失败";

    /// <summary>卡片底色：取配置里的三套颜色（ADR-12），与设置页配色项一一对应。</summary>
    public static string CardBackground(YuumiPalette palette, MatchSummary summary) =>
        summary.Remake ? palette.RemakeBg : summary.Win ? palette.WinBg : palette.LoseBg;

    /// <summary>分路标签（上单/打野/中单/下路/辅助）。</summary>
    public static string? PositionText(MatchPosition? position) => position switch
    {
        MatchPosition.Top => "上单",
        MatchPosition.Jungle => "打野",
        MatchPosition.Middle => "中单",
        MatchPosition.Bottom => "下路",
        MatchPosition.Utility => "辅助",
        _ => null,
    };

    /// <summary>卡片描边与结果文字用的实色（不含 alpha，见 <see cref="YuumiPalette.WinText"/>）。</summary>
    public static string ResultAccent(YuumiPalette palette, MatchSummary summary) =>
        summary.Remake ? palette.RemakeText : summary.Win ? palette.WinText : palette.LoseText;

    /// <summary>卡片描边用的浅色形态。</summary>
    public static string ResultBorder(YuumiPalette palette, MatchSummary summary) =>
        summary.Remake ? palette.RemakeBorder : summary.Win ? palette.WinBorder : palette.LoseBorder;

    /// <summary>把 8 位 <c>#AARRGGBB</c> 的 alpha 换成指定两位十六进制值。</summary>
    public static string WithAlpha(string argb, string alpha) =>
        argb.Length == 9 && argb[0] == '#' ? string.Concat("#", alpha, argb.AsSpan(3)) : argb;

    /// <summary>
    /// KDA 文本块：死亡数单独着色（非本人行严格走 palette.DeathText，本人行统一走胜负色）。
    /// </summary>
    public static Element Kda(
        int kills,
        int deaths,
        int assists,
        YuumiPalette palette,
        bool strong = false,
        string? uniformColor = null)
    {
        TextBlockElement Weighted(TextBlockElement block) => strong ? block.SemiBold() : block;

        static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);

        if (uniformColor is not null)
        {
            return HStack(0,
                Weighted(Caption(Str(kills)).Foreground(uniformColor)),
                Weighted(Caption("/").Foreground(uniformColor)),
                Weighted(Caption(Str(deaths)).Foreground(uniformColor)),
                Weighted(Caption("/").Foreground(uniformColor)),
                Weighted(Caption(Str(assists)).Foreground(uniformColor)));
        }

        return HStack(0,
            Weighted(Caption(Str(kills)).Foreground(Theme.PrimaryText)),
            Weighted(Caption("/").Foreground(Theme.SecondaryText)),
            Weighted(Caption(Str(deaths)).Foreground(palette.DeathText)),
            Weighted(Caption("/").Foreground(Theme.SecondaryText)),
            Weighted(Caption(Str(assists)).Foreground(Theme.PrimaryText)));
    }

    /// <summary>表格里的数字列：定宽 + 文本对齐，保证各行末位对齐，杜绝与其他列数字粘连。</summary>
    public static Element NumberCell(
        string text,
        double width,
        bool strong = false,
        string? foreground = null,
        TextAlignment alignment = TextAlignment.Right)
    {
        var cell = NumberCellCore(text, width, strong, alignment);
        return foreground is null ? cell : cell.Foreground(foreground);
    }

    /// <summary>同上，前景色取系统结构色令牌（胜负/强调色仍是字符串，故按类型重载）。</summary>
    public static Element NumberCell(
        string text,
        double width,
        bool strong,
        ThemeRef foreground,
        TextAlignment alignment) =>
        NumberCellCore(text, width, strong, alignment).Foreground(foreground);

    private static TextBlockElement NumberCellCore(string text, double width, bool strong, TextAlignment alignment)
    {
        var hAlign = alignment switch
        {
            TextAlignment.Center => HorizontalAlignment.Center,
            TextAlignment.Left => HorizontalAlignment.Left,
            _ => HorizontalAlignment.Right,
        };

        var cell = Caption(text)
            .Width(width)
            .TextAlignment(alignment)
            .HAlign(hAlign);

        return strong ? cell.SemiBold() : cell;
    }

    /// <summary>千分位数字（金币与输出）。InvariantCulture 保证与客户端显示一致，不受系统区域影响。</summary>
    public static string Thousands(int value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>纵向 1px 分隔线：用底色差值画线，避免给每行都套一层 Border 描边。</summary>
    public static Element Divider(ThemeRef color) =>
        Border(Empty()).Height(1).HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch).Background(color);
}
