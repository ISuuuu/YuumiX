using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Settings.Sections;

/// <summary>
/// 设置页「日志与网络」分组：日志等级 / 日志文件 / 软件 HTTP 代理。
/// 代理地址草稿由页面以 UseRef 持有（刻意不参与渲染，避免受控文本框光标跳动）。
/// </summary>
internal static class LogNetworkSection
{
    private static readonly string[] LogLevelLabels = ["调试（Debug）", "信息（Info）", "错误（Error）"];
    private static readonly int[] LogLevelValues = [0, 1, 2];

    public static Element Render(SettingsUi ui, Microsoft.UI.Reactor.Core.Ref<string?> proxyDraft)
    {
        var config = ui.Config;

        var logLevelCard = SettingsCardKit.Card(
            "日志等级", "写入 <exe>/log 的日志记录详细程度，修改后立即生效",
            ComboBox(LogLevelLabels, SettingsVisuals.IndexOf(LogLevelValues, config.General.LogLevel, LogLevelValues.Length - 1), index =>
            {
                var level = LogLevelValues[index];
                config.General.LogLevel = level;
                ui.Services.ApplyLogLevel(level);
                ui.Commit(nameof(GeneralConfig.LogLevel));
            }).Width(160).AutomationName("日志等级选择"));

        var logDirCard = SettingsCardKit.Card(
            "日志文件", $"存放于 {ui.Services.Paths.LogDirectory}",
            Button("打开目录", () =>
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", ui.Services.Paths.LogDirectory);
                }
                catch (Exception ex)
                {
                    ui.Logger.LogWarning(ex, "打开日志目录失败");
                }
            }).AutomationName("打开日志目录"));

        var httpProxyCard = SettingsCardKit.Expander(
            "软件 HTTP 代理",
            "软件更新、TFT 数据、OP.GG 服务访问走此 HTTP 代理通道",
            config.General.EnableHttpProxy ? SettingsStatus.Enabled : SettingsStatus.Disabled,
            headerControl: ToggleSwitch(config.General.EnableHttpProxy, v =>
            {
                if (v == config.General.EnableHttpProxy) return;
                config.General.EnableHttpProxy = v;
                ui.Commit(nameof(GeneralConfig.EnableHttpProxy));
            }, header: null, onContent: null, offContent: null)
                .AutomationName("HTTP代理开关"),
            innerContent: SettingsCardKit.Card("代理地址", "形如 127.0.0.1:7897；开关关闭时不可编辑", ProxyAddressRow(ui, proxyDraft)));

        return VStack(8, logLevelCard, logDirCard, httpProxyCard);
    }

    private static Element ProxyAddressRow(SettingsUi ui, Microsoft.UI.Reactor.Core.Ref<string?> proxyDraft)
    {
        static bool LooksValid(string value) =>
            value.Length == 0
            || (value.Contains(':') && !value.Contains(' ') && Uri.TryCreate("http://" + value, UriKind.Absolute, out _));

        var config = ui.Config;
        return HStack(8,
            // 开关关闭时禁用输入（对齐旧 ProxySection 的 disabled 联动）
            TextBox(proxyDraft.Current ?? config.General.HttpProxyAddr, v => proxyDraft.Current = v)
                .Width(240)
                .IsEnabled(config.General.EnableHttpProxy)
                .AutomationName("代理地址"),
            Button("应用", () =>
            {
                var next = (proxyDraft.Current ?? config.General.HttpProxyAddr).Trim();
                if (!LooksValid(next))
                {
                    ui.SetError("代理地址格式不对，应形如 127.0.0.1:7897");
                    return;
                }

                if (next == config.General.HttpProxyAddr)
                {
                    return;
                }

                config.General.HttpProxyAddr = next;
                ui.Commit(nameof(GeneralConfig.HttpProxyAddr));
            })
            .IsEnabled(config.General.EnableHttpProxy)
            .AutomationName("应用代理"));
    }
}
