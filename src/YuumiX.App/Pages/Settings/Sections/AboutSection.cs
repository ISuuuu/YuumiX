using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Settings.Sections;

/// <summary>设置页「关于」分组：版本、存储位置与开源仓库入口。</summary>
internal static class AboutSection
{
    /// <summary>开源仓库地址（工程 git remote 与 README 一致）。</summary>
    private const string RepositoryUrl = "https://github.com/ISuuuu/YuumiX";

    /// <summary>应用版本（入口程序集的文件版本，显示为 v主.次.修订）。</summary>
    private static string AppVersion =>
        "v" + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0");

    public static Element Render(SettingsUi ui)
    {
        return SettingsCardKit.Expander(
            "关于 YuumiX",
            $"当前版本 {AppVersion} · 运行环境与存储位置",
            SettingsStatus.Custom,
            headerControl: null,
            innerContent: VStack(10,
                SettingsVisuals.InfoRow("应用版本", AppVersion),
                SettingsVisuals.InfoRow("配置版本", $"v{ui.Config.Version}"),
                SettingsVisuals.InfoRow("配置文件", ui.Services.ConfigStore.FilePath),
                SettingsVisuals.InfoRow("数据目录", ui.Services.Paths.DataDirectory),
                SettingsVisuals.InfoRow("便携模式", SettingsVisuals.OnOff(ui.Services.Paths.IsPortable)),
                SettingsVisuals.InfoRow("日志目录", ui.Services.Paths.LogDirectory),
                SettingsVisuals.InfoRow("写回备份", $"{ui.Services.ConfigStore.ListBackups().Count} 份（保留最近 {ConfigStore.MaxBackups} 份）"),
                HStack(8,
                    Button("开源仓库", () =>
                    {
                        try
                        {
                            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = RepositoryUrl,
                                UseShellExecute = true,
                            });
                        }
                        catch (Exception ex)
                        {
                            ui.Logger.LogWarning(ex, "打开开源仓库页面失败");
                        }
                    }).AutomationName("打开开源仓库"))),
            statusText: AppVersion);
    }
}
