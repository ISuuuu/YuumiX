using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Theming;
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

        var (connState, setConnState) = UseState(lcu.CurrentState, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = lcu.States.Subscribe(setConnState);
                return () => subscription.Dispose();
            },
            0);

        var isConnected = connState.Status == LcuConnectionStatus.Connected;

        return Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                isConnected
                    ? RenderConnectedView(connState, palette)
                    : RenderDisconnectedView(services.Config.General.LolPath, palette),
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

        var centerContent = VStack(
            24,
            // 居中大标题
            TextBlock("客户端连接成功 🎉")
                .FontSize(28)
                .FontWeight(FontWeights.Bold)
                .HorizontalAlignment(HorizontalAlignment.Center),

            // 居中卡片展示 PID 与 Port
            Border(
                VStack(
                    8,
                    // PID 行
                    HStack(
                        4,
                        TextBlock("PID = ")
                            .FontFamily("Consolas, SF Mono, monospace")
                            .FontSize(15)
                            .Foreground(Theme.SecondaryText),
                        TextBlock($"{pid}")
                            .FontFamily("Consolas, SF Mono, monospace")
                            .FontSize(15)
                            .FontWeight(FontWeights.SemiBold)
                    )
                    .HorizontalAlignment(HorizontalAlignment.Center),

                    // Port 行
                    HStack(
                        4,
                        TextBlock("--app-port = ")
                            .FontFamily("Consolas, SF Mono, monospace")
                            .FontSize(15)
                            .Foreground(Theme.SecondaryText),
                        TextBlock($"{port}")
                            .FontFamily("Consolas, SF Mono, monospace")
                            .FontSize(15)
                            .FontWeight(FontWeights.SemiBold)
                    )
                    .HorizontalAlignment(HorizontalAlignment.Center)
                )
                .Padding(32, 22)
            )
            .Width(440)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .CornerRadius(12)
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
    /// 未连接态视图：对齐原 Home.vue
    /// </summary>
    private static Element RenderDisconnectedView(IReadOnlyList<string> lolPaths, YuumiPalette palette)
    {
        return VStack(
            16,
            // 居中 Logo
            Image(AppAssets.LogoUri)
                .Width(96)
                .Height(96)
                .AutomationName("Yuumi Logo")
                .HorizontalAlignment(HorizontalAlignment.Center)
                .Margin(0, 0, 0, 4),

            // 标题
            TextBlock("客户端未连接")
                .FontSize(24)
                .FontWeight(FontWeights.Bold)
                .HorizontalAlignment(HorizontalAlignment.Center),

            // 说明文案
            TextBlock("请启动英雄联盟客户端，连接成功后将自动识别并加载数据")
                .FontSize(13)
                .Foreground(Theme.SecondaryText)
                .HorizontalAlignment(HorizontalAlignment.Center)
                .Margin(0, 0, 0, 8),

            // 启动客户端按钮
            Button("启动客户端", () => LaunchClient(lolPaths))
                .HorizontalAlignment(HorizontalAlignment.Center)
                .Padding(24, 8)
                .Margin(0, 0, 0, 10),

            // 等待连接环
            HStack(
                10,
                ProgressRing()
                    .Width(16)
                    .Height(16)
                    .IsActive(true),
                TextBlock("等待客户端启动中...")
                    .FontSize(13)
                    .Foreground(Theme.SecondaryText)
            )
            .HorizontalAlignment(HorizontalAlignment.Center)
        )
        .HorizontalAlignment(HorizontalAlignment.Center)
        .VerticalAlignment(VerticalAlignment.Center);
    }

    private static void LaunchClient(IReadOnlyList<string> lolPaths)
    {
        if (lolPaths.Count == 0) return;
        var path = lolPaths[0];
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch
        {
            // 忽略启动失败
        }
    }
}
