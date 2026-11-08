using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace YuumiX.App.Pages.Notice;

/// <summary>
/// 公告页动效决策与合成关键帧封装（遵循 AGENTS.md 与原生 Fluent 规范）。
/// 纯透明度淡入，严禁 Scale/Translation，保护文字像素网格拟合；支持无障碍守卫。
/// </summary>
internal static class NoticeMotion
{
    private const int FadeDurationMs = 150;

    /// <summary>
    /// 判断是否应该播放公告内容淡入动效。
    /// 仅在有内容且未开启减少动画/高对比度时淡入。
    /// </summary>
    internal static bool ShouldAnimate(
        bool hasContent,
        bool reducedMotion,
        bool highContrast) =>
        hasContent && !reducedMotion && !highContrast;

    /// <summary>
    /// 构建公告卡片的动效身份。
    /// 仅在显式重试（reloadTick）或公告内容长度发生实质变化时演进动效身份，
    /// 缓存转在线（FromCache 变更但正文未变）不触发卡片重新淡入闪烁。
    /// </summary>
    internal static string? BuildTrigger(int reloadTick, string? markdown) =>
        string.IsNullOrEmpty(markdown) ? null : $"notice:{reloadTick}@{markdown.Length}";

    /// <summary>
    /// 为公告卡片区域施加 150ms 纯透明度淡入动效。
    /// </summary>
    internal static Element Content(
        Element content,
        object? trigger,
        bool skipMotion) =>
        skipMotion || trigger is null
            ? content
            : content.Keyframes(
                "NoticeContentFadeIn",
                trigger,
                kb => kb.At(0f, opacity: 0f)
                        .At(1f, opacity: 1f)
                        .Duration(FadeDurationMs));
}
