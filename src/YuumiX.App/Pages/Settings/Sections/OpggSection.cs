using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Windowing;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Settings.Sections;

/// <summary>设置页「OP.GG」分组：自动显示窗口 / 置顶窗口。</summary>
internal static class OpggSection
{
    public static Element Render(SettingsUi ui)
    {
        var config = ui.Config;

        return VStack(8,
            SettingsCardKit.Toggle("自动显示 OP.GG 窗口", "在英雄选择开始时自动显示 OP.GG 窗口", config.Functions.AutoShowOpgg, v =>
            {
                config.Functions.AutoShowOpgg = v;
                ui.Commit(nameof(FunctionsConfig.AutoShowOpgg));
            }),
            SettingsCardKit.Toggle("置顶 OP.GG 窗口", "在英雄选择时将 OP.GG 窗口置顶", config.Functions.EnableOpggOnTop, v =>
            {
                config.Functions.EnableOpggOnTop = v;
                OpggWindowHost.UpdateAlwaysOnTop(v);
                ui.Commit(nameof(FunctionsConfig.EnableOpggOnTop));
            }));
    }
}
