using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.App.Windowing;
using YuumiX.Core.Common;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Settings.Sections;

/// <summary>
/// <summary>
/// 设置页「外观与个性化」分组：Mica 背景 / 主题模式 / 界面缩放 / 语言设置 / 战绩卡片配色 / 死亡数字配色。
/// 颜色改动统一出口：先落内存与主题状态（界面即时生效），再由页面的防抖回调落盘。
/// </summary>
internal static class AppearanceSection
{
    private static readonly string[] ThemeModeLabels = ["浅色", "深色", "跟随系统"];
    private static readonly string[] ThemeModeValues = ["Light", "Dark", "Auto"];

    private static readonly string[] DpiScaleLabels = ["跟随系统", "100%", "125%", "150%"];
    private static readonly string[] DpiScaleValues = ["Auto", "100", "125", "150"];

    private static readonly string[] LanguageLabels = ["跟随系统", "简体中文", "繁體中文", "English"];
    private static readonly string[] LanguageValues = ["Auto", "zh_CN", "zh_TW", "en_US"];

    public static Element Render(SettingsUi ui, Action<string> requestColorCommit)
    {
        var config = ui.Config;
        var i18n = ui.Services.I18n;

        var themeModeLabels = new[]
        {
            i18n.T("settings.themeModeLight", "浅色"),
            i18n.T("settings.themeModeDark", "深色"),
            i18n.T("settings.themeModeAuto", "跟随系统"),
        };
        var dpiScaleLabels = new[]
        {
            i18n.T("settings.dpiScaleAuto", "跟随系统"),
            "100%", "125%", "150%",
        };
        var languageLabels = new[]
        {
            i18n.T("settings.langAuto", "跟随系统"),
            "简体中文", "繁體中文", "English",
        };

        var themeModeCard = SettingsCardKit.Card(
            i18n.T("settings.themeModeTitle", "主题模式"),
            i18n.T("settings.themeModeDesc", "浅色 / 深色 / 跟随系统主题自适应切换"),
            ComboBox(themeModeLabels, SettingsVisuals.IndexOf(ThemeModeValues, config.Personalization.ThemeMode, ThemeModeValues.Length - 1), index =>
            {
                config.Personalization.ThemeMode = ThemeModeValues[index];
                ui.Theme.Apply(config.Personalization);
                ui.Commit(nameof(PersonalizationConfig.ThemeMode));
            }).Width(160).AutomationName("主题模式选择"));

        var dpiScaleCard = SettingsCardKit.Card(
            i18n.T("settings.dpiScaleTitle", "界面缩放"),
            i18n.T("settings.dpiScaleDesc", "调整应用界面基础显示比例与窗口尺寸"),
            ComboBox(dpiScaleLabels, SettingsVisuals.IndexOf(DpiScaleValues, config.Personalization.DpiScale, 0), index =>
            {
                var scaleValue = DpiScaleValues[index];
                var scaleLabel = dpiScaleLabels[index];
                config.Personalization.DpiScale = scaleValue;
                ui.Commit(nameof(PersonalizationConfig.DpiScale));
                AppHost.Scale.SetScale(scaleValue);

                var window = ui.Window ?? ReactorApp.PrimaryWindow;
                if (window is not null)
                {
                    var (width, height) = WindowMetrics.CalculateWindowSize(scaleValue);
                    WindowMetrics.ApplyScaleAndRestoreIfMaximized(window, scaleValue);
                    ui.Toast.Success($"界面缩放已设为 {scaleLabel}，窗口尺寸已调整为 {width}×{height}");
                }
                else
                {
                    ui.Toast.Success($"界面缩放已设为 {scaleLabel}");
                }
            }).Width(160).AutomationName("界面缩放选择"));

        var languageCard = SettingsCardKit.Card(
            i18n.T("settings.langTitle", "语言设置"),
            i18n.T("settings.langDesc", "选择应用界面显示语言"),
            ComboBox(languageLabels, SettingsVisuals.IndexOf(LanguageValues, config.Personalization.Language, 0), index =>
            {
                var lang = LanguageValues[index];
                config.Personalization.Language = lang;
                ui.Commit(nameof(PersonalizationConfig.Language));
                ui.Services.I18n.SetLanguage(lang);
                ui.Toast.Success(i18n.T("settings.langSaved", "语言已更新并即时生效"));
            }).Width(160).AutomationName("语言选择"));

        return VStack(8,
            SettingsCardKit.Toggle(
                i18n.T("settings.micaTitle", "Mica 背景"),
                i18n.T("settings.micaDesc", "使用系统亚克力/云母材质作为窗口背景 · 即时生效"),
                config.Personalization.MicaEnabled,
                v =>
                {
                    config.Personalization.MicaEnabled = v;
                    ui.Theme.Apply(config.Personalization);
                    ui.Commit(nameof(PersonalizationConfig.MicaEnabled));
                }),
            themeModeCard,
            dpiScaleCard,
            languageCard,
            // 强调色跟随系统，原「主题强调色」卡片已删除；这里只剩业务配色（战绩卡片 / 死亡数字）。
            CardColorEditor(ui, requestColorCommit),
            DeathColorEditor(ui, requestColorCommit));
    }

    /// <summary>颜色改动统一出口：先落到内存配置与主题状态（界面即时生效），再由防抖 effect 落盘。</summary>
    private static void ApplyColor(SettingsUi ui, string field, string value, Action<string> assign, Action<string> requestColorCommit)
    {
        assign(value);
        ui.Theme.Apply(ui.Config.Personalization);
        requestColorCommit(field);
    }

    /// <summary>单个颜色编辑块：色块预览 + 当前值 + 原生取色器。</summary>
    private static Element ColorEditor(
        SettingsUi ui,
        Action<string> requestColorCommit,
        string label,
        string description,
        string current,
        bool withAlpha,
        Func<Windows.UI.Color, string> toStorage,
        Action<string> assign,
        string field,
        string resetValue)
    {
        var normalized = ColorHex.NormalizeArgb(current, resetValue);
        ColorHex.TryParseArgb(current, out var a, out var r, out var g, out var b);

        var picker = ColorPicker(
            Windows.UI.Color.FromArgb(a, r, g, b),
            color =>
            {
                var next = toStorage(color);
                if (ColorHex.NormalizeArgb(next, resetValue) == normalized)
                {
                    return;
                }

                ApplyColor(ui, field, next, assign, requestColorCommit);
            });

        if (withAlpha)
        {
            picker = picker.AlphaEnabled(true);
        }

        return VStack(8,
            Grid(
                [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [
                    SettingsVisuals.ColorSwatch(normalized).Grid(0, 0),
                    BodyStrong(label).VAlign(VerticalAlignment.Center).Margin(left: 8).Grid(0, 1),
                    Caption(normalized).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).Grid(0, 2),
                ]),
            Caption(description).Foreground(Theme.SecondaryText).TextWrapping(),
            picker.MaxWidth(320).HAlign(HorizontalAlignment.Left).AutomationName(label));
    }

    private static Element CardColorEditor(SettingsUi ui, Action<string> requestColorCommit)
    {
        var i18n = ui.Services.I18n;
        return SettingsCardKit.Expander(
            i18n.T("settings.cardColorTitle", "战绩卡片配色"),
            i18n.T("settings.cardColorDesc", "胜利、失败与重开卡片的底色与透明度"),
            SettingsStatus.Custom,
            headerControl: null,
            innerContent: VStack(16,
                (Grid(
                    [GridSize.Star(), GridSize.Star(), GridSize.Star()],
                    [GridSize.Auto],
                    [
                        ColorEditor(
                            ui, requestColorCommit,
                            i18n.T("settings.winCard", "胜利卡片"),
                            "战绩列表里胜利一方的底色（含透明度）",
                            ui.Config.Personalization.WinCardColor,
                            withAlpha: true,
                            c => ColorHex.ToArgb(c.A, c.R, c.G, c.B),
                            v => ui.Config.Personalization.WinCardColor = v,
                            nameof(PersonalizationConfig.WinCardColor),
                            YuumiTheme.WinCard).Grid(0, 0),
                        ColorEditor(
                            ui, requestColorCommit,
                            i18n.T("settings.loseCard", "失败卡片"),
                            "失败一方的底色（含透明度）",
                            ui.Config.Personalization.LoseCardColor,
                            withAlpha: true,
                            c => ColorHex.ToArgb(c.A, c.R, c.G, c.B),
                            v => ui.Config.Personalization.LoseCardColor = v,
                            nameof(PersonalizationConfig.LoseCardColor),
                            YuumiTheme.LoseCard).Grid(0, 1),
                        ColorEditor(
                            ui, requestColorCommit,
                            i18n.T("settings.remakeCard", "重开卡片"),
                            "重赛一方的底色（含透明度）",
                            ui.Config.Personalization.RemakeCardColor,
                            withAlpha: true,
                            c => ColorHex.ToArgb(c.A, c.R, c.G, c.B),
                            v => ui.Config.Personalization.RemakeCardColor = v,
                            nameof(PersonalizationConfig.RemakeCardColor),
                            YuumiTheme.RemakeCard).Grid(0, 2),
                    ]
                ) with { ColumnSpacing = 16 }),
                Button(i18n.T("settings.resetColors", "恢复默认"), () =>
                {
                    ui.Config.Personalization.WinCardColor = YuumiTheme.WinCard;
                    ui.Config.Personalization.LoseCardColor = YuumiTheme.LoseCard;
                    ui.Config.Personalization.RemakeCardColor = YuumiTheme.RemakeCard;
                    ui.Theme.Apply(ui.Config.Personalization);
                    requestColorCommit(nameof(PersonalizationConfig.WinCardColor));
                }).AutomationName("恢复默认")),
            statusText: i18n.T("settings.cardColorStatusSet", "自定义配色"));
    }

    private static Element DeathColorEditor(SettingsUi ui, Action<string> requestColorCommit)
    {
        var i18n = ui.Services.I18n;
        return SettingsCardKit.Expander(
            i18n.T("settings.deathColorTitle", "死亡数字配色"),
            "战绩列表与详情中死亡数字的颜色",
            SettingsStatus.Custom,
            headerControl: null,
            innerContent: VStack(16,
                (Grid(
                    [GridSize.Star(), GridSize.Star()],
                    [GridSize.Auto],
                    [
                        ColorEditor(
                            ui, requestColorCommit,
                            i18n.T("settings.lightMode", "浅色主题"),
                            "浅色主题下每局死亡数的颜色",
                            ui.Config.Personalization.LightDeathsNumberColor,
                            withAlpha: false,
                            c => ColorHex.ToArgb(0xFF, c.R, c.G, c.B),
                            v => ui.Config.Personalization.LightDeathsNumberColor = v,
                            nameof(PersonalizationConfig.LightDeathsNumberColor),
                            YuumiTheme.LightDeath).Grid(0, 0),
                        ColorEditor(
                            ui, requestColorCommit,
                            i18n.T("settings.darkMode", "深色主题"),
                            "深色主题下每局死亡数的颜色",
                            ui.Config.Personalization.DarkDeathsNumberColor,
                            withAlpha: false,
                            c => ColorHex.ToArgb(0xFF, c.R, c.G, c.B),
                            v => ui.Config.Personalization.DarkDeathsNumberColor = v,
                            nameof(PersonalizationConfig.DarkDeathsNumberColor),
                            YuumiTheme.DarkDeath).Grid(0, 1),
                    ]
                ) with { ColumnSpacing = 16 }),
                Button(i18n.T("settings.resetColors", "恢复默认"), () =>
                {
                    ui.Config.Personalization.LightDeathsNumberColor = YuumiTheme.LightDeath;
                    ui.Config.Personalization.DarkDeathsNumberColor = YuumiTheme.DarkDeath;
                    ui.Theme.Apply(ui.Config.Personalization);
                    requestColorCommit(nameof(PersonalizationConfig.LightDeathsNumberColor));
                }).AutomationName("恢复默认")),
            statusText: i18n.T("settings.deathColorStatusSet", "自定义配色"));
    }
}
