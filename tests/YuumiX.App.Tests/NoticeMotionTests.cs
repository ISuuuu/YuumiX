using Microsoft.UI.Reactor.Animation;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Pages.Notice;
using Xunit;

namespace YuumiX.App.Tests;

public sealed class NoticeMotionTests
{
    [Fact]
    public void ShouldAnimate_RespectsGuards()
    {
        Assert.True(NoticeMotion.ShouldAnimate(hasContent: true, reducedMotion: false, highContrast: false));
        Assert.False(NoticeMotion.ShouldAnimate(hasContent: false, reducedMotion: false, highContrast: false));
        Assert.False(NoticeMotion.ShouldAnimate(hasContent: true, reducedMotion: true, highContrast: false));
        Assert.False(NoticeMotion.ShouldAnimate(hasContent: true, reducedMotion: false, highContrast: true));
    }

    [Fact]
    public void Content_WhenReady_AppliesFadeInWithCorrectTrigger()
    {
        var content = Card(Body("公告 Markdown 卡片"));
        var result = NoticeMotion.Content(content, "notice:0@False@120", skipMotion: false);

        AssertFade(result, "NoticeContentFadeIn", "notice:0@False@120");
    }

    [Fact]
    public void Content_WhenTriggerNull_PreservesContentWithoutAnimation()
    {
        var content = Card(Body("公告内容"));
        var result = NoticeMotion.Content(content, trigger: null, skipMotion: false);

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
        var content = Card(Body("公告内容"));
        var result = NoticeMotion.Content(content, "notice:0@False@120", skipMotion);

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
