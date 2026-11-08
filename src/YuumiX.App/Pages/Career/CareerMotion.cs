using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace YuumiX.App.Pages.Career;

internal static class CareerMotion
{
    internal static Element Header(Element content, bool skipMotion, string? puuid, int refreshToken, bool isReady) =>
        content;

    internal static Element Summary(Element content, bool skipMotion, string? puuid, int refreshToken, bool isReady) =>
        content;

    // 战绩列表筛选：移除 queueFilter 动效身份，纯本地过滤不触发整表淡入，消除切换筛选时的闪烁。
    internal static Element Matches(Element content, bool skipMotion, string? puuid, int refreshToken, int queueFilter, int visibleCount) =>
        skipMotion || visibleCount == 0
            ? content
            : FadeIn(content, "careerMatchesFadeIn", $"matches:{puuid ?? "me"}@{refreshToken}");

    internal static Element Loot(Element content, object? trigger, bool skipMotion) =>
        content;

    internal static Element Loot(Element content, bool skipMotion, string? puuid, int refreshToken, bool isReady) =>
        content;

    internal static Element Mastery(Element content, object? trigger, bool skipMotion) =>
        content;

    internal static Element Mastery(Element content, bool skipMotion, string? puuid, int refreshToken, bool isReady) =>
        content;

    private static Element FadeIn(Element content, string name, object trigger) =>
        content.Keyframes(name, trigger, kb => kb
            .At(0f, opacity: 0f)
            .At(1f, opacity: 1f)
            .Duration(150));
}

/// <summary>
/// 负责跟踪生涯页从属页签（如战利品、英雄熟练度）的动效身份与展示生命周期。
/// 严格区分「重新进入页签（已有数据瞬间呈现）」与「当前页签下新结果就绪（平滑淡入）」，
/// 并在同一次展示期间稳定保留动画声明，防止在动画播放期间由于同页签重渲染撤销 Keyframes 而提前终止。
/// </summary>
internal sealed class CareerTabMotionTracker<TData> where TData : class
{
    private TData? _lastObservedData;
    private object? _activeTrigger;
    private int _sequence;

    internal object? EvaluateTrigger(
        CareerTab targetTab,
        CareerTab activeTab,
        TData? currentData,
        string? puuid,
        int refreshToken)
    {
        bool isNewDataReady = currentData is not null && !ReferenceEquals(currentData, _lastObservedData);
        if (isNewDataReady)
        {
            _lastObservedData = currentData;
            if (activeTab == targetTab)
            {
                // 只有当前处于该页签时新结果就绪，才赋予淡入动效身份；同一次展示期间保持该 Trigger
                _activeTrigger = $"{targetTab.ToString().ToLowerInvariant()}:{puuid ?? "me"}@{refreshToken}#{++_sequence}";
            }
            else
            {
                // 在其他页签后台就绪的，不赋予动效身份（后续切换过来属于重新进入页签，瞬间呈现）
                _activeTrigger = null;
            }
        }
        else if (activeTab != targetTab)
        {
            // 离开当前页签，重置动效身份；后续重新进入时保持 null 瞬间呈现已有数据
            _activeTrigger = null;
        }

        return _activeTrigger;
    }
}
