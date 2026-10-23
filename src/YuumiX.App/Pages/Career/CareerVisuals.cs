using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Common;
using YuumiX.Core.Features.Match.Models;

namespace YuumiX.App.Pages.Career;

/// <summary>
/// 生涯页的纯展示辅助：ID → 资源路径、配色混算、表格单元格。
///
/// 与 <c>SearchVisuals</c> 的分工：那边负责"一局比赛"的视觉约定（胜负底色、装备格、
/// 英雄头像路径），这边负责"生涯"独有的部分（头像、段位奖章、选中态配色）。
/// 两者都只做转换，不做业务判断——胜负该怎么判、胜率怎么算，都在 Core 模型上。
/// </summary>
internal static class CareerVisuals
{
    /// <summary>
    /// 头像资源路径。LCU 的头像同时提供 <c>.jpg</c>（旧接口）与 <c>.png</c>（新版本）；
    /// 这里沿用原实现的 <c>.jpg</c>，与 <c>LcuAssetSource</c> 的路径白名单一致。
    /// </summary>
    public static string ProfileIcon(int profileIconId) =>
        $"/lol-game-data/assets/v1/profile-icons/{profileIconId}.jpg";

    /// <summary>
    /// 段位奖章 URI；未定级或资源缺失时返回 <c>null</c>（调用方决定是留白还是画占位）。
    /// </summary>
    public static string? RankedMedalUri(string? tier) =>
        tier is not null && AppAssets.RankedIconUris.TryGetValue(tier, out string? uri) ? uri : null;

    /// <summary>段位奖章图标；无对应资源时给一个同尺寸占位，避免"排位表这一格突然变窄"。</summary>
    public static Element RankedMedal(string? tier, double size)
    {
        string? uri = RankedMedalUri(tier);
        return uri is null
            ? Border(Empty())
                .Width(size)
                .Height(size)
                .CornerRadius(size / 2)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke, 1)
            : Image(uri).Width(size).Height(size).AutomationName($"段位：{tier}");
    }

    /// <summary>
    /// 给颜色换一个 alpha（输入输出均为 <c>#AARRGGBB</c>）。
    ///
    /// 选中页签、徽标底色都用"强调色 + 低透明度"，而 <see cref="YuumiPalette.Accent"/>
    /// 是不透明值；Reactor 只接受字符串，所以只能在这里拼。
    /// 解析失败时原样返回——宁可少一层透明，也不要让整页因为一个颜色字符串渲染异常。
    /// </summary>
    public static string WithAlpha(string color, byte alpha) =>
        ColorHex.TryParseArgb(color, out _, out byte r, out byte g, out byte b)
            ? ColorHex.ToArgb(alpha, r, g, b)
            : color;

    /// <summary>表格单元格（固定宽度 + 居中对齐），表头与数据行共用同一套宽度。</summary>
    public static Element Cell(string text, double width, bool header = false) =>
        TextBlock(text)
            .Width(width)
            .FontSize(header ? 11 : 12)
            .TextAlignment(TextAlignment.Center)
            .Foreground(header ? Theme.SecondaryText : Theme.PrimaryText);

    /// <summary>一行「标签 : 值」，用于近期看板的统计文字。</summary>
    public static Element Stat(string label, string value, string? valueColor = null)
    {
        Element valueText = TextBlock(value).FontSize(13).FontWeight(Microsoft.UI.Text.FontWeights.SemiBold);
        if (valueColor is not null)
        {
            valueText = valueText.Foreground(valueColor);
        }

        return HStack(4,
            TextBlock(label).FontSize(11).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
            valueText);
    }

    /// <summary>分路文案（与战绩列表共用同一套简写，避免两处各写一份）。</summary>
    public static string PositionText(MatchPosition? position) =>
        Search.SearchVisuals.PositionText(position) ?? "未知分路";

    /// <summary>卡片外框：生涯页所有区块共用的圆角、底色与描边。</summary>
    public static Element Card(Element content, double padding = 16) =>
        Border(content)
            .Padding(padding)
            .CornerRadius(10)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);

    /// <summary>
    /// 胜利文字色。取自原 <c>App.vue</c> 的 <c>--win-color</c>（浅色 #065F46 / 深色 #10B981）。
    ///
    /// 为什么不复用 <see cref="YuumiPalette.WinCard"/>：那是卡片**底色**（带 20% alpha），
    /// 直接拿来写字会淡到看不清。胜负文字色在配置里没有对应项，因此按实际明暗取常量。
    /// </summary>
    public static string WinText => IsDarkTheme ? "#FF10B981" : "#FF065F46";

    /// <summary>失败文字色（原 <c>--loss-color</c>：浅色 #9F1239 / 深色 #F43F5E）。</summary>
    public static string LoseText => IsDarkTheme ? "#FFF43F5E" : "#FF9F1239";

    /// <summary>当前是否为深色主题。<c>Default</c> 时取系统设置，与 WinUI 的实际绘制口径一致。</summary>
    public static bool IsDarkTheme =>
        Application.Current?.RequestedTheme == ApplicationTheme.Dark;

    /// <summary>蓝色精粹专属色。</summary>
    public const string BlueEssenceColor = "#FF3B82F6";

    /// <summary>橙色精粹专属色。</summary>
    public const string OrangeEssenceColor = "#FFF97316";

    /// <summary>钥匙/黄金专属色。</summary>
    public const string GoldColor = "#FFEAB308";

    /// <summary>通用小胶囊标签（Pill / Chip）。</summary>
    public static Element Chip(
        string text,
        string foreground,
        string background,
        double fontSize = 11,
        double padX = 6,
        double padY = 2,
        string? borderBrush = null)
    {
        var border = Border(TextBlock(text)
                .FontSize(fontSize)
                .Foreground(foreground)
                .VAlign(VerticalAlignment.Center))
            .Padding(padX, padY)
            .CornerRadius(4)
            .Background(background);

        return borderBrush is not null
            ? border.WithBorder(borderBrush, 1)
            : border;
    }

    /// <summary>对局卡片背景色（胜利淡翡翠、失败淡玫瑰红、重开淡灰）。</summary>
    public static string MatchCardBg(bool win, bool remake)
    {
        if (remake)
        {
            return IsDarkTheme ? "#1AFFFFFF" : "#0A000000";
        }

        return win
            ? (IsDarkTheme ? "#1F10B981" : "#1410B981")
            : (IsDarkTheme ? "#1FF43F5E" : "#14F43F5E");
    }

    /// <summary>对局卡片边框色。</summary>
    public static string MatchCardBorder(bool win, bool remake)
    {
        if (remake)
        {
            return IsDarkTheme ? "#33FFFFFF" : "#1A000000";
        }

        return win
            ? (IsDarkTheme ? "#3310B981" : "#2E10B981")
            : (IsDarkTheme ? "#33F43F5E" : "#2EF43F5E");
    }
}
