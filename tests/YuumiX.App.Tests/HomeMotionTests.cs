using Microsoft.UI.Reactor.Animation;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Pages.Home;
using Xunit;

namespace YuumiX.App.Tests;

public sealed class HomeMotionTests
{
    [Fact]
    public void ShouldAnimateSummonerCard_RespectsGuards()
    {
        Assert.True(HomeMotion.ShouldAnimateSummonerCard(hasData: true, reducedMotion: false, highContrast: false));
        Assert.False(HomeMotion.ShouldAnimateSummonerCard(hasData: false, reducedMotion: false, highContrast: false));
        Assert.False(HomeMotion.ShouldAnimateSummonerCard(hasData: true, reducedMotion: true, highContrast: false));
        Assert.False(HomeMotion.ShouldAnimateSummonerCard(hasData: true, reducedMotion: false, highContrast: true));
    }

    [Fact]
    public void SummonerCard_WhenReady_AppliesFadeInWithCorrectTrigger()
    {
        var content = Card(Body("召唤师资料"));
        var result = HomeMotion.SummonerCard(content, "home:profile:puuid-1", skipMotion: false);

        AssertFade(result, "HomeSummonerCardFadeIn", "home:profile:puuid-1");
    }

    [Fact]
    public void BuildSummonerTrigger_PreservesIdentityDuringTierEnrichment()
    {
        Assert.Null(HomeMotion.BuildSummonerTrigger(null));
        Assert.Null(HomeMotion.BuildSummonerTrigger(""));

        string? initialTrigger = HomeMotion.BuildSummonerTrigger("puuid-1");
        Assert.Equal("home:profile:puuid-1", initialTrigger);

        // 异步补齐段位不应影响动效身份，防止召唤师卡片重新淡入闪烁
        string? enrichedTrigger = HomeMotion.BuildSummonerTrigger("puuid-1");
        Assert.Equal(initialTrigger, enrichedTrigger);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SummonerCard_ReducedMotionOrHighContrast_PreservesContent(bool reducedMotion, bool highContrast)
    {
        bool skipMotion = reducedMotion || highContrast;
        var content = Card(Body("召唤师资料"));
        var result = HomeMotion.SummonerCard(content, "home:profile:puuid-1@DIAMOND", skipMotion);

        Assert.Same(content, result);
        Assert.Null(result.KeyframeAnimations);
    }

    private static void AssertFade(Element element, string name, object trigger)
    {
        Assert.NotNull(element.KeyframeAnimations);
        var animation = Assert.Single(element.KeyframeAnimations);
        Assert.Equal(name, animation.Name);
        Assert.Equal(trigger, animation.Trigger);
        Assert.Equal(TimeSpan.FromMilliseconds(150), animation.Definition.Duration);
        Assert.False(animation.Definition.Loop);

        var frames = animation.Definition.Keyframes;
        Assert.Equal(2, frames.Length);
        Assert.Equal(0f, frames[0].Progress);
        Assert.Equal(0f, frames[0].Opacity);
        Assert.Equal(1f, frames[1].Progress);
        Assert.Equal(1f, frames[1].Opacity);

        // 严格遵守 AGENTS.md §1.2：严禁对文字区域做 Scale/Translation/Rotation，保持 DirectWrite 像素对齐
        Assert.All(frames, frame =>
        {
            Assert.Null(frame.Scale);
            Assert.Null(frame.Translation);
            Assert.Null(frame.Rotation);
        });
    }
}
