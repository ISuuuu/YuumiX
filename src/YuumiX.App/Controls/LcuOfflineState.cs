using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;

namespace YuumiX.App.Controls;

/// <summary>
/// LCU 未连接时的整页占位，对齐旧系统 <c>LcuOfflineState.vue</c>：
/// 居中的手柄图标 + 「请先启动英雄联盟客户端」，内容区整页替换。
///
/// <para><b>为什么三页共用一处实现</b></para>
/// 旧版 Career / Search / GameInfo / TFT 各自 <c><LcuOfflineState v-if="!store.isConnected" /></c>，
/// 组件本身是同一个，只有文案可以覆盖。「客户端没开」这件事对用户只有一种含义，
/// 因此本版也只允许一种写法：图标 + 一句提示，需要额外动作时通过 <c>extra</c> 传入。
///
/// <para><b>不在这里放「启动客户端」按钮</b></para>
/// 启动入口由主页承担（<c>HomePage.RenderDisconnectedView</c>），重复放会让同一动作
/// 在四个页面各有一个入口，状态却互不知情。对局信息的「预览演示对局」是一个例外：
/// 它不依赖客户端，属于本页自带的能力，因此由调用方作为 <c>extra</c> 传入。
/// </summary>
public static class LcuOfflineState
{
    /// <summary>默认提示文案（旧系统 <c>gameInfo.launchLolPrompt</c>）。</summary>
    public const string DefaultMessage = "请先启动英雄联盟客户端";

    /// <param name="message">主提示文案；省略时用 <see cref="DefaultMessage"/>。</param>
    /// <param name="extra">附加在提示下方的元素（例如不依赖客户端的本地操作按钮）。</param>
    public static Element Render(string? message = null, Element? extra = null)
    {
        var items = new List<Element>
        {
            TitleLarge("🎮")
                .HAlign(HorizontalAlignment.Center)
                .AccessibilityHidden(),
            Body(message ?? DefaultMessage)
                .Foreground(Theme.SecondaryText)
                .HAlign(HorizontalAlignment.Center),
        };

        if (extra is not null)
        {
            items.Add(extra);
        }

        return Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                VStack(16, items.ToArray())
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 0),
            ])
        .HAlign(HorizontalAlignment.Stretch)
        .VAlign(VerticalAlignment.Stretch)
        .Padding(32);
    }
}
