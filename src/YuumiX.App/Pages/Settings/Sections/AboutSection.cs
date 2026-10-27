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

    /// <summary>
    /// 应用版本，显示为 <c>v主.次.修订[-预发布]</c>。
    /// 复用 <see cref="YuumiX.Core.Updates.UpdateService.CurrentVersion"/> 而不是自己读
    /// <c>Assembly.Version</c>：后者只有数字段，预发布版本会显示成正式版，与更新检查看到的版本号不一致。
    /// </summary>
    private static string AppVersion => "v" + YuumiX.Core.Updates.UpdateService.CurrentVersion;

    public static Element Render(SettingsUi ui)
    {
        var i18n = ui.Services.I18n;

        return SettingsCardKit.Expander(
            i18n.T("settings.aboutTitle", "关于 YuumiX"),
            $"当前版本 {AppVersion} · 运行环境与存储位置",
            SettingsStatus.Custom,
            headerControl: null,
            innerContent: VStack(12,
                SettingsVisuals.InfoRow(i18n.T("settings.appVersion", "应用版本"), AppVersion),
                SettingsVisuals.InfoRow(i18n.T("settings.configVersion", "配置版本"), $"v{ui.Config.Version}"),
                SettingsVisuals.InfoRow(i18n.T("settings.configFile", "配置文件"), ui.Services.ConfigStore.FilePath),
                SettingsVisuals.InfoRow(i18n.T("settings.dataDirectory", "数据目录"), ui.Services.Paths.DataDirectory),
                SettingsVisuals.InfoRow(i18n.T("settings.portableMode", "便携模式"), ui.Config.General.EnableCheckUpdate ? i18n.T("settings.enabled", "已开启") : i18n.T("settings.disabled", "已关闭")),
                SettingsVisuals.InfoRow(i18n.T("settings.logDirectory", "日志目录"), ui.Services.Paths.LogDirectory),
                SettingsVisuals.InfoRow(i18n.T("settings.backups", "写回备份"), $"{ui.Services.ConfigStore.ListBackups().Count} 份（保留最近 {ConfigStore.MaxBackups} 份）"),
                HStack(8,
                    Button(i18n.T("settings.aboutRepository", "开源仓库"), () =>
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
