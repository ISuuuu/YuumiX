using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
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
        summary.Remake ? palette.RemakeCard : summary.Win ? palette.WinCard : palette.LoseCard;

    /// <summary>卡片描边与结果文字用的实色（不含 alpha，见 <see cref="YuumiPalette.WinAccent"/>）。</summary>
    public static string ResultAccent(YuumiPalette palette, MatchSummary summary) =>
        summary.Remake ? palette.RemakeAccent : summary.Win ? palette.WinAccent : palette.LoseAccent;

    /// <summary>
    /// 卡片描边用的**浅色**形态：把胜负底色的 alpha 提到 <c>0x66</c>。
    ///
    /// 直接拿实色描边会让整列卡片镶一圈深绿/深红，页面显得很重；
    /// 原实现的描边是"比底色深一档"的浅色细线，因此这里只调 alpha、不改色相。
    /// </summary>
    public static string ResultBorder(YuumiPalette palette, MatchSummary summary) =>
        WithAlpha(CardBackground(palette, summary), "66");

    /// <summary>把 8 位 <c>#AARRGGBB</c> 的 alpha 换成指定两位十六进制值。</summary>
    public static string WithAlpha(string argb, string alpha) =>
        argb.Length == 9 && argb[0] == '#' ? string.Concat("#", alpha, argb.AsSpan(3)) : argb;

    /// <summary>
    /// KDA 文本块：死亡数单独着色。
    ///
    /// 拆成五段而不是一个字符串，是因为原实现里死亡数是唯一的红色数字，
    /// 语义上就是"该被注意的那一项"；用 RichText 又要处理段落样式与裁剪，反而更重。
    /// </summary>
    public static Element Kda(
        int kills,
        int deaths,
        int assists,
        double fontSize,
        YuumiPalette palette)
    {
        Element Number(int value, string? color = null)
        {
            Element text = TextBlock(value.ToString(CultureInfo.InvariantCulture)).FontSize(fontSize);
            return color is null ? text : text.Foreground(color);
        }

        return HStack(0,
            Number(kills),
            TextBlock("/").FontSize(fontSize).Foreground(Theme.SecondaryText),
            Number(deaths, palette.LoseAccent),
            TextBlock("/").FontSize(fontSize).Foreground(Theme.SecondaryText),
            Number(assists));
    }

    /// <summary>表格里的数字列：定宽 + 右对齐，保证各行末位对齐。</summary>
    public static Element NumberCell(string text, double width, bool strong = false)
    {
        Element cell = TextBlock(text)
            .FontSize(12)
            .Width(width)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Right);

        return strong ? cell.FontWeight(Microsoft.UI.Text.FontWeights.SemiBold) : cell;
    }

    /// <summary>千分位数字（金币与输出）。InvariantCulture 保证与客户端显示一致，不受系统区域影响。</summary>
    public static string Thousands(int value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>纵向 1px 分隔线：用底色差值画线，避免给每行都套一层 Border 描边。</summary>
    public static Element Divider(string color) =>
        Border(Empty()).Height(1).HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch).Background(color);
}
