using Xunit;
using static Microsoft.UI.Reactor.Factories;
using static YuumiX.App.Controls.SettingsCardElement;
using YuumiX.App.Controls;

namespace YuumiX.App.Tests;

/// <summary>
/// SettingsCardKit.Expander 子项归一化兜底的回归测试（R15）。
/// Toolkit 的 SettingsExpander 要求每个 item 必须是 SettingsCard，
/// 非卡片项在展开实例化时会因 Style.TargetType 不匹配抛 COMException (0x800F1000) 闪退
/// （AGENTS.md §4/§5.9 的前科）。归一化兜底必须永远拦在原生控件之前。
/// </summary>
public sealed class SettingsCardKitExpanderTests
{
    [Fact]
    public void Normalize_FiltersNullAndEmpty_WrapsNonCardsInContentCard()
    {
        var rawCard = SettingsCard(header: "h", description: "d");
        var nonCard = TextBlock("x");

        var expander = (SettingsExpanderElement)SettingsCardKit.Expander(
            "header",
            "description",
            SettingsStatus.Enabled,
            headerControl: null,
            innerItems: [null!, Empty(), nonCard, rawCard]);

        var items = expander.Items;
        Assert.NotNull(items);
        Assert.Equal(2, items.Count); // null 与 EmptyElement 被过滤
        Assert.IsType<SettingsCardElement>(items[0]); // 非卡片项被 ContentCard 兜底包装
        Assert.Same(rawCard, items[1]); // 已是 SettingsCard 的项原样透传
    }

    [Fact]
    public void Normalize_AllFilteredOut_ProducesNoItems()
    {
        var expander = (SettingsExpanderElement)SettingsCardKit.Expander(
            "header",
            "description",
            SettingsStatus.Enabled,
            headerControl: null,
            innerItems: [null!, Empty()]);

        Assert.Empty(expander.Items);
    }
}
