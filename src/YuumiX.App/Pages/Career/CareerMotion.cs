using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace YuumiX.App.Pages.Career;

internal static class CareerMotion
{
    internal static Element Header(Element content, bool skipMotion, string? puuid, int refreshToken, bool isReady) =>
        skipMotion || !isReady
            ? content
            : FadeIn(content, "careerHeaderFadeIn", $"header:{puuid ?? "me"}@{refreshToken}");

    internal static Element Summary(Element content, bool skipMotion, string? puuid, int refreshToken, bool isReady) =>
        skipMotion || !isReady
            ? content
            : FadeIn(content, "careerSummaryFadeIn", $"summary:{puuid ?? "me"}@{refreshToken}");

    internal static Element Matches(Element content, bool skipMotion, string? puuid, int refreshToken, int queueFilter, int visibleCount) =>
        skipMotion || visibleCount == 0
            ? content
            : FadeIn(content, "careerMatchesFadeIn", $"matches:{puuid ?? "me"}@{refreshToken}@{queueFilter}");

    internal static Element Loot(Element content, bool skipMotion, string? puuid, int refreshToken, bool isReady) =>
        skipMotion || !isReady
            ? content
            : FadeIn(content, "careerLootFadeIn", $"loot:{puuid ?? "me"}@{refreshToken}");

    internal static Element Mastery(Element content, bool skipMotion, string? puuid, int refreshToken, bool isReady) =>
        skipMotion || !isReady
            ? content
            : FadeIn(content, "careerMasteryFadeIn", $"mastery:{puuid ?? "me"}@{refreshToken}");

    private static Element FadeIn(Element content, string name, object trigger) =>
        content.Keyframes(name, trigger, kb => kb
            .At(0f, opacity: 0f)
            .At(1f, opacity: 1f)
            .Duration(150));
}
