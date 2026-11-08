using Microsoft.UI.Reactor.Animation;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Pages.Opgg;
using Xunit;

namespace YuumiX.App.Tests;

public sealed class OpggMotionTests
{
    [Fact]
    public void ShouldAnimate_RespectsGuards()
    {
        Assert.True(OpggMotion.ShouldAnimate(hasContent: true, reducedMotion: false, highContrast: false));
        Assert.False(OpggMotion.ShouldAnimate(hasContent: false, reducedMotion: false, highContrast: false));
        Assert.False(OpggMotion.ShouldAnimate(hasContent: true, reducedMotion: true, highContrast: false));
        Assert.False(OpggMotion.ShouldAnimate(hasContent: true, reducedMotion: false, highContrast: true));
    }

    [Fact]
    public void View_WhenReady_AppliesFadeInWithCorrectTrigger()
    {
        var content = Body("OP.GG 梯队内容");
        var result = OpggMotion.View(content, "opgg:tier@", skipMotion: false);

        AssertFade(result, "OpggViewFadeIn", "opgg:tier@");
    }

    [Fact]
    public void BuildTrigger_PreservesIdentityDuringFilterChanges()
    {
        // 1. 未就绪时不触发动效
        Assert.Null(OpggMotion.BuildTrigger("tier", selectedChampId: null, isReady: false));

        // 2. 梯队视图就绪
        string? tierTrigger = OpggMotion.BuildTrigger("tier", selectedChampId: null, isReady: true);
        Assert.Equal("opgg:tier@", tierTrigger);

        // 3. 切换分路、段位不改变触发值（避免整区重播淡入）
        string? tierSameViewTrigger = OpggMotion.BuildTrigger("tier", selectedChampId: null, isReady: true);
        Assert.Equal(tierTrigger, tierSameViewTrigger);

        // 4. 切换英雄或切换到出装视图时演进动效身份
        string? buildTriggerChampA = OpggMotion.BuildTrigger("build", selectedChampId: 103, isReady: true);
        string? buildTriggerChampB = OpggMotion.BuildTrigger("build", selectedChampId: 157, isReady: true);
        Assert.Equal("opgg:build@103", buildTriggerChampA);
        Assert.Equal("opgg:build@157", buildTriggerChampB);
        Assert.NotEqual(buildTriggerChampA, buildTriggerChampB);
    }

    [Fact]
    public void View_WhenTriggerNull_PreservesContentWithoutAnimation()
    {
        var content = Body("加载中状态");
        var result = OpggMotion.View(content, trigger: null, skipMotion: false);

        Assert.Same(content, result);
        Assert.Null(result.KeyframeAnimations);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void View_ReducedMotionOrHighContrast_PreservesContent(bool reducedMotion, bool highContrast)
    {
        bool skipMotion = reducedMotion || highContrast;
        var content = Body("OP.GG 梯队内容");
        var result = OpggMotion.View(content, "opgg:tier@@mid@platinum_plus@ranked@kr", skipMotion);

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
