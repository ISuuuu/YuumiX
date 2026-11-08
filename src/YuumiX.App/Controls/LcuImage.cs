using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Media.Imaging;
using YuumiX.App.Composition;
using YuumiX.App.Theming;
using YuumiX.Core.Assets;
using static Microsoft.UI.Reactor.Factories;

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
    /// <summary>
    /// 超采样上限（DIP）：边长不超过该值的图标按 2 倍解码再缩回显示，
    /// 让小尺寸圆形/圆角边缘拿到更多灰度层次。更大的图自身像素充足，不值得多占 4 倍内存。
    /// </summary>
    private const double SupersampleMaxSize = 48;

    public override Element Render()
    {
        var services = UseContext(AppHost.Context) ?? AppHost.Services;
        var assets = services.Services.GetRequiredService<AssetService>();

        // 优先在渲染期同步检查磁盘缓存：若已命中，首帧直接拿到绝对 URI 渲染，
        // 消除组件在同一表格槽位复用时的旧图残留与延迟多帧重绘（ADR-01/ADR-11）。
        var cachedPath = !string.IsNullOrEmpty(Props.AssetPath) ? assets.TryGetCachedPath(Props.AssetPath) : null;
        var (asyncState, setAsyncState) = UseState<(string Path, string? Uri, bool Failed)>(
            (Props.AssetPath, cachedPath is not null ? new Uri(cachedPath).AbsoluteUri : null, false),
            threadSafe: true);

        string? uri;
        bool failed;

        if (cachedPath is not null)
        {
            uri = new Uri(cachedPath).AbsoluteUri;
            failed = false;
        }
        else if (string.Equals(asyncState.Path, Props.AssetPath, StringComparison.Ordinal))
        {
            uri = asyncState.Uri;
            failed = asyncState.Failed;
        }
        else
        {
            // 路径切换且尚未缓存：当前帧立即给出干净占位，禁止串用上一路径的旧图
            uri = null;
            failed = false;
        }

        UseEffect(
            () =>
            {
                if (string.IsNullOrEmpty(Props.AssetPath))
                {
                    return static () => { };
                }

                var cached = assets.TryGetCachedPath(Props.AssetPath);
                if (cached is not null)
                {
                    return static () => { };
                }

                var cancellation = new CancellationTokenSource();
                _ = ResolveAsync(assets, Props.AssetPath, setAsyncState, cancellation.Token);

                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                };
            },
            Props.AssetPath);

        if (uri is null)
        {
            Element placeholder = Border(TextBlock(failed ? Icons.Error : string.Empty)
                    .FontFamily(Icons.FontFamily)
                    .FontSize(Props.Size * 0.4)
                    .Foreground(Theme.SecondaryText))
                .Width(Props.Size)
                .Height(Props.Size)
                .CornerRadius(Props.CornerRadius)
                .Background(Theme.CardBackground);

            // 调用方传 CornerRadius 0 表示「裁剪与描边由外层统一负责」（如侧边栏圆形头像），
            // 此时占位不能再画自己的描边，否则两层描边会叠成双线。
            if (Props.CornerRadius > 0)
            {
                placeholder = placeholder.WithBorder(Theme.CardStroke, 1);
            }

            return placeholder.AutomationName($"图标占位：{Props.AssetPath}");
        }

        Element img = Image(uri)
            .Width(Props.Size)
            .Height(Props.Size)
            .AutomationName($"图标：{Props.AssetPath}")
            .Set(image =>
            {
                // LCU 图标源图（英雄头像原生 120px+）远大于显示边长，不限制解码尺寸时
                // 由 GPU 按非整数倍双线性缩小，轮廓边缘发虚发糊。按目标逻辑边长预解码，
                // 渲染端 1:1 贴图，边缘保持干净。
                //
                // DecodePixelType 必须显式设 Logical：BitmapImage 的默认值是 Physical（官方文档），
                // 按物理像素解码。100% 缩放下两者等价，但 125% / 150% 缩放下 32 物理像素会被
                // 拉伸到 40 / 48 物理像素显示——整张图标连同圆形边缘一起发虚。
                //
                // 小尺寸再按 2 倍超采样解码（32 → 64），交给 D2D 下采样到目标尺寸：
                // 圆形边缘因此能拿到更多灰度层次，抗锯齿过渡比 1:1 贴图更平滑。
                // 大图本身像素充足，不超采样（否则白占 4 倍内存）。
                // Set 每次重渲染都会重放（§5.12），已设置过解码尺寸就不再写。
                if (image.Source is not BitmapImage bmp || bmp.DecodePixelWidth > 0)
                {
                    return;
                }

                // 只约束宽度、不设 DecodePixelHeight，非正方形源图可保持纵横比不被拉变形。
                bmp.DecodePixelType = DecodePixelType.Logical;
                int sample = Props.Size <= SupersampleMaxSize ? 2 : 1;
                bmp.DecodePixelWidth = Math.Max(1, (int)Math.Round(Props.Size * sample));
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

    private static async Task ResolveAsync(
        AssetService assets,
        string assetPath,
        Action<(string Path, string? Uri, bool Failed)> setAsyncState,
        CancellationToken cancellationToken)
    {
        try
        {
            var localPath = await assets.GetCachedPathAsync(assetPath, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (localPath is null)
            {
                setAsyncState((assetPath, null, true));
                return;
            }

            // Windows 路径 → file:///C:/...；直接拼 "file://" + 路径在含空格或中文时是非法 URI
            // （本机数据目录就在中文用户名下）。
            setAsyncState((assetPath, new Uri(localPath).AbsoluteUri, false));
        }
        catch (OperationCanceledException)
        {
            // 组件卸载或路径切换，属正常路径
        }
        catch (Exception)
        {
            // 一张图失败不应让整页渲染抛异常
            setAsyncState((assetPath, null, true));
        }
    }
}
