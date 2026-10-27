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
    /// 选中页签、徽标底色都用"强调色 + 低透明度"，而 <see cref="Theme.Accent"/>
    /// 是不透明值；Reactor 只接受字符串，所以只能在这里拼。
    /// 解析失败时原样返回——宁可少一层透明，也不要让整页因为一个颜色字符串渲染异常。
    /// </summary>
    public static string WithAlpha(string color, byte alpha) =>
        ColorHex.TryParseArgb(color, out _, out byte r, out byte g, out byte b)
            ? ColorHex.ToArgb(alpha, r, g, b)
            : color;

    /// <summary>表格单元格（固定宽度 + 居中对齐），表头与数据行共用同一套宽度。</summary>
    public static Element Cell(string text, double width, bool header = false)
    {
        var cell = Caption(text)
            .Width(width)
            .TextAlignment(TextAlignment.Center)
            .Foreground(header ? Theme.SecondaryText : Theme.PrimaryText);

        // 表头层级改由 secondary 文本色承担：12px 中文字重 600 会被雅黑吸附到 Bold，笔画粘连发糊
        return cell;
    }

    /// <summary>
    /// 一行「标签 : 值」，用于近期看板的统计文字。
    /// 标签与值必须同为 VAlign(Center)：看板行高被 32px 英雄头像撑高后，未居中的
    /// TextBlock 会被拉伸并贴顶渲染，数字整体上浮约 8px（值与标签基线错位）。
    /// </summary>
    public static Element Stat(string label, string value, string? valueColor = null)
    {
        Element valueText = Caption(value).SemiBold().VAlign(VerticalAlignment.Center);
        if (valueColor is not null)
        {
            valueText = valueText.Foreground(valueColor);
        }

        return HStack(4,
            Caption(label).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
            valueText);
    }

    /// <summary>分路文案（与战绩列表共用同一套简写，避免两处各写一份）。</summary>
    public static string PositionText(MatchPosition? position) =>
        Search.SearchVisuals.PositionText(position) ?? "未知分路";

    /// <summary>卡片外框：生涯页所有区块共用的圆角、底色与描边。</summary>
    public static Element Card(Element content, double padding = 16) =>
        Card(content, padding, padding);

    /// <summary>
    /// 卡片外框（横向/纵向内边距分开给）。
    ///
    /// 旧版各区块的内边距并不统一：资料卡 24、排位表 0（单元格自带 6×16）、
    /// 近期看板 10×16、战绩列表 20（卡片自带 14×20）。只给一个统一值会把
    /// 「行内文字留白」压扁成方形，因此保留分开给的入口。
    /// </summary>
    public static Element Card(Element content, double padH, double padV) =>
        Border(content)
            .Padding(padH, padV)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);

    /// <summary>
    /// 胜利文字色。取自原 <c>App.vue</c> 的 <c>--win-color</c>（浅色 #065F46 / 深色 #10B981）。
    ///
    /// 为什么不复用 <see cref="YuumiPalette.WinCard"/>：那是卡片**底色**（带 20% alpha），
    /// 直接拿来写字会淡到看不清。胜负文字色在配置里没有对应项，因此按实际明暗取常量。
    /// </summary>
    public static string WinText => IsDarkTheme ? AssetColors.WinDark : AssetColors.WinLight;

    /// <summary>失败文字色（原 <c>--loss-color</c>：浅色 #9F1239 / 深色 #F43F5E）。</summary>
    public static string LoseText => IsDarkTheme ? AssetColors.LoseDark : AssetColors.LoseLight;

    /// <summary>当前是否为深色主题。<c>Default</c> 时取系统设置，与 WinUI 的实际绘制口径一致。</summary>
    public static bool IsDarkTheme =>
        Application.Current?.RequestedTheme == ApplicationTheme.Dark;

    /// <summary>蓝色精粹专属色。</summary>
    public const string BlueEssenceColor = AssetColors.BlueEssence;

    /// <summary>橙色精粹专属色。</summary>
    public const string OrangeEssenceColor = AssetColors.OrangeEssence;

    /// <summary>钥匙/黄金专属色。</summary>
    public const string GoldColor = AssetColors.Gold;

    /// <summary>通用小胶囊标签（Pill / Chip）。</summary>
    public static Element Chip(
        string text,
        string foreground,
        string background,
        double padX = 6,
        double padY = 2,
        string? borderBrush = null)
    {
        var border = Border(Caption(text)
                .Foreground(foreground)
                .VAlign(VerticalAlignment.Center))
            .Padding(padX, padY)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Background(background);

        return borderBrush is not null
            ? border.WithBorder(borderBrush, 1)
            : border;
    }

    /// <summary>D1：读取失败的提示条（对齐旧版失败 toast 的文案口径）。</summary>
    public static Element FailureHint(string message, Action onRetry) =>
        Card(
            HStack(12,
                Body(message).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                Button(Caption("重试"), onRetry).Height(30).Padding(12, 0).AutomationName($"重试：{message}")),
            padding: 16);

    /// <summary>未登录时的资料卡占位：说明原因而不是留白。</summary>
    public static Element EmptyHeader(bool loading) =>
        Card(
            VStack(6,
                BodyStrong(loading ? "正在读取生涯数据…" : "未读取到当前召唤师（客户端未登录）"),
                Caption("启动并登录英雄联盟客户端后，这里会显示头像、等级与排位成绩。")),
            padding: 20);

    /// <summary>页签按钮：选中时用强调色文字 + 浅色底色与边框，未选中走次要文字色。</summary>
    public static Element TabButton(
        string label,
        CareerTab target,
        CareerTab current,
        Action<CareerTab> setTab)
    {
        bool isSelected = target == current;
        var text = Body(label)
            .Foreground(isSelected ? Theme.AccentText : Theme.SecondaryText)
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);

        Element button = Button(text, () =>
        {
            if (target == current)
            {
                return;
            }

            setTab(target);
        })
        .Height(30)
        .Padding(16, 0)
        .CornerRadius(YuumiTheme.OverlayRadius)
        .AutomationName(label);

        return isSelected
            ? button
                .Background(Theme.SubtleFill)
                .WithBorder(Theme.CardStroke, 1)
            : button
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .BorderThickness(0);
    }

    /// <summary>页签栏：三个页签按钮 + 底部分隔线。</summary>
    public static Element TabBar(CareerTab current, Action<CareerTab> setTab) =>
        VStack(8,
            HStack(8,
                TabButton("生涯战绩", CareerTab.Matches, current, setTab),
                TabButton("战利品", CareerTab.Loot, current, setTab),
                TabButton("英雄熟练度", CareerTab.Mastery, current, setTab)),
            Border(Empty()).Height(1).Background(Theme.CardStroke).HAlign(HorizontalAlignment.Stretch));
}
