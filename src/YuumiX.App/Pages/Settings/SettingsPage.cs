using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Settings;

/// <summary>
/// 设置页（阶段 5 前半：只读渲染）。
///
/// 本轮刻意只做「读」：配置写回会动 <c>%APPDATA%/Yuumi/config.json</c>，
/// 而那份文件与原版 Yuumi 共用。先把字段映射、默认值语义、主题展示做对，
/// 再单独一轮做编辑与写回，出问题时才能一眼分辨是「显示错」还是「写坏」。
///
/// 字段来源：<see cref="AppConfig"/> 的四节（General / Personalization / Functions / Other），
/// 与原 <c>src/views/Settings.vue</c> 的分组一一对应。
/// </summary>
public sealed class SettingsPage : Component
{
    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var config = services.Config;

        return ScrollView(
            VStack(20,
                VStack(4,
                    Heading("设置"),
                    Caption("对应原实现：src/views/Settings.vue")),

                ReadOnlyNotice(),

                Section("常规",
                    "客户端路径、启动行为与日志",
                    VStack(10,
                        Row("英雄联盟路径", JoinPaths(config.General.LolPath)),
                        Row("WeGame 路径", OrDash(config.General.WegamePath)),
                        Row("随应用启动客户端", OnOff(config.General.EnableStartLolWithApp)),
                        Row("关闭到托盘", TriState(config.General.EnableCloseToTray)),
                        Row("游戏开始时最小化", OnOff(config.General.EnableGameStartMinimize)),
                        Row("检查更新", OnOff(config.General.EnableCheckUpdate)),
                        Row("日志等级", LogLevelName(config.General.LogLevel)),
                        Row("HTTP 代理", config.General.EnableHttpProxy ? config.General.HttpProxyAddr : "未启用"))),

                Section("个性化",
                    "主题、语言与战绩卡片配色",
                    VStack(12,
                        HStack(12,
                            TextBlock("主题色").Width(220),
                            Border(Empty())
                                .Width(56).Height(24)
                                .CornerRadius(4)
                                .Background(config.Personalization.ThemeColor)
                                .WithBorder(Theme.CardStroke, 1),
                            TextBlock(config.Personalization.ThemeColor)
                                .Foreground(Theme.SecondaryText)),
                        Row("主题模式", config.Personalization.ThemeMode),
                        Row("语言", config.Personalization.Language),
                        Row("缩放", config.Personalization.DpiScale),
                        Row("Mica 背景", OnOff(config.Personalization.MicaEnabled)),
                        ColorRow("胜利卡片", config.Personalization.WinCardColor),
                        ColorRow("失败卡片", config.Personalization.LoseCardColor),
                        ColorRow("重赛卡片", config.Personalization.RemakeCardColor))),

                Section("功能开关",
                    "自动化与数据抓取的关键参数（展示当前值）",
                    VStack(10,
                        Row("生涯战绩场数", config.Functions.CareerGamesNumber.ToString()),
                        Row("LCU 接口并发", config.Functions.ApiConcurrencyNumber.ToString()),
                        Row("自动接受对局", OnOff(config.Functions.EnableAutoAcceptMatching)),
                        Row("自动重连", OnOff(config.Functions.EnableAutoReconnect)),
                        Row("自动选人", OnOff(config.Functions.EnableAutoSelectChampion)),
                        Row("自动禁用", OnOff(config.Functions.EnableAutoBanChampion)),
                        Row("大乱斗悬浮窗", OnOff(config.Functions.EnableBenchOverlay)),
                        Row("截图保存目录", OrDash(config.Functions.ScreenshotSavePath)))),

                Section("关于",
                    "运行环境与数据位置",
                    VStack(10,
                        Row("配置版本", $"v{config.Version}"),
                        Row("数据目录", services.Paths.DataDirectory),
                        Row("便携模式", OnOff(services.Paths.IsPortable)),
                        Row("日志目录", services.Paths.LogDirectory)))
            ).Padding(28).HAlign(HorizontalAlignment.Stretch)
        );
    }

    /// <summary>只读提示。写回在下一轮交付，这里先说明清楚，避免被当成「保存无效」的缺陷。</summary>
    private static Element ReadOnlyNotice() =>
        Border(
            VStack(6,
                TextBlock("当前为只读预览").FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
                Caption("字段已接入真实配置；编辑与写回（含写前备份）将在下一轮实现。")))
        .Padding(14)
        .CornerRadius(8)
        .Background(Theme.CardBackground)
        .WithBorder(Theme.CardStroke, 1);

    private static Element Section(string title, string note, Element content) =>
        Border(
            VStack(12,
                VStack(4,
                    SubHeading(title),
                    Caption(note)),
                content)
        ).Padding(18)
         .CornerRadius(8)
         .Background(Theme.CardBackground)
         .WithBorder(Theme.CardStroke, 1);

    private static Element Row(string label, string value) =>
        HStack(12,
            TextBlock(label).Width(220),
            TextBlock(value).Foreground(Theme.SecondaryText).TextWrapping());

    private static Element ColorRow(string label, string color) =>
        HStack(12,
            TextBlock(label).Width(220),
            Border(Empty())
                .Width(56).Height(24)
                .CornerRadius(4)
                .Background(color)
                .WithBorder(Theme.CardStroke, 1),
            TextBlock(color).Foreground(Theme.SecondaryText));

    private static string OnOff(bool value) => value ? "已开启" : "已关闭";

    /// <summary>三元开关：老配置里没有这个字段时是 <c>null</c>，UI 必须区分「关」与「未表态」。</summary>
    private static string TriState(bool? value) => value switch
    {
        true => "已开启",
        false => "已关闭",
        null => "未设置（按平台默认）",
    };

    private static string LogLevelName(int level) => level switch
    {
        0 => "Debug",
        2 => "Error",
        _ => "Info",
    };

    private static string JoinPaths(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? "未配置" : string.Join(" ; ", paths);

    private static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "未配置" : value;
}
