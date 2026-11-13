using Xunit;
using YuumiX.App.Pages.Career;

namespace YuumiX.App.Tests;

/// <summary>
/// 加载态写回语义：已有数据时刷新不得把内容退回「正在读取…」占位，
/// 否则每次开箱/分解/刷新按钮都会让整块内容闪一下。
/// </summary>
public sealed class CareerLoadingTests
{
    [Fact]
    public void KeepDataWhileLoading_SkipsLoadingWhenDataExists()
    {
        var applied = new List<bool>();
        Action<bool> apply = applied.Add;

        var guarded = CareerLoaders.KeepDataWhileLoading(apply, hasData: true);
        guarded(true);
        guarded(false);

        // 刷新开始时的置真被跳过；结束时的置假照旧写回（幂等，不引起重渲染）
        Assert.Equal(new[] { false }, applied);
    }

    [Fact]
    public void KeepDataWhileLoading_ShowsLoadingOnFirstLoad()
    {
        var applied = new List<bool>();
        Action<bool> apply = applied.Add;

        var guarded = CareerLoaders.KeepDataWhileLoading(apply, hasData: false);
        guarded(true);
        guarded(false);

        Assert.Equal(new[] { true, false }, applied);
    }
}
