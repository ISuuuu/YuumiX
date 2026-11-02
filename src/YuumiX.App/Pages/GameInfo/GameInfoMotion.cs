using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace YuumiX.App.Pages.GameInfo;

/// <summary>
/// 对局信息页动效决策与合成关键帧封装（遵循 AGENTS.md 与原生 Fluent 规范）。
/// 纯透明度淡入，严禁 Scale/Translation，保护文字像素网格拟合；支持无障碍守卫。
/// </summary>
internal static class GameInfoMotion
{
    private const int FadeDurationMs = 150;

    /// <summary>
    /// 判断是否应该播放对局信息面板淡入动效。
    /// 仅在数据就绪（非骨架、非加载）且未开启减少动画/高对比度时淡入。
    /// </summary>
    internal static bool ShouldAnimate(
        bool isContentReady,
        bool reducedMotion,
        bool highContrast) =>
        isContentReady && !reducedMotion && !highContrast;

    /// <summary>
    /// 为对局信息视图（5 列/10 列）施加 150ms 纯透明度淡入动效。
    /// </summary>
    internal static Element Content(
        Element content,
        object? trigger,
        bool skipMotion) =>
        skipMotion || trigger is null
            ? content
            : content.Keyframes(
                "GameInfoContentFadeIn",
                trigger,
                kb => kb.At(0f, opacity: 0f)
                        .At(1f, opacity: 1f)
                        .Duration(FadeDurationMs));
}
