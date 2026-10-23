using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Pages;

/// <summary>
/// 页面占位组件入参。骨架阶段用于标注每个页面待迁移的真实功能。
/// </summary>
public sealed record PlaceholderProps(string Title, string SourceView, string[] PendingFeatures);

/// <summary>
/// 骨架阶段的统一页面占位。
///
/// 目的不是复刻 UI，而是把「路由 → 页面组件」的装配关系固定下来。
/// 后续每迁移一个模块，就把对应的占位替换为真实页面组件。
/// </summary>
public sealed class PlaceholderPage : Component<PlaceholderProps>
{
    public override Element Render()
    {
        var featureList = Props.PendingFeatures
            .Select(feature => (Element)HStack(8,
                TextBlock("\uE73E").FontSize(11).Foreground(Theme.SecondaryText),
                TextBlock(feature).Foreground(Theme.SecondaryText).TextWrapping()))
            .ToArray();

        return ScrollView(
            VStack(20,
                VStack(4,
                    Heading(Props.Title),
                    Caption($"对应原实现：{Props.SourceView}")),

                Border(
                    VStack(12,
                        SubHeading("待迁移功能"),
                        VStack(8, featureList))
                ).Padding(20)
                 .CornerRadius(8)
                 .Background(Theme.CardBackground)
                 .WithBorder(Theme.CardStroke, 1)
            ).Padding(28).HAlign(HorizontalAlignment.Stretch)
        );
    }
}
