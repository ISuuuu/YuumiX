using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.App.Windowing;
using YuumiX.Core.Features.AramBench;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Pages.Bench;

/// <summary>
/// 大乱斗板凳席悬浮窗的视图（旧版 <c>views/BenchOverlay.vue</c> 的移植）。
///
/// 结构：拖动条 ｜ 英雄头像横排 ｜ 关闭按钮，无任何弹层。
/// 与旧版的交互差异：
/// <list type="bullet">
///   <item><b>不做文字提示</b>：旧版点置灰头像/换人失败会弹居中 toast（后改底部迷你条），
///         这里一律不弹——置灰态本身加悬停 tooltip 已经说明原因，浮窗只有 72px 高，
///         任何提示条都会盖住头像；换人失败在服务层记日志，不打扰界面；</item>
///   <item><b>点击反馈只落在头像自己身上</b>：被点的头像描边闪一下强调色（<see cref="ClickFeedbackMs"/>），
///         用来覆盖"请求已发出、头像还没换过来"的空窗期，不额外占版面；</item>
///   <item><b>hover 不改变布局盒</b>：旧版用 <c>scale(1.1)</c> + 主题色描边，浮层里缩放会带动
///         整行重排，观感上像是头像在"跳"（AGENTS.md §1.2 同源风险）。这里改为在同一位置上
///         叠加「强调色轻纱 + 高亮环」——尺寸、间距、DOM 结构全部不变，
///         LcuImage 不会卸载重绘，反馈面积却从 1px 描边扩大到整块头像；
///         <b>刻意不做手型光标</b>：WinUI 的 <c>ProtectedCursor</c> 是 protected 属性，Reactor
///         只能用 <c>.Set</c> 反射写入，而选人阶段 LCU 状态持续推送引起的每次重渲染都会把它
///         重置后再补写，实测指针在手型与默认箭头之间高频抖动，代价远大于收益；</item>
///   <item>不可用状态用 0.4 不透明度表达灰化（WinUI 没有 CSS 的 <c>grayscale()</c> 滤镜），
///         悬停时只提亮到 0.65 并给中性色高亮环——再亮就接近全亮，
///         会削弱「灰 = 不可用」的语义（点击仍会闪警告色），鼠标悬停的 tooltip 给出具体原因；</item>
///   <item>键盘焦点与鼠标悬停共用同一套高亮，两者状态分开记录，
///         避免「焦点还在、鼠标移出」把高亮一起抹掉。<b>注意</b>本悬浮窗宿主设了
///         <c>NoActivate</c>（<c>WS_EX_NOACTIVATE</c>），窗口从不激活、键盘焦点进不来，
///         因此 <c>focusedId</c> 目前不会真正生效，属于「窗口一旦支持激活即可用」的预留；
///         <c>.IsTabStop(true)</c> / <c>OnKeyDown</c> 则是 <c>REACTOR_A11Y_004</c> 的硬要求，不可删。</item>
/// </list>
/// </summary>
public sealed class AramBenchOverlayView : Component
{
    /// <summary>头像边长（DIP）。与旧版 <c>.champ-item</c> 的 44px 一致。</summary>
    private const double TileSize = 44;

    /// <summary>头像间距：8px（4 的倍数）。</summary>
    private const double TileGap = 8;

    /// <summary>拖动条宽度（DIP）。</summary>
    private const double GripWidth = 24;

    /// <summary>关闭按钮边长（DIP）。与旧版 <c>.close-btn</c> 的 24px 一致。</summary>
    private const double CloseSize = 24;

    /// <summary>不可用头像的不透明度。</summary>
    private const double DisabledOpacity = 0.4;

    /// <summary>
    /// 不可用头像在悬停时提亮到的不透明度。
    /// 只提到「能看清是哪个英雄」就够，不能接近 1.0——那样会让人误以为可以交换
    /// （实际点击会闪警告色），tooltip 也救不回被削弱的「灰 = 不可用」语义。
    /// </summary>
    private const double DisabledActiveOpacity = 0.65;

    /// <summary>悬停时叠在图标上的强调色轻纱不透明度：整块染色是最容易被察觉的面积变化。</summary>
    private const double HoverWashOpacity = 0.18;

    /// <summary>
    /// 高亮环粗细（DIP）。画在独立覆盖层上而不是承载头像的 Border，
    /// 因此不会把 44px 头像内缩裁掉一圈。
    ///
    /// <para>几何关系要算清楚：外层 Border 的 1px 描边占 0→1，覆盖层落在其内容区（42×42）内，
    /// 所以「外层 1px + 内环 1px」合成的视觉环带正好是 2px。两者悬停时同色，接缝不可见。
    /// 若这里写 2，实际视觉会变成 3px 环——比设计意图重一档。</para>
    /// </summary>
    private const double HighlightRingThickness = 1;

    /// <summary>
    /// 点击后描边保持强调色的时长（毫秒）。
    /// 只做颜色切换、不改描边粗细：1px→2px 会让 44px 的头像被内缩裁掉一圈。
    /// </summary>
    private const int ClickFeedbackMs = 400;

    public override Element Render()
    {
        var services = UseContext(AppHost.Context) ?? AppHost.Services;
        var palette = UseContext(AppHost.ThemeContext)?.Current ?? AppHost.Theme.Current;
        var bench = services.Services.GetRequiredService<IAramBenchService>();
        var logger = services.LoggerFactory.CreateLogger<AramBenchOverlayView>();

        // threadSafe: true —— 状态推送来自 LCU 后台线程（ADR-07）
        var (state, setState) = UseState(bench.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = bench.States.Subscribe(setState);
                return () => subscription.Dispose();
            },
            0);

        // 交互状态：悬停项、键盘焦点项、成功闪烁项、拒绝闪烁项
        var (hoveredId, setHoveredId) = UseState(0, threadSafe: true);
        var (focusedId, setFocusedId) = UseState(0, threadSafe: true);
        var (flashSuccessId, setFlashSuccessId) = UseState(0, threadSafe: true);
        var (flashDeniedId, setFlashDeniedId) = UseState(0, threadSafe: true);

        var (closeHovered, setCloseHovered) = UseState(false, threadSafe: true);
        var (gripHovered, setGripHovered) = UseState(false, threadSafe: true);

        var successVersion = UseRef(0);
        var deniedVersion = UseRef(0);

        void FlashSuccess(int championId)
        {
            int version = ++successVersion.Current;
            setFlashSuccessId(championId);
            setFlashDeniedId(0);

            _ = Task.Delay(ClickFeedbackMs).ContinueWith(
                _ =>
                {
                    if (successVersion.Current == version)
                    {
                        setFlashSuccessId(0);
                    }
                },
                TaskScheduler.Default);
        }

        void FlashDenied(int championId)
        {
            int version = ++deniedVersion.Current;
            setFlashDeniedId(championId);
            setFlashSuccessId(0);

            _ = Task.Delay(ClickFeedbackMs).ContinueWith(
                _ =>
                {
                    if (deniedVersion.Current == version)
                    {
                        setFlashDeniedId(0);
                    }
                },
                TaskScheduler.Default);
        }

        void RequestSwap(AramBenchChampion champion)
        {
            // 账号未拥有/不可用的英雄无法交换，直接拒绝并闪烁警告
            if (string.Equals(champion.Reason, "未拥有/不可用", StringComparison.Ordinal))
            {
                FlashDenied(champion.ChampionId);
                return;
            }

            // 若当前已经是点亮状态（保护期外或已解锁），点击即时给强调色高亮反馈
            if (champion.Clickable)
            {
                FlashSuccess(champion.ChampionId);
            }

            // 保护期内虽初始置灰，但点击依然发起交换请求去触发验证：
            // 若能换成功说明是属于我放上去的牌，官方会话确认后自动动态点亮为亮色；
            // 若不能换（属于队友手持或保护期中），超时后触发警告色闪烁提示不可换。
            _ = SwapQuietlyAsync(bench, champion.ChampionId, FlashDenied, logger);
        }

        var drag = UseWindowDragMove();

        var tiles = BuildTiles(
            state,
            hoveredId,
            focusedId,
            flashSuccessId,
            flashDeniedId,
            setHoveredId,
            setFocusedId,
            RequestSwap);

        // 显式标 Element：三元两支是不同元素类型，让编译器推断会报 CS0173
        Element benchStrip = tiles.Length == 0
            ? Caption("板凳席空空如也").Foreground(Theme.SecondaryText)
            : HStack(TileGap, tiles);

        var content = ScrollViewer(benchStrip)
            // 只允许横向滚动：竖排会出现半截头像，看起来比裁掉更糟
            .HorizontalScrollMode(ScrollMode.Enabled)
            .VerticalScrollMode(ScrollMode.Disabled)
            .Set(scroller =>
            {
                // 仅在真的放不下时才出现滚动条：常态下这条细线是多余的视觉噪音
                scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            })
            .VAlign(VerticalAlignment.Center)
            .Grid(column: 1);

        return Grid(
            [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            [GridSize.Star()],
            [
                BuildGrip(drag, gripHovered, setGripHovered).Grid(column: 0),
                content,
                BuildCloseButton(closeHovered, setCloseHovered).Grid(column: 2),
            ])
            .Padding(12, 0)
            .Background(Theme.SolidBackground)
            .WithBorder(Theme.CardStroke, 1)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .RequestedTheme(palette.ElementTheme);
    }

    /// <summary>
    /// 发起交换并把异常吃掉（服务层已按结果记日志）。
    /// </summary>
    private static async Task SwapQuietlyAsync(
        IAramBenchService bench,
        int championId,
        Action<int> onDenied,
        ILogger logger)
    {
        try
        {
            var outcome = await bench.SwapAsync(championId).ConfigureAwait(true);
            if (outcome is AramBenchSwapOutcome.Protected or AramBenchSwapOutcome.NotOwned or AramBenchSwapOutcome.Failed)
            {
                onDenied(championId);
            }
        }
        catch (OperationCanceledException)
        {
            // 关窗/切局导致的取消属正常路径
        }
        catch (Exception ex)
        {
            onDenied(championId);
            logger.LogWarning(ex, "板凳席交换请求异常: {ChampionId}", championId);
        }
    }

    private static Element[] BuildTiles(
        AramBenchState state,
        int hoveredId,
        int focusedId,
        int flashSuccessId,
        int flashDeniedId,
        Action<int> onHover,
        Action<int> onFocus,
        Action<AramBenchChampion> onTap)
    {
        var tiles = new Element[state.Champions.Count];
        for (int i = 0; i < state.Champions.Count; i++)
        {
            var champion = state.Champions[i];

            bool isSuccess = champion.ChampionId == flashSuccessId;
            bool isDenied = champion.ChampionId == flashDeniedId;
            bool isActive = champion.ChampionId == hoveredId || champion.ChampionId == focusedId;
            bool showRing = isActive || isSuccess || isDenied;
            bool showWash = isActive && champion.Clickable && !isSuccess && !isDenied;

            // 状态色：拒绝闪烁（警告色）＞ 成功闪烁（强调色）＞ 悬停/焦点（可换则强调色）＞ 默认描边
            // 只切画刷与不透明度，绝不动态增删 DOM 节点，避免 LcuImage 卸载导致的图片白闪。
            ThemeRef stroke = isDenied
                ? Theme.SystemCaution
                : isSuccess
                    ? Theme.Accent
                    : isActive
                        ? (champion.Clickable ? Theme.Accent : Theme.ControlStroke)
                        : (champion.Clickable ? Theme.CardStroke : Theme.ControlStroke);

            double opacity = champion.Clickable
                ? 1.0
                : (isActive ? DisabledActiveOpacity : DisabledOpacity);

            Element icon = Component<LcuImage, LcuImageProps>(new LcuImageProps(
                $"/lol-game-data/assets/v1/champion-icons/{champion.ChampionId}.png",
                TileSize,
                YuumiTheme.ControlRadius));

            // 悬停反馈全部落在「始终存在的覆盖层」上：
            // 轻纱给整块 44px 头像染色（面积变化远比 1px 描边换色醒目），
            // 高亮环补足轮廓（与外层 1px 描边同色，合成 2px 环带）；两层都不参与命中测试、不占布局空间、不内缩裁切头像。
            Element wash = Border(Empty())
                .CornerRadius(YuumiTheme.ControlRadius)
                .Background(Theme.Accent)
                .Opacity(showWash ? HoverWashOpacity : 0)
                .IsHitTestVisible(false);

            Element ring = Border(Empty())
                .CornerRadius(YuumiTheme.ControlRadius)
                .WithBorder(stroke, HighlightRingThickness)
                .Opacity(showRing ? 1 : 0)
                .IsHitTestVisible(false);

            var tile = Border(Grid([GridSize.Star()], [GridSize.Star()], [icon, wash, ring]))
                .Width(TileSize)
                .Height(TileSize)
                .CornerRadius(YuumiTheme.ControlRadius)
                .WithBorder(stroke, 1)
                .Opacity(opacity)
                // 图标钮必须给无障碍名，否则读屏只能念出"图像"（同 AGENTS §4 的按钮要求）
                .AutomationName(string.IsNullOrEmpty(champion.Reason)
                    ? $"板凳席英雄 {champion.ChampionId}"
                    : $"板凳席英雄 {champion.ChampionId}：{champion.Reason}")
                // 可点击容器必须可聚焦并响应 Enter/Space，否则键盘用户完全够不到（REACTOR_A11Y_004）
                .IsTabStop(true)
                .OnKeyDown((_, e) =>
                {
                    if (e.Key is VirtualKey.Enter or VirtualKey.Space)
                    {
                        onTap(champion);
                        e.Handled = true;
                    }
                })
                .OnPointerEntered((_, _) => onHover(champion.ChampionId))
                .OnPointerExited((_, _) => onHover(0))
                .OnGotFocus((_, _) => onFocus(champion.ChampionId))
                .OnLostFocus((_, _) => onFocus(0))
                // 点在头像上不应触发窗口拖动
                .Drag(false)
                .OnTapped((_, _) => onTap(champion));

            if (!string.IsNullOrEmpty(champion.Reason))
            {
                tile = tile.ToolTip(champion.Reason);
            }

            tiles[i] = tile;
        }

        return tiles;
    }

    /// <summary>
    /// 拖动条。旧版是 20px 宽的 <c>.drag-handle</c> + <c>data-tauri-drag-region</c>。
    ///
    /// 用点阵而不是字形：Segoe 的 gripper 字形在不同系统版本上字形名不一致，
    /// 直接画三根 12×2 的圆头短线，任何字体缺失都不影响。
    /// </summary>
    private static Element BuildGrip(Action drag, bool isHovered, Action<bool> setHovered)
    {
        var grip = Border(VStack(2, [GripBar(isHovered), GripBar(isHovered), GripBar(isHovered)]))
            .Width(GripWidth)
            .Height(TileSize)
            .CornerRadius(YuumiTheme.ControlRadius)
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center)
            .ToolTip("按住拖动悬浮窗")
            .AutomationName("拖动大乱斗悬浮窗")
            .OnPointerEntered((_, _) => setHovered(true))
            .OnPointerExited((_, _) => setHovered(false))
            .OnPointerPressed((_, _) => drag())
            .IsDragRegion(true);

        return isHovered ? grip.Background(Theme.SubtleFill) : grip;
    }

    private static Element GripBar(bool isHovered) =>
        Border(Empty())
            .Width(12)
            .Height(2)
            .CornerRadius(YuumiTheme.PillRadius)
            .Background(isHovered ? Theme.PrimaryText : Theme.SecondaryText);

    private static Element BuildCloseButton(bool isHovered, Action<bool> setHovered)
    {
        var btn = Border(Caption(Icons.Cancel)
                .FontFamily(Icons.FontFamily)
                .Foreground(isHovered ? Theme.PrimaryText : Theme.SecondaryText))
            .Width(CloseSize)
            .Height(CloseSize)
            .CornerRadius(YuumiTheme.ControlRadius)
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center)
            .ToolTip("本次选人不再显示")
            .AutomationName("隐藏大乱斗悬浮窗")
            .IsTabStop(true)
            .OnKeyDown((_, e) =>
            {
                if (e.Key is VirtualKey.Enter or VirtualKey.Space)
                {
                    AramBenchWindowHost.Dismiss();
                    e.Handled = true;
                }
            })
            .OnPointerEntered((_, _) => setHovered(true))
            .OnPointerExited((_, _) => setHovered(false))
            .Drag(false)
            .OnTapped((_, _) => AramBenchWindowHost.Dismiss());

        return isHovered ? btn.Background(Theme.SubtleFill) : btn;
    }
}
