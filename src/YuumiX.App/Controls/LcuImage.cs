using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Media.Imaging;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.Core.Assets;

using YuumiX.App.Theming;

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
        var services = UseContext(AppHost.Context) ?? AppHost.Services;
        var assets = services.Services.GetRequiredService<AssetService>();

        var cachedPath = assets.TryGetCachedPath(Props.AssetPath);
        var initialUri = cachedPath is not null ? new Uri(cachedPath).AbsoluteUri : null;

        // threadSafe: true —— 完成回调来自线程池，滚动一屏会有几十个回调同时到达（ADR-07）
        var (uri, setUri) = UseState<string?>(initialUri, threadSafe: true);
        var (failed, setFailed) = UseState(false, threadSafe: true);

        UseEffect(
            () =>
            {
                var cached = assets.TryGetCachedPath(Props.AssetPath);
                if (cached is not null)
                {
                    setUri(new Uri(cached).AbsoluteUri);
                    setFailed(false);
                    return static () => { };
                }

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
            return Border(TextBlock(failed ? Icons.Error : string.Empty)
                    .FontFamily(Icons.FontFamily)
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
            .AutomationName($"图标：{Props.AssetPath}")
            .Set(image =>
            {
                // LCU 图标源图（英雄头像原生 120px+）远大于显示边长，不限制解码尺寸时
                // 由 GPU 按非整数倍双线性缩小，轮廓边缘发虚发糊。按目标逻辑边长预解码
                // （DecodePixelType.Logical 默认值，含 DPI 归算，WIC 高质量插值），
                // 渲染端 1:1 贴图，边缘保持干净。
                // Set 每次重渲染都会重放（§5.12），已设置过解码尺寸就不再写。
                if (image.Source is not BitmapImage bmp || bmp.DecodePixelWidth > 0)
                {
                    return;
                }

                // 只约束宽度、不设 DecodePixelHeight，非正方形源图可保持纵横比不被拉变形。
                bmp.DecodePixelWidth = Math.Max(1, (int)Math.Round(Props.Size));
            });

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
