using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace YuumiX.App.Pages.SavedPlayers;

/// <summary>
/// 路人集动效决策与合成关键帧封装（遵循 AGENTS.md 与原生 Fluent 规范）。
/// 纯透明度淡入，严禁 Scale/Translation，保护文字像素网格拟合；支持无障碍守卫。
/// </summary>
internal static class SavedPlayersMotion
{
    private const int FadeDurationMs = 150;

    /// <summary>
    /// 判断是否应该播放路人集列表淡入动效。
    /// 仅在非骨架加载、有数据且未开启减少动画/高对比度时淡入。
    /// </summary>
    internal static bool ShouldAnimate(
        bool hasData,
        bool reducedMotion,
        bool highContrast) =>
        hasData && !reducedMotion && !highContrast;

    /// <summary>
    /// 构建路人集列表的动画 Trigger 身份。
    /// 分离底层取数刷新计数（refreshNonce）与动效身份（motionNonce）。
    /// 仅在账号、检索条件或显式手动刷新变更时演进动画身份，后台静默刷新（页面切回、存储后台更新）不重置 trigger，
    /// 避免已有列表在静默刷新时整块重新淡入。
    /// </summary>
    internal static string? BuildListTrigger(
        string? selfPuuid,
        string filter,
        string committedSearch,
        int motionNonce,
        bool hasData) =>
        hasData
            ? $"savedplayers:{selfPuuid}@{filter}@{committedSearch}@{motionNonce}"
            : null;

    /// <summary>
    /// 为路人集列表区域施加 150ms 纯透明度淡入动效。
    /// </summary>
    internal static Element List(
        Element content,
        object? trigger,
        bool skipMotion) =>
        skipMotion || trigger is null
            ? content
            : content.Keyframes(
                "SavedPlayersListFadeIn",
                trigger,
                kb => kb.At(0f, opacity: 0f)
                        .At(1f, opacity: 1f)
                        .Duration(FadeDurationMs));
}
