using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using YuumiX.App.Composition;
using YuumiX.App.Theming;
using YuumiX.Core.Automation;
using YuumiX.Core.Localization;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Shell;

/// <summary>
/// 应用主窗口标题栏渲染与游戏阶段副标题文案生成。
/// </summary>
internal static class ShellTitleBar
{
    /// <summary>
    /// 构造主窗口系统 TitleBar（高度由 AppWindowTitleBar Standard 档锁定 32 DIP）。
    /// </summary>
    internal static Element Build(
        bool isClientConnected,
        GamePhase gamePhase,
        string? mapSide,
        I18nService? i18n = null) =>
        TitleBar("YuumiX") with
        {
            // 标题栏图标与原 CustomTitleBar.vue 的 titlebar-logo 对齐：同一个 Logo 文件。
            // 用 ImageIcon 而非 BitmapIcon —— 后者默认单色化，会把彩色 Logo 染成主题色。
            Icon = ImageIcon(new Uri(AppAssets.LogoUri)),
            // 对应原 CustomTitleBar.vue 的 titlebar-phase：已连接且非空闲时展示阶段中文名；
            // 活跃对局三阶段已知阵营时追加红蓝方（旧版 mapSideLabel）。
            Subtitle = GetPhaseSubtitle(isClientConnected, gamePhase, mapSide, i18n),
            IsBackButtonVisible = false,
        };

    /// <summary>
    /// 计算标题栏副标题（游戏状态）。已连接且非空闲（None）阶段时返回阶段中文名，
    /// 否则返回空串隐藏（对齐原 CustomTitleBar.vue 的 <c>isConnected &amp;&amp; gamePhase !== 'None'</c> 规则）。
    ///
    /// 红蓝方（旧版 mapSideLabel）：仅选人/游戏加载/游戏中三阶段且阵营已知时拼接；
    /// 其余阶段（Lobby/EndOfGame/Reconnect 等）只显示阶段名，与旧版 else 分支清除阵营一致。
    /// </summary>
    internal static string GetPhaseSubtitle(
        bool isClientConnected,
        GamePhase gamePhase,
        string? mapSide,
        I18nService? i18n = null)
    {
        if (!isClientConnected || gamePhase is GamePhase.None or GamePhase.Unknown)
        {
            return string.Empty;
        }

        var label = i18n?.T("phase." + gamePhase, null) ?? (gamePhase switch
        {
            GamePhase.Lobby => "房间组队中",
            GamePhase.Matchmaking => "匹配中",
            GamePhase.ReadyCheck => "确认对局",
            GamePhase.ChampSelect => "选择英雄",
            GamePhase.GameStart => "游戏加载",
            GamePhase.InProgress => "游戏中",
            GamePhase.Reconnect => "重新连接",
            GamePhase.WaitingForStats => "等待数据",
            GamePhase.PreEndOfGame => "结算中",
            GamePhase.EndOfGame => "对局结束",
            GamePhase.WatchInProgress => "观战中",
            _ => gamePhase.ToString(),
        });

        if (mapSide is "blue" or "red" &&
            gamePhase is GamePhase.ChampSelect or GamePhase.GameStart or GamePhase.InProgress)
        {
            var blueSide = i18n?.T("titlebar.blueSide", "蓝色方") ?? "蓝色方";
            var redSide = i18n?.T("titlebar.redSide", "红色方") ?? "红色方";
            label += mapSide == "blue" ? $" - {blueSide}" : $" - {redSide}";
        }

        return label;
    }
}
