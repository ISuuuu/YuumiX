using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.Core.Automation;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Settings.Sections;

/// <summary>设置页「高级与隐藏」分组：隐藏菜单 / 多杀自动截图。</summary>
internal static class AdvancedSection
{
    public static Element Render(SettingsUi ui)
    {
        var config = ui.Config;
        var menu = ui.Menu;
        var i18n = ui.Services.I18n;

        var hideMenuEnabled = config.Functions.HideTft || config.Functions.HideSavedPlayers;
        var hideMenuCard = SettingsCardKit.Expander(
            i18n.T("settings.hideMenuTitle", "隐藏菜单"),
            i18n.T("settings.hideMenuDesc", "控制侧边栏云顶之弈与路人集功能入口的显隐"),
            hideMenuEnabled ? SettingsStatus.Enabled : SettingsStatus.Disabled,
            // 两项入口的公共开关：关掉即同时显示两个入口，单项仍可在展开区单独控制。
            headerControl: SettingsCardKit.Switch(hideMenuEnabled, v =>
            {
                config.Functions.HideTft = v;
                config.Functions.HideSavedPlayers = v;
                menu.Apply(v, v);
                ui.Commit(nameof(FunctionsConfig.HideTft));
            }, "隐藏菜单开关"),
            innerItems:
            [
                SettingsCardKit.Card(
                    i18n.T("settings.hideMenuOptionTft", "隐藏云顶之弈入口"),
                    i18n.T("settings.hideTftDesc", "侧边栏不显示云顶之弈功能入口"),
                    SettingsCardKit.Switch(config.Functions.HideTft, v =>
                    {
                        config.Functions.HideTft = v;
                        menu.Apply(config.Functions.HideTft, config.Functions.HideSavedPlayers);
                        ui.Commit(nameof(FunctionsConfig.HideTft));
                    }, "隐藏云顶之弈")),
                SettingsCardKit.Card(
                    i18n.T("settings.hideMenuOptionSavedPlayers", "隐藏路人集入口"),
                    i18n.T("settings.hideSavedPlayersDesc", "侧边栏不显示路人集功能入口"),
                    SettingsCardKit.Switch(config.Functions.HideSavedPlayers, v =>
                    {
                        config.Functions.HideSavedPlayers = v;
                        menu.Apply(config.Functions.HideTft, config.Functions.HideSavedPlayers);
                        ui.Commit(nameof(FunctionsConfig.HideSavedPlayers));
                    }, "隐藏路人集")),
            ]);

        var screenshotCard = SettingsCardKit.Expander(
            i18n.T("settings.screenshotGroup", "多杀自动截图"),
            i18n.T("settings.enableScreenshotOnMultikillDesc", "对局中达成多杀或超神时自动保存精彩瞬间"),
            config.Functions.EnableScreenshotOnMultikill ? SettingsStatus.Enabled : SettingsStatus.Disabled,
            headerControl: SettingsCardKit.Switch(config.Functions.EnableScreenshotOnMultikill, v =>
            {
                config.Functions.EnableScreenshotOnMultikill = v;
                ui.Commit(nameof(FunctionsConfig.EnableScreenshotOnMultikill));
            }, "多杀截图开关"),
            // 开关关闭时子项不可编辑（对齐旧 ScreenshotSection.vue 的「开启后才显示」）
            innerItems:
            [
                SettingsCardKit.Card(
                    i18n.T("settings.screenshotLevelsTitle", "截图触发事件"),
                    i18n.T("settings.screenshotLevelsDesc", "达成以下任一事件时保存截图"),
                    HStack(12,
                        ScreenshotLevelCheckBox(ui, 3, i18n.T("settings.tripleKill", "三杀")),
                        ScreenshotLevelCheckBox(ui, 4, i18n.T("settings.quadraKill", "四杀")),
                        ScreenshotLevelCheckBox(ui, 5, i18n.T("settings.pentaKill", "五杀")),
                        ScreenshotLevelCheckBox(ui, 8, i18n.T("settings.legendary", "超神"))))
                    .IsEnabled(config.Functions.EnableScreenshotOnMultikill),
                SettingsCardKit.Card(
                    i18n.T("settings.screenshotSavePathTitle", "截图保存位置"),
                    i18n.T("settings.screenshotSavePathDesc", "截图文件写入的目录"),
                    ScreenshotSavePathRow(ui))
                    .IsEnabled(config.Functions.EnableScreenshotOnMultikill),
            ]);

        return VStack(8, hideMenuCard, screenshotCard);
    }

    // 截图触发事件复选组：3=三杀 4=四杀 5=五杀 8=超神（写入 ScreenshotOnMultikillLevels）
    private static Element ScreenshotLevelCheckBox(SettingsUi ui, int level, string label)
    {
        var levels = ui.Config.Functions.ScreenshotOnMultikillLevels;
        return CheckBox(
            levels.Contains(level),
            v =>
            {
                var changed = false;
                if (v && !levels.Contains(level))
                {
                    levels.Add(level);
                    changed = true;
                }
                else if (!v && levels.Contains(level))
                {
                    levels.Remove(level);
                    changed = true;
                }

                if (changed)
                {
                    ui.Commit(nameof(FunctionsConfig.ScreenshotOnMultikillLevels));
                }
            },
            label).AutomationName(label);
    }

    private static Element ScreenshotSavePathRow(SettingsUi ui)
    {
        var config = ui.Config;
        var i18n = ui.Services.I18n;

        return HStack(8,
            // 只读展示（旧版 n-input readonly 同款），placeholder 提示默认位置
            TextBox(config.Functions.ScreenshotSavePath, _ => { }, i18n.T("settings.screenshotPathPlaceholder", "默认保存在系统「图片」目录下的 Yuumi_Screenshots 文件夹"))
                .Width(240)
                .IsReadOnly()
                .AutomationName("截图保存目录"),
            Button(ui.Services.I18n.T("settings.browseBtn", "浏览"), async () =>
            {
                var picked = await Services.FolderPickerService.PickFolderAsync();
                if (picked is null)
                {
                    return;
                }

                config.Functions.ScreenshotSavePath = LolPathDetector.NormalizePath(picked);
                ui.Commit(nameof(FunctionsConfig.ScreenshotSavePath));
                ui.Toast.Success(i18n.T("settings.screenshotPathUpdated", "已成功更新截图保存目录"));
            }).AutomationName("浏览截图目录"),
            Button(ui.Services.I18n.T("settings.openScreenshotFolderBtn", "打开目录"), () =>
            {
                try
                {
                    // 与旧 open_screenshot_folder 一致：目录不存在时自动创建
                    var dir = LiveEventMatcher.ResolveSaveDirectory(config.Functions.ScreenshotSavePath);
                    Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start("explorer.exe", dir);
                }
                catch (Exception ex)
                {
                    ui.Logger.LogWarning(ex, "打开截图目录失败");
                    ui.Toast.Error(i18n.T("settings.screenshotPathError", "无法打开截图文件夹") + $": {ex.Message}");
                }
            }).AutomationName("打开截图目录"));
    }
}
