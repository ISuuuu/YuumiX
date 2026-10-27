using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Home;

/// <summary>
/// 主页视图：根据 LCU 连接状态呈现。
///
/// 连接成功时对齐 docs/pic/首页-已连接.png：
///   - 居中标题：客户端连接成功 🎉
///   - 居中卡片：PID = {pid} / --app-port = {port}
///   - 右下角可爱的 Yuumi Logo
///
/// 未连接时对齐原 Home.vue：
///   - 居中品牌 Logo
///   - 客户端未连接提示与描述
///   - 启动客户端按钮与等待客户端启动状态环
/// </summary>
public sealed class HomePage : Component
{
    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var lcu = services.Services.GetRequiredService<LcuConnection>();

        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = theme.Subscribe(setPalette);
                return () => subscription.Dispose();
            },
            0);

        var (lang, setLang) = UseState(services.I18n.CurrentLanguage, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = services.I18n.CurrentLanguageChanged.Subscribe(setLang);
                return () => subscription.Dispose();
            },
            0);

        var (connState, setConnState) = UseState(lcu.CurrentState, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = lcu.States.Subscribe(setConnState);
                return () => subscription.Dispose();
            },
            0);

        var isConnected = connState.Status == LcuConnectionStatus.Connected;
        if (!isConnected)
        {
            // 与生涯 / 战绩查询 / 对局信息共用同一套离线布局（Controls/LcuOfflineState），
            // 只是主页多出可用的启动入口与等待指示。
            return LcuOfflineState.RenderHome(() => LaunchClient(services));
        }

        return Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                RenderConnectedView(connState, palette),
            ]
        )
        .Padding(32);
    }

    /// <summary>
    /// 连接成功态视图：对齐 docs/pic/首页-已连接.png
    /// </summary>
    private static Element RenderConnectedView(LcuConnectionState connState, YuumiPalette palette)
    {
        var creds = connState.Credentials;
        var pid = creds?.ProcessId ?? 0;
        var port = creds?.Port ?? 0;
        var i18n = AppHost.Services?.I18n;

        var centerContent = VStack(
            24,
            // 居中大标题
            Title(i18n?.T("home.connected", "客户端连接成功 🎉") ?? "客户端连接成功 🎉")
                .HorizontalAlignment(HorizontalAlignment.Center),

            // 居中卡片展示 PID 与 Port
            Border(
                VStack(
                    8,
                    // PID 行
                    HStack(
                        4,
                        Body("PID = ")
                            .FontFamily(Icons.MonoFontFamily)
                            .Foreground(Theme.SecondaryText),
                        BodyStrong($"{pid}")
                            .FontFamily(Icons.MonoFontFamily))
                    .HorizontalAlignment(HorizontalAlignment.Center),

                    // Port 行
                    HStack(
                        4,
                        Body("--app-port = ")
                            .FontFamily(Icons.MonoFontFamily)
                            .Foreground(Theme.SecondaryText),
                        BodyStrong($"{port}")
                            .FontFamily(Icons.MonoFontFamily))
                    .HorizontalAlignment(HorizontalAlignment.Center)
                )
                .Padding(32, 20)
            )
            .Width(440)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .HorizontalAlignment(HorizontalAlignment.Center)
        )
        .HorizontalAlignment(HorizontalAlignment.Center)
        .VerticalAlignment(VerticalAlignment.Center);

        // 右下角猫咪 Logo
        var cornerLogo = Image(AppAssets.LogoUri)
            .Width(130)
            .Height(130)
            .Opacity(0.85)
            .AutomationName("Yuumi Logo")
            .HorizontalAlignment(HorizontalAlignment.Right)
            .VerticalAlignment(VerticalAlignment.Bottom)
            .Margin(0, 0, 36, 36);

        return Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                centerContent,
                cornerLogo,
            ]
        );
    }

    /// <summary>
    /// 启动客户端：走 Core 的 <see cref="LolClientLauncher"/>（旧 launch_lol_client 语义），
    /// 含 WeGame 条目分支、TCLS/Riot 启动形态与 740/5 错误的 UAC 提权兜底。
    /// 进程扫描是阻塞调用，放后台执行；失败不弹窗——旧版 Home.vue 亦只 console.warn，
    /// 界面上的「等待客户端启动中」指示环承担反馈职责。
    /// </summary>
    private static void LaunchClient(AppServices services)
    {
        var launcher = services.Services.GetRequiredService<YuumiX.Core.Features.Tools.LolClientLauncher>();
        var logger = services.LoggerFactory.CreateLogger<YuumiX.Core.Features.Tools.LolClientLauncher>();
        var config = services.Config;
        _ = Task.Run(() =>
        {
            string? error = launcher.Launch(config.General.LolPath, config.General.WegamePath);
            if (error is not null)
            {
                logger.LogWarning("手动启动客户端失败: {Error}", error);
            }
        });
    }
}
