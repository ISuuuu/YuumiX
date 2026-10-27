using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Services;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Settings.Sections;

/// <summary>
/// 「通用设置」分组的客户端路径卡（partial，R14 行数拆分）：
/// 路径条目编辑、WeGame 启动项与自动检测/浏览入口。
/// </summary>
internal static partial class GeneralSection
{
    private static Element LolPathCard(SettingsUi ui, PathEditUi? pathEdit, Action<int, bool, string> requestPathCommit)
    {
        var config = ui.Config;
        var i18n = ui.Services.I18n;
        var lolPathStatus = config.General.LolPath.Count == 0
            ? i18n.T("settings.pathNotSet", "未配置")
            : i18n.T("settings.pathSetCount", "已设置 {count} 个路径").Replace("{count}", config.General.LolPath.Count.ToString());

        return SettingsCardKit.Expander(
            i18n.T("settings.lolPathGroup", "英雄联盟客户端路径"),
            i18n.T("settings.lolPathDesc", "设置英雄联盟客户端所在目录的路径"),
            config.General.LolPath.Count == 0 ? SettingsStatus.Unconfigured : SettingsStatus.Custom,
            headerControl: null,
            innerContent: LolPathEditor(ui, pathEdit, requestPathCommit),
            statusText: lolPathStatus);
    }

    // 路径条目行：普通条目 = 可编辑输入框 + 删除；WeGame 条目 = 徽章 + WegamePath 输入框 + 删除。
    // 输入框值优先取编辑草稿（防抖期间的重渲染不能打断正在输入的文本）。
    private static Element PathRow(SettingsUi ui, PathEditUi? pathEdit, Action<int, bool, string> requestPathCommit, int index, string path)
    {
        var config = ui.Config;
        var isWeGame = path == AppConfig.WeGameMarker;
        string textValue;
        if (pathEdit is { Index: var editIndex, IsWeGame: var editIsWeGame, Draft: var editDraft }
            && editIndex == index && editIsWeGame == isWeGame)
        {
            textValue = editDraft ?? string.Empty;
        }
        else
        {
            textValue = isWeGame ? config.General.WegamePath ?? string.Empty : path;
        }

        Element input = isWeGame
            ? TextBox(textValue, v => requestPathCommit(index, true, v), ui.Services.I18n.T("settings.wegameDirPlaceholder", "WeGame 安装目录"))
                .AutomationName("WeGame 路径")
            : TextBox(textValue, v => requestPathCommit(index, false, v), ui.Services.I18n.T("settings.clientDirPlaceholder", "英雄联盟客户端安装目录"))
                .AutomationName($"客户端路径 {index + 1}");

        var rowChildren = new List<Element>();
        if (isWeGame)
        {
            rowChildren.Add(
                Border(
                    Caption("WeGame")
                        .SemiBold()
                        .Foreground(Theme.AccentText)
                )
                .Padding(new Thickness(8, 4, 8, 4))
                .CornerRadius(YuumiTheme.ControlRadius)
                .Background(Theme.Accent)
                .VAlign(VerticalAlignment.Center));
        }

        rowChildren.Add(input.HAlign(HorizontalAlignment.Stretch).Grid(column: 0));
        rowChildren.Add(
            Button("✕", () =>
            {
                // 删除 WeGame 条目时联动清空 WegamePath（对齐旧 handleRemovePath）
                var removed = config.General.LolPath[index];
                config.General.LolPath.RemoveAt(index);
                if (removed == AppConfig.WeGameMarker)
                {
                    config.General.WegamePath = null;
                }

                ui.Commit(nameof(GeneralConfig.LolPath));
            })
                .Padding(new Thickness(4, 0, 4, 0))
                .MinWidth(0)
                .AutomationName($"删除路径 {index + 1}")
                .VAlign(VerticalAlignment.Center)
                .Grid(column: 1));

        return Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [.. rowChildren])
            .Padding(new Thickness(0, 4, 0, 4))
            .WithKey($"path-{index}-{path}");
    }

    private static Element LolPathEditor(SettingsUi ui, PathEditUi? pathEdit, Action<int, bool, string> requestPathCommit)
    {
        var config = ui.Config;
        var rows = new List<Element>();
        for (var i = 0; i < config.General.LolPath.Count; i++)
        {
            rows.Add(PathRow(ui, pathEdit, requestPathCommit, i, config.General.LolPath[i]));
        }

        var content = new List<Element>
        {
            // 空列表提示（对齐旧 pathEmpty 文案）
            rows.Count == 0
                ? Caption(ui.Services.I18n.T("settings.pathEmpty", "暂无客户端路径")).Foreground(Theme.SecondaryText).HAlign(HorizontalAlignment.Center).Margin(top: 4, bottom: 8)
                : VStack(0, [.. rows]),
        };

        // 说明文案
        content.Add(Caption(ui.Services.I18n.T("settings.lolPathHint", "启动时将自动检测并提取客户端锁文件凭据；支持配置多个安装目录"))
            .Foreground(Theme.SecondaryText)
            .Margin(top: 4, bottom: 8));

        // 操作按钮行：自动检测 / 浏览文件夹 / 添加 WeGame 启动项
        content.Add(HStack(8,
            Button(ui.Services.I18n.T("settings.detectBtn", "自动检测"), async () =>
            {
                var detected = await Task.Run(() => ui.Services.Services
                    .GetRequiredService<LolPathDetector>()
                    .DetectLolPath());
                if (detected is null)
                {
                    ui.Toast.Error(ui.Services.I18n.T("settings.lolNotRunning", "未检测到运行中的英雄联盟客户端"));
                    return;
                }

                if (config.General.LolPath.Contains(detected, StringComparer.Ordinal))
                {
                    ui.Toast.Info(ui.Services.I18n.T("settings.pathAlreadyExists", "该路径已存在"));
                    return;
                }

                config.General.LolPath.Add(detected);
                ui.Commit(nameof(GeneralConfig.LolPath));
                ui.Toast.Success(ui.Services.I18n.T("settings.pathAdded", "已添加: {path}").Replace("{path}", detected));
            }).AutomationName("自动检测客户端路径"),
            Button(ui.Services.I18n.T("settings.browseBtn", "浏览文件夹"), async () =>
            {
                var picked = await Services.FolderPickerService.PickFolderAsync();
                if (picked is null)
                {
                    return;
                }

                // 与路径列表其余条目同格式：正斜杠 + 盘符大写
                var normalized = LolPathDetector.NormalizePath(picked);
                if (config.General.LolPath.Contains(normalized, StringComparer.Ordinal))
                {
                    ui.Toast.Info(ui.Services.I18n.T("settings.pathAlreadyExists", "该路径已存在"));
                    return;
                }

                config.General.LolPath.Add(normalized);
                ui.Commit(nameof(GeneralConfig.LolPath));
                ui.Toast.Success(ui.Services.I18n.T("settings.pathAdded", "已添加: {path}").Replace("{path}", normalized));
            }).AutomationName("浏览客户端文件夹"),
            Button(ui.Services.I18n.T("settings.wegameAddBtn", "添加 WeGame 启动项"), async () =>
            {
                if (config.General.LolPath.Contains(AppConfig.WeGameMarker))
                {
                    ui.Toast.Info(ui.Services.I18n.T("settings.wegameAlreadyExists", "WeGame 启动项已存在"));
                    return;
                }

                var detected = await Task.Run(() => ui.Services.Services
                    .GetRequiredService<LolPathDetector>()
                    .DetectWeGameDirectory());
                if (detected is null)
                {
                    ui.Toast.Error(ui.Services.I18n.T("settings.wegameNotInstalled", "未检测到 WeGame 安装路径，请先安装 WeGame"));
                    return;
                }

                config.General.LolPath.Add(AppConfig.WeGameMarker);
                config.General.WegamePath = detected;
                ui.Commit(nameof(GeneralConfig.LolPath));
                ui.Toast.Success(ui.Services.I18n.T("settings.wegameAdded", "已添加 WeGame 启动项") + $" ({detected})");
            }).AutomationName("添加 WeGame 启动项")).HAlign(HorizontalAlignment.Right));

        return VStack(4, [.. content]);
    }
}
