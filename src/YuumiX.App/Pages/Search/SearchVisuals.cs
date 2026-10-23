using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Assets;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Parsers;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 战绩页的纯展示辅助：把契约模型里的 ID 变成可渲染的元素。
///
/// 页面里不再出现"拼接 icon 路径"或"判断胜负该用哪个颜色"的散装代码——
/// 这类规则一旦分散到各组件，改主题色或改图标约定就要满页找。
/// </summary>
internal static class SearchVisuals
{
    /// <summary>装备格数（含守卫格）。</summary>
    public const int ItemSlotCount = 7;

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

    /// <summary>基石符文图标。</summary>
    public static Element RuneIcon(GameDataCatalog catalog, int runeId, double size)
    {
        string? path = runeId > 0 && catalog.Runes.TryGetValue(runeId, out string? iconPath) ? iconPath : null;
        return Icon(path, size, size / 2);
    }

    /// <summary>海克斯强化图标。</summary>
    public static Element AugmentIcon(GameDataCatalog catalog, int augmentId, double size)
    {
        string? path = catalog.Augments.TryGetValue(augmentId, out CherryAugmentDetail? detail)
            ? detail.IconPath
            : null;

        return Icon(path, size, 4);
    }

    /// <summary>胜负文案：重开优先于胜负（重开局也有 win 字段，显示"胜利"会误导）。</summary>
    public static string ResultText(MatchSummary summary) =>
        summary.Remake ? "重开" : summary.Win ? "胜利" : "失败";

    /// <summary>卡片底色：取配置里的三套颜色（ADR-12），与设置页配色项一一对应。</summary>
    public static string CardBackground(YuumiPalette palette, MatchSummary summary) =>
        summary.Remake ? palette.RemakeCard : summary.Win ? palette.WinCard : palette.LoseCard;

    /// <summary>分路标签。原实现用英文缩写，这里保持同样的短形态。</summary>
    public static string? PositionText(MatchPosition? position) => position switch
    {
        MatchPosition.Top => "上单",
        MatchPosition.Jungle => "打野",
        MatchPosition.Middle => "中单",
        MatchPosition.Bottom => "下路",
        MatchPosition.Utility => "辅助",
        _ => null,
    };

    /// <summary>一行"标签 + 值"的小文本块，详情区大量重复，抽出来统一字色与间距。</summary>
    public static Element Field(string label, string value) =>
        HStack(6,
            TextBlock(label).FontSize(11).Foreground(Theme.SecondaryText),
            TextBlock(value).FontSize(12));
}
