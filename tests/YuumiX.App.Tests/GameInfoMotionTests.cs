using Microsoft.UI.Reactor.Animation;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Pages.GameInfo;
using Xunit;

namespace YuumiX.App.Tests;

public sealed class GameInfoMotionTests
{
    [Fact]
    public void ShouldAnimate_RespectsGuards()
    {
        Assert.True(GameInfoMotion.ShouldAnimate(isContentReady: true, reducedMotion: false, highContrast: false));
        Assert.False(GameInfoMotion.ShouldAnimate(isContentReady: false, reducedMotion: false, highContrast: false));
        Assert.False(GameInfoMotion.ShouldAnimate(isContentReady: true, reducedMotion: true, highContrast: false));
        Assert.False(GameInfoMotion.ShouldAnimate(isContentReady: true, reducedMotion: false, highContrast: true));
    }

    [Fact]
    public void Content_WhenReady_AppliesFadeInWithCorrectTrigger()
    {
        var content = Body("对局详情面板");
        var result = GameInfoMotion.Content(content, "gameinfo:5@123456@my@False", skipMotion: false);

        AssertFade(result, "GameInfoContentFadeIn", "gameinfo:5@123456@my@False");
    }

    [Fact]
    public void Content_WhenTriggerNullOrSkeleton_PreservesContentWithoutAnimation()
    {
        var content = Body("骨架对局列表");
        var result = GameInfoMotion.Content(content, trigger: null, skipMotion: false);

        Assert.Same(content, result);
        Assert.Null(result.KeyframeAnimations);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Content_ReducedMotionOrHighContrast_PreservesContent(bool reducedMotion, bool highContrast)
    {
        bool skipMotion = reducedMotion || highContrast;
        var content = Body("对局详情面板");
        var result = GameInfoMotion.Content(content, "gameinfo:5@123456@my@False", skipMotion);

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
