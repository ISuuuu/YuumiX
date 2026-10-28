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
    /// 生涯页正文列宽（DIP）。三个页签（生涯战绩 / 战利品 / 英雄熟练度）**共用这一个值**，
    /// 页签切换时左右边界才不会跳。
    ///
    /// 此前各页签各自写死内部宽度：战利品与熟练度按 916 排（4 张 220 的卡 + 3×12 间距），
    /// 于是比铺满 1000 的生涯战绩整体窄 84 且靠左，切页签就能看到内容宽度变化。
    /// 现在所有区块都以 <see cref="ContentWidth"/> 为基准反算自己的可用宽，改一处即可整体对齐。
    /// </summary>
    public const double ContentWidth = 1000;

    /// <summary>卡片的横向间距（战利品 / 熟练度的多列卡片栅格）。</summary>
    public const double CardGap = 12;

    /// <summary>
    /// 卡片内部可用宽（用于「卡片里再左右分栏」的头部行）。
    /// 卡片外框宽恒为 <see cref="ContentWidth"/>，内容宽 = 1000 − 左右内边距。
    /// </summary>
    public static double CardInnerWidth(double padding) => ContentWidth - padding * 2;

    /// <summary>
    /// 等分栅格里的单卡宽。<paramref name="containerPadding"/> 是**外层卡片**的内边距：
    /// 0 表示直接排在正文列里（如熟练度的汇总卡与英雄网格），16 表示排在一张
    /// <see cref="Card(Element, double)"/> 内部（如战利品的材料/库存区块）。
    /// 不区分这两者会让「卡内栅格」比容器宽出 2×padding 而被裁掉。
    /// </summary>
    public static double GridCardWidth(int columns, double containerPadding) =>
        (CardInnerWidth(containerPadding) - CardGap * (columns - 1)) / columns;

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
    public static Element Stat(string label, string value, string? valueColor = null, bool colorLabel = false)
    {
        Element valueText = Caption(value).SemiBold().VAlign(VerticalAlignment.Center);
        if (valueColor is not null)
        {
            valueText = valueText.Foreground(valueColor);
        }

        var labelElement = Caption(label).VAlign(VerticalAlignment.Center);
        labelElement = (colorLabel && valueColor is not null)
            ? labelElement.Foreground(valueColor)
            : labelElement.Foreground(Theme.SecondaryText);

        return HStack(4, labelElement, valueText);
    }

    /// <summary>分路文案（与战绩列表共用同一套简写，避免两处各写一份）。</summary>
    public static string PositionText(MatchPosition? position)
    {
        var i18n = Composition.AppHost.Services?.I18n;
        return Search.SearchVisuals.PositionText(position)
            ?? (i18n?.T("career.posUnknown", "未知分路") ?? "未知分路");
    }

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
    public static Element EmptyHeader() =>
        Card(
            VStack(6,
                BodyStrong("未读取到当前召唤师（客户端未登录）"),
                Caption("启动并登录英雄联盟客户端后，这里会显示头像、等级与排位成绩。")),
            padding: 20);

    /// <summary>页签按钮：选中时用底色与描边区分，文字统一采用主文本色（PrimaryText），保持与控件一致的对比度。</summary>
    public static Element TabButton(
        string label,
        CareerTab target,
        CareerTab current,
        Action<CareerTab> setTab)
    {
        bool isSelected = target == current;
        var normalWeight = new Windows.UI.Text.FontWeight(400);
        var text = (Body(label) with { Weight = normalWeight })
            .Foreground(Theme.PrimaryText)
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
        .FontWeight(normalWeight)
        // 旧版 `.career-tab-item`：padding 6×16 + 字号 0.88rem（14.08）→ 实际行高 33。
        // 横向 16 走 Padding，纵向由 Height 固定（6 的上下内边距换成 33 的盒子，光标与命中区一致）。
        .Height(33)
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
    public static Element TabBar(CareerTab current, Action<CareerTab> setTab)
    {
        var i18n = Composition.AppHost.Services?.I18n;
        var matchesText = i18n?.T("career.recentMatches", "生涯战绩") ?? "生涯战绩";
        var lootText = i18n?.T("tools.lootManager.title", "战利品") ?? "战利品";
        var masteryText = i18n?.T("gameInfo.masteryBadge", "英雄熟练度") ?? "英雄熟练度";

        return VStack(8,
            HStack(8,
                TabButton(matchesText, CareerTab.Matches, current, setTab),
                TabButton(lootText, CareerTab.Loot, current, setTab),
                TabButton(masteryText, CareerTab.Mastery, current, setTab)),
            Border(Empty()).Height(1).Background(Theme.DividerStroke).HAlign(HorizontalAlignment.Stretch));
    }

    // ── 骨架屏（加载态的结构占位）────────────────────────────────────────
    //
    // 三条纪律：
    // 1. **与真实内容同尺寸**：骨架的宽高必须等于终态，否则数据到达时仍会重排，
    //    骨架就白做了。下面每个尺寸都对齐真实构件（见各处注释）。
    // 2. **静态灰块，不做呼吸动画**：与战绩查询页的 `MatchCardView.Skeleton` 保持一致，
    //    避免两页加载态观感分裂；也避免在文字附近引入动效。
    // 3. **只占位、不交互**：骨架态的页签不可点（加载中切页签没有意义）。

    /// <summary>骨架里的灰块：统一底色与圆角，尺寸由调用方给。</summary>
    private static Element Block(double width, double height, double radius = 4) =>
        Border(Empty())
            .Width(width)
            .Height(height)
            .CornerRadius(radius)
            .Background(Theme.SubtleFill);

    /// <summary>
    /// 资料卡骨架：对应 <see cref="CareerProfileHeader.Render"/> 的终态结构——
    /// 104 经验环 + 名字 + Tag 两行文字，右侧两枚 28 高操作按钮；外层同样是
    /// padding 24 的卡片。终态高 = 104 + 24×2 = 152，骨架必须同高，
    /// 否则资料卡到位时整页会下移 152 − 实际高度。
    /// </summary>
    public static Element SkeletonHeader() =>
        Border(
            Grid(
                [GridSize.Star(), GridSize.Auto, GridSize.Star()],
                [GridSize.Auto],
                [
                    HStack(20,
                            Block(104, 104, 52),
                            VStack(4, Block(140, 20), Block(80, 14)))
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 1),
                    VStack(8, Block(72, 28, YuumiTheme.OverlayRadius), Block(72, 28, YuumiTheme.OverlayRadius))
                        .HAlign(HorizontalAlignment.Right)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 2),
                ]))
            .Padding(24)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);

    /// <summary>
    /// 页签栏骨架：三个页签位 + 底部分隔线。
    /// 页签宽度按「14px 正文字体的中文全角宽 = 字号」估算，与真实页签
    /// （文字 + 左右 16 padding）逐个对齐，页签之间的间距因此不会跳。
    /// </summary>
    public static Element SkeletonTabBar() =>
        VStack(8,
            HStack(8,
                SkeletonTab(56),
                SkeletonTab(42),
                SkeletonTab(70)),
            Border(Empty()).Height(1).Background(Theme.DividerStroke).HAlign(HorizontalAlignment.Stretch));

    /// <summary>单个页签位：文字条 + 与真实页签相同的 padding 与高度（33）。</summary>
    private static Element SkeletonTab(double textWidth) =>
        Border(Block(textWidth, 14))
            .Height(33)
            .Padding(16, 0)
            .CornerRadius(YuumiTheme.OverlayRadius);

    /// <summary>
    /// 战绩列表骨架：与 <c>CareerMatchRow</c> 的 82 高卡片同高、同圆角、同描边，
    /// 列表步进沿用真实布局（VStack 间距 8 + 每项 2 的下外边距 = 行距 10）。
    /// 数量取 8：视口只放得下几行，多铺是浪费，但也不能太少——否则骨架
    /// 比真实列表短一截，数据到达时列表下方会「长出来」。
    /// </summary>
    public static Element SkeletonMatchList(int count = 8) =>
        VStack(8, Enumerable.Range(0, count)
            .Select(index => Border(Empty())
                .Height(CareerMatchRow.RowHeight)
                .CornerRadius(YuumiTheme.OverlayRadius)
                .Background(Theme.SubtleFill)
                .WithBorder(Theme.CardStroke, 1)
                .Margin(0, 0, 0, 2)
                .WithKey($"career-skeleton-{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
            .ToArray());

    /// <summary>
    /// 排位段位表骨架：与 <see cref="RankedTableView"/> 同为 9 列等分、同高 103（三行 33 + 两条 1px 分隔线）、
    /// padding 0 出血卡片。加载中排位数据尚未到达时占位，防止整页出现 "--" 真实空表且在数据到达时跳变。
    /// </summary>
    public static Element SkeletonRankedTable()
    {
        var columns = Enumerable.Repeat(GridSize.Star(), 9).ToArray();
        var rows = new[] { GridSize.Auto };

        Element HeaderRow() =>
            Grid(columns, rows,
                Enumerable.Range(0, 9).Select(i =>
                    Block(i == 0 ? 32 : 48, 12)
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center)
                        .Padding(16, 6)
                        .Grid(row: 0, column: i)
                        .WithKey($"ranked-skel-head-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
                .ToArray())
            .MinHeight(33);

        Element DataRow(string rowKey, double firstColWidth = 56) =>
            Grid(columns, rows,
                Enumerable.Range(0, 9).Select(i =>
                    Block(i == 0 ? firstColWidth : (i == 5 ? 60 : 36), 12)
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center)
                        .Padding(16, 6)
                        .Grid(row: 0, column: i)
                        .WithKey($"ranked-skel-{rowKey}-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
                .ToArray())
            .MinHeight(33);

        Element Divider() =>
            Border(Empty()).Height(1).Background(Theme.DividerStroke).HAlign(HorizontalAlignment.Stretch);

        return Card(
            VStack(0,
                HeaderRow(),
                Divider(),
                DataRow("row1", 56),
                Divider(),
                DataRow("row2", 56)),
            padding: 0);
    }

    /// <summary>
    /// 近期对局看板骨架：与 <see cref="RecentSummaryView"/> 终态同高（50 = 32 内容 + 8×2 padding + 2 边框），
    /// padH 16、padV 8。左侧为战绩文字/常玩位置/常用英雄头像灰块，右侧为队友按钮与下拉位。
    /// </summary>
    public static Element SkeletonRecentSummary() =>
        Card(
            Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [
                    HStack(16,
                        Block(160, 16),
                        HStack(6,
                            Block(56, 14),
                            HStack(4, Block(48, 18, YuumiTheme.ControlRadius), Block(48, 18, YuumiTheme.ControlRadius))),
                        HStack(4, Enumerable.Range(0, 6)
                            .Select(i => Block(32, 32, 16).WithKey($"recent-skel-champ-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
                            .ToArray()))
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 0),
                    HStack(8,
                        Block(72, 28, YuumiTheme.ControlRadius),
                        Block(140, 32, YuumiTheme.ControlRadius))
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 1),
                ]),
            padH: 16,
            padV: 8);

    /// <summary>
    /// 战利品页签骨架：材料区（标题 + 4 列卡片）与库存管理区（工具栏 + 2 行 4 列卡片），
    /// 尺寸与 <see cref="LootManagerView"/> 严格对齐。
    /// </summary>
    public static Element SkeletonLoot()
    {
        double cardWidth = GridCardWidth(4, 16);

        Element materialsSection = Card(
            VStack(16,
                Block(140, 20),
                HStack(CardGap, Enumerable.Range(0, 4)
                    .Select(i => Border(Empty())
                        .Width(cardWidth)
                        .Height(120)
                        .CornerRadius(YuumiTheme.OverlayRadius)
                        .Background(Theme.SubtleFill)
                        .WithBorder(Theme.CardStroke, 1)
                        .WithKey($"loot-skel-mat-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
                    .ToArray())),
            padding: 16);

        Element inventorySection = Card(
            VStack(16,
                HStack(12, Block(280, 28), Block(180, 28)),
                VStack(CardGap,
                    HStack(CardGap, Enumerable.Range(0, 4)
                        .Select(i => Border(Empty())
                            .Width(cardWidth)
                            .Height(130)
                            .CornerRadius(YuumiTheme.OverlayRadius)
                            .Background(Theme.SubtleFill)
                            .WithBorder(Theme.CardStroke, 1)
                            .WithKey($"loot-skel-inv-r1-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
                        .ToArray()),
                    HStack(CardGap, Enumerable.Range(0, 4)
                        .Select(i => Border(Empty())
                            .Width(cardWidth)
                            .Height(130)
                            .CornerRadius(YuumiTheme.OverlayRadius)
                            .Background(Theme.SubtleFill)
                            .WithBorder(Theme.CardStroke, 1)
                            .WithKey($"loot-skel-inv-r2-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
                        .ToArray()))),
            padding: 16);

        return VStack(16, materialsSection, inventorySection);
    }

    /// <summary>
    /// 英雄熟练度页签骨架：顶部 4 张汇总卡片 + 工具栏卡片 + 2 行 4 列英雄卡片，
    /// 尺寸与 <see cref="ChampionMasteryView"/> 严格对齐。
    /// </summary>
    public static Element SkeletonMastery()
    {
        double cardWidth = GridCardWidth(4, 0);

        Element summaryCards = HStack(CardGap, Enumerable.Range(0, 4)
            .Select(i => Card(
                VStack(8, Block(80, 14), Block(120, 24), Block(90, 12)),
                padding: 16)
                .Width(cardWidth)
                .Height(96)
                .WithKey($"mastery-skel-sum-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
            .ToArray());

        Element toolbar = Card(
            HStack(12,
                Block(180, 28),
                Block(120, 28),
                Block(120, 28),
                Block(120, 28)),
            padH: 12,
            padV: 10);

        Element heroGrid = VStack(CardGap,
            HStack(CardGap, Enumerable.Range(0, 4)
                .Select(i => Border(Empty())
                    .Width(cardWidth)
                    .Height(100)
                    .CornerRadius(YuumiTheme.OverlayRadius)
                    .Background(Theme.SubtleFill)
                    .WithBorder(Theme.CardStroke, 1)
                    .WithKey($"mastery-skel-hero-r1-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
                .ToArray()),
            HStack(CardGap, Enumerable.Range(0, 4)
                .Select(i => Border(Empty())
                    .Width(cardWidth)
                    .Height(100)
                    .CornerRadius(YuumiTheme.OverlayRadius)
                    .Background(Theme.SubtleFill)
                    .WithBorder(Theme.CardStroke, 1)
                    .WithKey($"mastery-skel-hero-r2-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
                .ToArray()));

        return VStack(16, summaryCards, toolbar, heroGrid);
    }
}
