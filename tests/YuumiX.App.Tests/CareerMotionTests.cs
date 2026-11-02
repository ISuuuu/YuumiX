using Microsoft.UI.Reactor.Animation;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Pages.Career;
using Xunit;

namespace YuumiX.App.Tests;

public sealed class CareerMotionTests
{
    [Fact]
    public void Header_WhenNotReadyOrSkeleton_PreservesContentWithoutAnimation()
    {
        var content = Body("骨架头部");
        var result = CareerMotion.Header(content, skipMotion: false, puuid: "puuid-1", refreshToken: 0, isReady: false);

        Assert.Same(content, result);
        Assert.Null(result.KeyframeAnimations);
    }

    [Fact]
    public void Header_WhenReady_AppliesFadeInWithCorrectTrigger()
    {
        var content = Body("个人资料");
        var result = CareerMotion.Header(content, skipMotion: false, puuid: "puuid-1", refreshToken: 2, isReady: true);

        AssertFade(result, "careerHeaderFadeIn", "header:puuid-1@2");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Header_ReducedMotionOrHighContrast_PreservesContent(bool reducedMotion, bool highContrast)
    {
        bool skipMotion = reducedMotion || highContrast;
        var content = Body("个人资料");
        var result = CareerMotion.Header(content, skipMotion, puuid: "puuid-1", refreshToken: 0, isReady: true);

        Assert.Same(content, result);
        Assert.Null(result.KeyframeAnimations);
    }

    [Fact]
    public void Summary_WhenReady_AppliesFadeIn()
    {
        var content = Body("排位与近期看板");
        var result = CareerMotion.Summary(content, skipMotion: false, puuid: "puuid-1", refreshToken: 1, isReady: true);

        AssertFade(result, "careerSummaryFadeIn", "summary:puuid-1@1");
    }

    [Fact]
    public void Summary_WhenNotReady_PreservesContent()
    {
        var content = Body("骨架看板");
        var result = CareerMotion.Summary(content, skipMotion: false, puuid: "puuid-1", refreshToken: 0, isReady: false);

        Assert.Same(content, result);
        Assert.Null(result.KeyframeAnimations);
    }

    [Fact]
    public void Matches_WhenEmpty_PreservesContent()
    {
        var content = Body("空战绩");
        var result = CareerMotion.Matches(content, skipMotion: false, puuid: "puuid-1", refreshToken: 0, queueFilter: 0, visibleCount: 0);

        Assert.Same(content, result);
        Assert.Null(result.KeyframeAnimations);
    }

    [Fact]
    public void Matches_WhenHasVisibleMatches_AppliesFadeIn()
    {
        var content = Body("战绩列表");
        var result = CareerMotion.Matches(content, skipMotion: false, puuid: "puuid-1", refreshToken: 1, queueFilter: 420, visibleCount: 10);

        AssertFade(result, "careerMatchesFadeIn", "matches:puuid-1@1@420");
    }

    [Fact]
    public void LootAndMastery_WhenReady_AppliesFadeIn()
    {
        var lootContent = Body("战利品内容");
        var lootResult = CareerMotion.Loot(lootContent, skipMotion: false, puuid: "puuid-1", refreshToken: 0, isReady: true);
        AssertFade(lootResult, "careerLootFadeIn", "loot:puuid-1@0");

        var masteryContent = Body("熟练度内容");
        var masteryResult = CareerMotion.Mastery(masteryContent, skipMotion: false, puuid: "puuid-1", refreshToken: 0, isReady: true);
        AssertFade(masteryResult, "careerMasteryFadeIn", "mastery:puuid-1@0");
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
