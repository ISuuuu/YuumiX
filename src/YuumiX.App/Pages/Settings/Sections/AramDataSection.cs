using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using YuumiX.App.Controls;
using YuumiX.App.Windowing;
using YuumiX.Core.Persistence;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Pages.Settings.Sections;

/// <summary>
/// 设置页「海克斯大乱斗」分组：选人主动弹出 / 全局显隐热键。
/// 数据源地址（General.AramDataApiUrl）仍保留在 config.json 中供手动修改，不在界面暴露。
/// </summary>
internal static class AramDataSection
{
    private static readonly string[] HotkeyModifierTokens =
    [
        "Alt",
        "Ctrl",
        "Shift",
        "Ctrl+Shift",
        "Alt+Shift",
        string.Empty
    ];

    private static readonly string[] HotkeyKeys =
    [
        "X", "H", "Z", "C", "V", "B", "G", "K", "L",
        "`", "Tab",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"
    ];

    public static Element Render(SettingsUi ui)
    {
        var config = ui.Config;
        var i18n = ui.Services.I18n;
        var (modIdx, keyIdx) = ParseHotkeyIndices(config.General.AramDataHotkey);
        var noModifierLabel = i18n.T("settings.aramHotkeyNoModifier", "无修饰键");
        string[] modifierLabels =
        [
            "Alt",
            "Ctrl",
            "Shift",
            "Ctrl+Shift",
            "Alt+Shift",
            noModifierLabel
        ];

        void ApplyHotkey(int nextModIdx, int nextKeyIdx)
        {
            var nextHotkey = BuildHotkeyString(nextModIdx, nextKeyIdx);
            if (string.Equals(nextHotkey, config.General.AramDataHotkey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            config.General.AramDataHotkey = nextHotkey;
            AramHotkeyMonitor.UpdateHotkey(nextHotkey);
            ui.Commit(nameof(GeneralConfig.AramDataHotkey));
        }

        var autoOpenCard = SettingsCardKit.Toggle(
            i18n.T("settings.aramAutoOpenTitle", "选人页面主动弹出"),
            i18n.T("settings.aramAutoOpenDesc", "进入海克斯大乱斗选人阶段时自动弹出右侧推荐窗口"),
            config.General.AramDataAutoOpen,
            v =>
            {
                config.General.AramDataAutoOpen = v;
                ui.Commit(nameof(GeneralConfig.AramDataAutoOpen));
            });

        var hotkeyCard = SettingsCardKit.Card(
            i18n.T("settings.aramHotkeyTitle", "全局显隐热键"),
            i18n.T("settings.aramHotkeyDesc", "游戏内按一次显示，再按一次隐藏海克斯推荐窗口"),
            HStack(8,
                PixelComboBox.Render(
                    modifierLabels,
                    modIdx,
                    idx => ApplyHotkey(idx, keyIdx),
                    automationName: i18n.T("settings.aramHotkeyModifierAria", "海克斯热键修饰键"),
                    width: 120),
                PixelComboBox.Render(
                    HotkeyKeys,
                    keyIdx,
                    idx => ApplyHotkey(modIdx, idx),
                    automationName: i18n.T("settings.aramHotkeyKeyAria", "海克斯热键主键"),
                    width: 88)));

        return VStack(8, autoOpenCard, hotkeyCard);
    }

    private static (int ModIndex, int KeyIndex) ParseHotkeyIndices(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return (0, 0);
        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return (0, 0);

        var keyToken = parts[^1];
        var keyIdx = Array.FindIndex(HotkeyKeys, k => string.Equals(k, keyToken, StringComparison.OrdinalIgnoreCase));
        if (keyIdx < 0) keyIdx = 0;

        var modToken = parts.Length > 1 ? string.Join("+", parts[..^1]) : string.Empty;
        var modIdx = Array.FindIndex(HotkeyModifierTokens, m => string.Equals(m, modToken, StringComparison.OrdinalIgnoreCase));
        if (modIdx < 0) modIdx = 0;

        return (modIdx, keyIdx);
    }

    private static string BuildHotkeyString(int modIndex, int keyIndex)
    {
        var mod = modIndex >= 0 && modIndex < HotkeyModifierTokens.Length ? HotkeyModifierTokens[modIndex] : "Alt";
        var key = keyIndex >= 0 && keyIndex < HotkeyKeys.Length ? HotkeyKeys[keyIndex] : "X";
        return string.IsNullOrEmpty(mod) ? key : $"{mod}+{key}";
    }
}
