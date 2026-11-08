using Microsoft.UI.Reactor.Animation;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Pages.SavedPlayers;
using Xunit;

namespace YuumiX.App.Tests;

public sealed class SavedPlayersMotionTests
{
    [Fact]
    public void ShouldAnimate_RespectsGuards()
    {
        Assert.True(SavedPlayersMotion.ShouldAnimate(hasData: true, reducedMotion: false, highContrast: false));
        Assert.False(SavedPlayersMotion.ShouldAnimate(hasData: false, reducedMotion: false, highContrast: false));
        Assert.False(SavedPlayersMotion.ShouldAnimate(hasData: true, reducedMotion: true, highContrast: false));
        Assert.False(SavedPlayersMotion.ShouldAnimate(hasData: true, reducedMotion: false, highContrast: true));
    }

    [Fact]
    public void List_WhenReady_AppliesFadeInWithCorrectTrigger()
    {
        var content = Body("路人集卡片列表");
        var result = SavedPlayersMotion.List(content, "savedplayers:puuid-1@0", skipMotion: false);

        AssertFade(result, "SavedPlayersListFadeIn", "savedplayers:puuid-1@0");
    }

    [Fact]
    public void BuildListTrigger_SeparatesSilentRefreshFromMotionIdentity()
    {
        // 1. 无数据时返回 null，不触发动画
        Assert.Null(SavedPlayersMotion.BuildListTrigger("puuid-1", "all", "", 0, hasData: false));

        // 2. 首次有数据
        string? trigger1 = SavedPlayersMotion.BuildListTrigger("puuid-1", "all", "", 0, hasData: true);
        Assert.Equal("savedplayers:puuid-1@0", trigger1);

        // 3. 切换本地分类或搜索关键字不改变 trigger，防止本地筛选重复淡入闪烁
        string? triggerFilterChange = SavedPlayersMotion.BuildListTrigger("puuid-1", "black", "player", 0, hasData: true);
        Assert.Equal(trigger1, triggerFilterChange);

        // 4. 切回页面或后台数据存储更新：refreshNonce 发生变化，但 motionNonce 保持 0（静默刷新）
        // Trigger 恒定不变，绝对不触发已有列表整块重新淡入
        string? triggerSilentRefresh = SavedPlayersMotion.BuildListTrigger("puuid-1", "all", "", 0, hasData: true);
        Assert.Equal(trigger1, triggerSilentRefresh);

        // 5. 用户在页头主动点击刷新按钮：motionNonce 递增
        // Trigger 发生改变，允许播放一次淡入动效
        string? triggerManualRefresh = SavedPlayersMotion.BuildListTrigger("puuid-1", "all", "", 1, hasData: true);
        Assert.Equal("savedplayers:puuid-1@1", triggerManualRefresh);
        Assert.NotEqual(trigger1, triggerManualRefresh);
    }

    [Fact]
    public void List_WhenTriggerNullOrLoading_PreservesContentWithoutAnimation()
    {
        var content = Body("骨架占位列表");
        var result = SavedPlayersMotion.List(content, trigger: null, skipMotion: false);

        Assert.Same(content, result);
        Assert.Null(result.KeyframeAnimations);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void List_ReducedMotionOrHighContrast_PreservesContent(bool reducedMotion, bool highContrast)
    {
        bool skipMotion = reducedMotion || highContrast;
        var content = Body("路人集卡片列表");
        var result = SavedPlayersMotion.List(content, "savedplayers:puuid-1@0", skipMotion);

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
