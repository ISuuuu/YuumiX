using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace YuumiX.App.Pages.Home;

/// <summary>
/// 主页动效决策与合成关键帧封装（遵循 AGENTS.md 与原生 Fluent 规范）。
/// 纯透明度淡入，严禁 Scale/Translation，保护文字像素网格拟合；支持无障碍守卫。
/// </summary>
internal static class HomeMotion
{
    private const int FadeDurationMs = 150;

    /// <summary>
    /// 判断是否应该对召唤师卡片播放淡入动画。
    /// </summary>
    internal static bool ShouldAnimateSummonerCard(
        bool hasData,
        bool reducedMotion,
        bool highContrast) =>
        hasData && !reducedMotion && !highContrast;

    /// <summary>
    /// 为召唤师概览卡片施加 150ms 纯透明度淡入动效。
    /// </summary>
    internal static Element SummonerCard(
        Element content,
        object? trigger,
        bool skipMotion) =>
        skipMotion || trigger is null
            ? content
            : content.Keyframes(
                "HomeSummonerCardFadeIn",
                trigger,
                kb => kb.At(0f, opacity: 0f)
                        .At(1f, opacity: 1f)
                        .Duration(FadeDurationMs));
}
