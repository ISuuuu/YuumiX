using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Theming;

namespace YuumiX.App.Controls;

/// <summary>
/// LCU 未连接时的占位视图：主页与生涯 / 战绩查询 / 对局信息共用同一套排版——
/// 品牌 Logo + 「客户端未连接」+ 启动提示，主页额外提供启动按钮与等待指示。
///
/// <para><b>为什么四页共用一处实现</b></para>
/// 「客户端没开」对用户只有一种含义，四个页面各写一套必然会排版漂移。
/// 业务页不放启动入口：启动动作由主页承担，避免同一动作在四个页面各有一个入口；
/// 但主页动作区的高度必须保留（不可见占位），否则整组内容比主页矮一截，
/// 垂直居中后 Logo 与文案的纵向位置就和主页对不上。
/// </summary>
public static class LcuOfflineState
{
    /// <summary>默认提示文案，与主页未连接态保持一致。</summary>
    public const string DefaultMessage = "请启动英雄联盟客户端，连接成功后将自动识别并加载数据";

    private static string EffectiveDefaultMessage =>
        AppHost.Services?.I18n.T("home.offlineMessage", DefaultMessage) ?? DefaultMessage;

    private static BitmapImage? _logoSource;

    /// <summary>
    /// 品牌 Logo 只解码一次，供四个页面共享。
    ///
    /// <para>页面切换时 NavigationHost 会重新挂载页面（CacheMode 默认 Disabled），
    /// 每次挂载若新建 <c>BitmapImage</c>，图片会晚于文字一帧出现——这就是切换时
    /// 「图片闪一下、文字看不出来」的原因。缓存同一个已解码的 ImageSource 后，
    /// 新挂载的 Image 直接拿到现成像素，不再有空白帧。</para>
    ///
    /// <para>Image 工厂只接受 URI 字符串、无法传入 ImageSource，所以用
    /// <c>.Set()</c> 覆盖 Source（Reactor 保证 Set 在属性写入之后执行）。</para>
    /// </summary>
    private static BitmapImage LogoSource =>
        _logoSource ??= new BitmapImage(new Uri(AppAssets.LogoUri));

    /// <param name="message">主提示文案；省略时用 <see cref="EffectiveDefaultMessage"/>。</param>
    /// <param name="extra">替换「启动客户端」按钮位置的本地动作（例如对局信息的「预览演示对局」）。</param>
    public static Element Render(string? message = null, Element? extra = null) =>
        RenderCore(message ?? EffectiveDefaultMessage, extra, onLaunchClient: null);

    /// <summary>主页未连接态：共用排版 + 可用的启动入口与等待指示。</summary>
    public static Element RenderHome(Action onLaunchClient) =>
        RenderCore(EffectiveDefaultMessage, null, onLaunchClient);

    private static Element RenderCore(string message, Element? extra, Action? onLaunchClient)
    {
        bool interactive = onLaunchClient is not null;
        var i18n = AppHost.Services?.I18n;

        var launchBtnText = i18n?.T("home.launchBtn", "启动客户端") ?? "启动客户端";
        var waitingText = i18n?.T("home.waitingForLol", "等待客户端启动中...") ?? "等待客户端启动中...";
        var notConnectedText = i18n?.T("home.notConnected", "客户端未连接") ?? "客户端未连接";

        // 动作区：主页是真入口；业务页不重复放入口，但启动按钮必须留在布局里撑出与主页一致的高度。
        // 有 extra 时（对局信息的「预览演示对局」）把它叠在占位按钮上，而不是顶替——extra 比启动按钮矮，
        // 顶替后整组内容变矮，垂直居中会让 Logo 比主页低几像素，切页时就能看出位移。
        // 启动回调必须挂在 Button 工厂参数上（此前重构时丢失过挂接，按钮沦为摆设）；
        // 业务页 onLaunchClient 为 null，lambda 为空操作，且按钮本就禁用并视觉隐藏。
        Element launchSlot = Button(launchBtnText, () => onLaunchClient?.Invoke())
            .AutomationName(launchBtnText)
            .IsEnabled(interactive)
            .Padding(24, 8)
            .Margin(0, 0, 0, 8)
            .HAlign(HorizontalAlignment.Center);

        if (!interactive)
        {
            // 业务页不重复启动入口：结构保留（决定整组高度）、视觉隐藏、不可交互、不进无障碍树。
            launchSlot = launchSlot
                .IsHitTestVisible(false)
                .Opacity(0)
                .AccessibilityHidden();
        }

        Element actionSlot = extra is null
            ? launchSlot
            : Grid(
                [GridSize.Auto],
                [GridSize.Auto],
                [
                    launchSlot,
                    extra
                        .Margin(0, 0, 0, 0)
                        .VAlign(VerticalAlignment.Top)
                        .HAlign(HorizontalAlignment.Center),
                ]);

        Element waiting = HStack(
                12,
                ProgressRing()
                    .Width(16)
                    .Height(16)
                    .IsActive(interactive),
                Body(waitingText)
                    .Foreground(Theme.SecondaryText))
            .HAlign(HorizontalAlignment.Center);

        if (!interactive)
        {
            waiting = waiting
                .IsHitTestVisible(false)
                .Opacity(0)
                .AccessibilityHidden();
        }

        return Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                VStack(
                    16,
                    Image(AppAssets.LogoUri)
                        .Set(img => img.Source = LogoSource)
                        .Width(96)
                        .Height(96)
                        .AutomationName("Yuumi Logo")
                        .HAlign(HorizontalAlignment.Center)
                        .Margin(0, 0, 0, 4),
                    Title(notConnectedText)
                        .HAlign(HorizontalAlignment.Center),
                    Body(message)
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)
                        .Margin(0, 0, 0, 8),
                    actionSlot.HAlign(HorizontalAlignment.Center),
                    waiting)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center),
            ])
        .HAlign(HorizontalAlignment.Stretch)
        .VAlign(VerticalAlignment.Stretch)
        .Padding(32);
    }
}
