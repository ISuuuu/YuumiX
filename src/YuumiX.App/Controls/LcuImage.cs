using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.Core.Assets;

namespace YuumiX.App.Controls;

/// <summary>图标组件入参。<see cref="Size"/> 为 DIP 边长（正方形图标）。</summary>
public sealed record LcuImageProps(string AssetPath, double Size = 40, double CornerRadius = 4);

/// <summary>
/// 统一的 LCU 资源图片组件（ADR-01 / ADR-11）。
///
/// 页面里禁止再出现「自己下载字节并塞给 BitmapImage」的写法：那会让每个列表项
/// 都变成命令式代码，也让缓存与并发控制散落在各处。这里只做三件事——
/// 解析路径、显示图片、失败时给占位。
///
/// 占位符是刻意的：资源缺失（客户端未启动、CDragon 无此 ID）在真实使用中是常态，
/// 空白区域会让用户以为布局坏了，一个明确的灰色方块才说明「这里本该有图」。
///
/// 为什么没有独立的 <c>UseLcuAsset</c> hook：Reactor 的 <c>UseState</c>/<c>UseEffect</c>/<c>UseContext</c>
/// 都是 <see cref="Component"/> 的 **protected** 成员，外部类型（含扩展方法）无法调用，
/// 组件之间也没有公开的 RenderContext 入口。因此可复用的最小单元就是本组件本身——
/// 需要的页面直接放一个 <see cref="LcuImage"/>，不再各自写一套加载逻辑。
/// </summary>
public sealed class LcuImage : Component<LcuImageProps>
{
    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var assets = services.Services.GetRequiredService<AssetService>();

        // threadSafe: true —— 完成回调来自线程池，滚动一屏会有几十个回调同时到达（ADR-07）
        var (uri, setUri) = UseState<string?>(null, threadSafe: true);
        var (failed, setFailed) = UseState(false, threadSafe: true);

        UseEffect(
            () =>
            {
                setUri(null);
                setFailed(false);

                var cancellation = new CancellationTokenSource();
                _ = ResolveAsync(assets, setUri, setFailed, cancellation.Token);

                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                };
            },
            Props.AssetPath);

        if (uri is null)
        {
            return Border(TextBlock(failed ? "\uE783" : string.Empty)
                    .FontSize(Props.Size * 0.4)
                    .Foreground(Theme.SecondaryText))
                .Width(Props.Size)
                .Height(Props.Size)
                .CornerRadius(Props.CornerRadius)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke, 1)
                .AutomationName($"图标占位：{Props.AssetPath}");
        }

        Element img = Image(uri)
            .Width(Props.Size)
            .Height(Props.Size)
            .AutomationName($"图标：{Props.AssetPath}");

        if (Props.CornerRadius > 0)
        {
            return Border(img)
                .Width(Props.Size)
                .Height(Props.Size)
                .CornerRadius(Props.CornerRadius);
        }

        return img;
    }

    private async Task ResolveAsync(
        AssetService assets,
        Action<string?> setUri,
        Action<bool> setFailed,
        CancellationToken cancellationToken)
    {
        try
        {
            var localPath = await assets.GetCachedPathAsync(Props.AssetPath, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (localPath is null)
            {
                setFailed(true);
                return;
            }

            // Windows 路径 → file:///C:/...；直接拼 "file://" + 路径在含空格或中文时是非法 URI
            // （本机数据目录就在中文用户名下）。
            setUri(new Uri(localPath).AbsoluteUri);
        }
        catch (OperationCanceledException)
        {
            // 组件卸载或路径切换，属正常路径
        }
        catch (Exception)
        {
            // 一张图失败不应让整页渲染抛异常
            setFailed(true);
        }
    }
}
