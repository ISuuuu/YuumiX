using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;

namespace YuumiX.App.Diagnostics;

/// <summary>
/// 架构能力验证页。
///
/// 存在的目的：在迁移任何业务代码之前，先证明 Reactor 能覆盖 Yuumi 的四个高风险场景。
/// 这四项一旦不成立，整体方案就要调整，因此必须先行验证：
///   1. 自定义主题色 —— 原项目用 CSS 变量驱动动态主题色，Reactor 需靠 Theme 令牌 + 直接色值；
///   2. 网络图片加载 —— 原项目全部英雄/装备/技能图标来自 LCU 与 CDragon，这是最大的迁移风险；
///   3. 多窗口 —— 原项目有 OP.GG 独立窗口与大乱斗悬浮窗；
///   4. 系统托盘 —— 原项目支持关闭到托盘与托盘菜单。
/// </summary>
internal sealed class CapabilityLab : Component
{
    /// <summary>
    /// 真实 CDragon 资源地址，用于验证 Image 对网络图片的加载能力。
    /// 路径规则对应原 Rust 端 map_asset_path_to_cdragon_url 的 v1/champion-icons 分支。
    /// </summary>
    private const string ChampionIconUrl =
        "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/champion-icons/266.png";

    /// <summary>网格的说明文字：把「首次下载 / 二次命中缓存」的预期写清楚，便于判断是否真的生效。</summary>
    private const string ChampionIconNote =
        "10 列 × 20 行。首次加载会触发约 200 次资源解析（LCU 未连接时全部走 CDragon）；" +
        "再次打开该页面应明显更快，即全部命中磁盘缓存。个别 ID 无对应英雄时显示占位符。";

    public override Element Render()
    {
        var (trayVisible, setTrayVisible) = UseState(false);

        // 托盘图标由状态驱动可见性，而不是条件调用 hook —— hook 顺序必须恒定。
        UseTrayIcon(new TrayIconSpec(
            WindowIcon.FromRgba(BuildTrayPixels(), 16, 16),
            "YuumiX 架构验证",
            IsVisible: trayVisible));

        return ScrollViewer(
            VStack(20,
                VStack(4,
                    Title("架构能力验证")
                        .HeadingLevel(AutomationHeadingLevel.Level1),
                    Caption("迁移业务代码前，先确认 Reactor 覆盖 Yuumi 的关键场景")),

                Section("1 · 主题与配色",
                    "验证 Theme 系统令牌与 Yuumi 自定义色能否共存",
                    HStack(12,
                        ThemeSwatch("Theme.Accent", Theme.Accent),
                        ColorSwatch("胜利", YuumiTheme.WinCard),
                        ColorSwatch("失败", YuumiTheme.LoseCard),
                        ColorSwatch("重赛", YuumiTheme.RemakeCard))),

                Section("2 · 网络图片",
                    "英雄/装备/技能图标全部来自 LCU 与 CDragon，这是迁移风险最高的一环",
                    HStack(16,
                        Image(ChampionIconUrl).Width(72).Height(72)
                            .AutomationName("CDragon 英雄图标"),
                        VStack(4,
                            TextBlock("CDragon 英雄图标（远程 URL）"),
                            Caption("若此图未显示，说明需要改用本地资源代理方案"))) ),

                Section("3 · 系统托盘",
                    "原项目支持关闭到托盘，需要在非打包模式下可用",
                    HStack(12,
                        Button(trayVisible ? "隐藏托盘图标" : "显示托盘图标", () => setTrayVisible(!trayVisible))
                            .AutomationName("切换托盘图标"),
                        Caption(trayVisible ? "托盘图标已显示（查看任务栏通知区域）" : "托盘图标当前隐藏"))),

                Section("4 · 多窗口",
                    "OP.GG 独立窗口与大乱斗悬浮窗依赖多窗口能力",
                    HStack(12,
                        Button("打开一个子窗口", OpenChildWindow)
                            .AutomationName("打开子窗口"),
                        Caption("验证 WindowSpec 独立窗口的生命周期"))),

                Section("5 · 本地资源链路（ADR-11）",
                    "图标经「磁盘缓存 → LCU → CDragon」解析后以 file:// 引用；此处用 200 项做滚动压力验证",
                    VStack(12,
                        Caption(ChampionIconNote),
                        BuildChampionIconGrid()))
            ).Padding(28).HAlign(HorizontalAlignment.Stretch)
        );
    }

    /// <summary>
    /// 200 个英雄头像的网格：10 列 × 20 行。
    ///
    /// 用它承担阶段 4 的验收：真实列表滚动时同一屏会同时请求数十张图，
    /// 只有这种密度才能暴露「并发上限、缓存落盘、解码卡顿」一类问题。
    /// 个别 ID 没有对应英雄，会显示占位符——这本身也是失败路径的实测。
    /// </summary>
    private static Element BuildChampionIconGrid()
    {
        const int perRow = 10;
        const int total = 200;

        var rows = new List<Element>();

        for (var start = 0; start < total; start += perRow)
        {
            var items = new List<Element>();

            for (var id = start; id < Math.Min(start + perRow, total); id++)
            {
                items.Add(Component<LcuImage, LcuImageProps>(
                    new LcuImageProps($"/lol-game-data/assets/v1/champion-icons/{id}.png", 52)));
            }

            rows.Add(HStack(6, [.. items]));
        }

        return VStack(6, [.. rows]);
    }

    private static void OpenChildWindow()
    {
        ReactorApp.OpenWindow(
            new WindowSpec
            {
                Title = "YuumiX 子窗口",
                Width = 420,
                Height = 260,
                Backdrop = BackdropChoice.Of(BackdropKind.Mica),
                StartPosition = WindowStartPosition.CenterOnCurrent,
            },
            () => new ChildWindowProbe());
    }

    private static Element Section(string title, string note, Element content) =>
        Border(
            VStack(12,
                VStack(4,
                    Subtitle(title)
                        .HeadingLevel(AutomationHeadingLevel.Level2),
                    Caption(note)),
                content)
        ).Padding(16)
         .CornerRadius(YuumiTheme.OverlayRadius)
         .Background(Theme.CardBackground)
         .WithBorder(Theme.CardStroke, 1);

    private static Element ThemeSwatch(string label, ThemeRef theme) =>
        VStack(6,
            Border(Empty())
                .Width(56).Height(40)
                .CornerRadius(YuumiTheme.OverlayRadius)
                .Background(theme)
                .WithBorder(Theme.CardStroke, 1),
            Caption(label));

    private static Element ColorSwatch(string label, string color) =>
        VStack(6,
            Border(Empty())
                .Width(56).Height(40)
                .CornerRadius(YuumiTheme.OverlayRadius)
                .Background(color)
                .WithBorder(Theme.CardStroke, 1),
            Caption(label));

    /// <summary>生成 16×16 的纯色 RGBA 像素，避免为验证引入图标文件依赖。</summary>
    private static byte[] BuildTrayPixels()
    {
        const int size = 16;
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            pixels[i * 4 + 0] = 0x00;
            pixels[i * 4 + 1] = 0x9F;
            pixels[i * 4 + 2] = 0xAA;
            pixels[i * 4 + 3] = 0xFF;
        }

        return pixels;
    }
}

/// <summary>子窗口内容：验证 OpenWindow 能独立承载一棵新的元素树。</summary>
internal sealed class ChildWindowProbe : Component
{
    public override Element Render()
    {
        var (clicks, setClicks) = UseState(0);

        return VStack(16,
            Subtitle("子窗口已独立渲染")
                .HeadingLevel(AutomationHeadingLevel.Level2),
            Caption("该窗口拥有自己的状态树，与主窗口互不影响。"),
            // 分析器对插值字符串按钮会误判为 icon-only，显式补 AutomationName
            Button($"独立计数：{clicks}", () => setClicks(clicks + 1))
                .AutomationName("独立计数按钮")
        ).Padding(24);
    }
}
