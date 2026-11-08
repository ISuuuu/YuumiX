using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace YuumiX.App.Pages.Opgg;

/// <summary>
/// OP.GG 视图动效决策与合成关键帧封装（遵循 AGENTS.md 与原生 Fluent 规范）。
/// 纯透明度淡入，严禁 Scale/Translation，保护文字像素网格拟合；支持无障碍守卫。
/// </summary>
internal static class OpggMotion
{
    private const int FadeDurationMs = 150;

    /// <summary>
    /// 判断是否应该播放 OP.GG 内容淡入动效。
    /// 仅在有内容且未开启减少动画/高对比度时淡入。
    /// </summary>
    internal static bool ShouldAnimate(
        bool hasContent,
        bool reducedMotion,
        bool highContrast) =>
        hasContent && !reducedMotion && !highContrast;

    /// <summary>
    /// 构建 OP.GG 视图主体的动效身份。
    /// 仅在主视图切换（梯队 ↔ 出装）或选中英雄变更时演进动效身份，
    /// 避免在同视图内切换分路位置、段位或大区时整块正文重复淡入闪烁。
    /// </summary>
    internal static string? BuildTrigger(string currentView, int? selectedChampId, bool isReady) =>
        isReady ? $"opgg:{currentView}@{selectedChampId}" : null;

    /// <summary>
    /// 为 OP.GG 主视图（梯队列表 / 英雄出装）施加 150ms 纯透明度淡入动效。
    /// </summary>
    internal static Element View(
        Element content,
        object? trigger,
        bool skipMotion) =>
        skipMotion || trigger is null
            ? content
            : content.Keyframes(
                "OpggViewFadeIn",
                trigger,
                kb => kb.At(0f, opacity: 0f)
                        .At(1f, opacity: 1f)
                        .Duration(FadeDurationMs));
}
