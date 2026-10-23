using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;

namespace YuumiX.App.Pages;

/// <summary>
/// 页面占位组件入参。骨架阶段用于标注每个页面待迁移的真实功能。
///
/// <see cref="BrandLogoUri"/> 为可选的页面头部品牌图（<c>file:///</c> URI）。
/// 目前只有主页传值，对应原 Home.vue 顶部的 96px Logo；其余页面传 null 即不渲染图片。
/// </summary>
public sealed record PlaceholderProps(
    string Title,
    string SourceView,
    string[] PendingFeatures,
    string? BrandLogoUri = null);

/// <summary>
/// 骨架阶段的统一页面占位。
///
/// 目的不是复刻 UI，而是把「路由 → 页面组件」的装配关系固定下来。
/// 后续每迁移一个模块，就把对应的占位替换为真实页面组件。
///
/// 页脚的 Core 状态行同时充当 ADR-06 的活体验证：这些页面都是外壳的后代，
/// 能读到服务就说明 <c>Provide</c> → <c>UseContext</c> 链路成立。
/// </summary>
public sealed class PlaceholderPage : Component<PlaceholderProps>
{
    /// <summary>品牌图边长（DIP）。与原 Home.vue 的 <c>.offline-logo</c> 96px 保持一致。</summary>
    private const double BrandLogoSize = 96;

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);

        var featureList = Props.PendingFeatures
            .Select(feature => (Element)HStack(8,
                TextBlock("\uE73E").FontSize(11).Foreground(Theme.SecondaryText),
                TextBlock(feature).Foreground(Theme.SecondaryText).TextWrapping()))
            .ToArray();

        var blocks = new List<Element>();

        if (Props.BrandLogoUri is not null)
        {
            // Logo 自带留白与投影观感，这里不再补 Border/背景，避免出现「图里还有一圈框」。
            blocks.Add(Image(Props.BrandLogoUri)
                .Width(BrandLogoSize)
                .Height(BrandLogoSize)
                .HAlign(HorizontalAlignment.Left)
                .AutomationName("YuumiX 图标"));
        }

        blocks.Add(VStack(4,
            Heading(Props.Title),
            Caption($"对应原实现：{Props.SourceView}")));

        blocks.Add(Border(
                VStack(12,
                    SubHeading("待迁移功能"),
                    VStack(8, featureList))
            ).Padding(20)
             .CornerRadius(8)
             .Background(Theme.CardBackground)
             .WithBorder(Theme.CardStroke, 1));

        blocks.Add(Caption(
            $"Core 已就绪：配置 v{services.Config.Version} · 日志等级 {services.Config.General.LogLevel} · " +
            $"数据目录 {services.Paths.DataDirectory}"));

        return ScrollView(
            VStack(20, blocks.ToArray()).Padding(28).HAlign(HorizontalAlignment.Stretch)
        );
    }
}

