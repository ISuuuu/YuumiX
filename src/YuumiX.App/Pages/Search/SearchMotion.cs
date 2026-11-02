using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using YuumiX.Core.Features.Match.Models;

namespace YuumiX.App.Pages.Search;

internal static class SearchMotion
{
    internal static Element List(Element content, bool skipMotion, int visibleCount, string historyCacheKey) =>
        skipMotion || visibleCount == 0
            ? content
            : FadeIn(content, "listFadeIn", historyCacheKey);

    internal static Element Detail(Element content, bool skipMotion, long currentGameId, AsyncValue<MatchDetail?> resource) =>
        skipMotion || currentGameId <= 0 || resource is not AsyncValue<MatchDetail?>.Data { Value: not null }
            ? content
            : FadeIn(content, "detailFadeIn", currentGameId);

    // 触发值只取结果身份，补充字段和同目标刷新不改变已有元素上的动画身份。
    private static Element FadeIn(Element content, string name, object trigger) =>
        content.Keyframes(name, trigger, kb => kb
            .At(0f, opacity: 0f)
            .At(1f, opacity: 1f)
            .Duration(150));
}
