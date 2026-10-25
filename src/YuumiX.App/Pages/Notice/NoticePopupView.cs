using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Notice;

namespace YuumiX.App.Pages.Notice;

/// <summary>公告弹窗入参。<see cref="OnClose"/> 由外壳提供，用于关闭弹窗。</summary>
public sealed record NoticePopupProps(Action OnClose);

/// <summary>
/// 公告弹窗的加载状态。
/// </summary>
public enum NoticeStatus
{
    /// <summary>正在拉取 NOTICE.md。</summary>
    Loading,

    /// <summary>拉取成功（含缓存兜底），展示 Markdown 内容。</summary>
    Loaded,

    /// <summary>拉取失败且无缓存可用，提供重试。</summary>
    Failed,
}

/// <summary>
/// 右下角公告弹窗，复刻旧版 <c>NoticePopup.vue</c> 的形态与交互：
///
/// <list type="bullet">
///   <item><description>320px 宽卡片吸右下角（旧版 <c>right: 20px; bottom: 20px</c>），圆角 14；</description></item>
///   <item><description>标题栏：铃铛图标 + 「公告」，主题色 10% 透明度底（旧版 primary-color-alpha-10）；</description></item>
///   <item><description>正文：经 <see cref="NoticeService"/> 拉取 NOTICE.md（白名单 + 代理 + 缓存兜底），
///         用 <see cref="NoticeMarkdown"/> 渲染，最高 240px 后滚动（旧版 max-height: 240px）；</description></item>
///   <item><description>加载失败给重试按钮；打开 8 秒后自动关闭（旧版 AUTO_DISMISS_MS），重试会重置计时。</description></item>
/// </list>
///
/// 与旧版的差异：定时器从 setTimeout 换成 Task.Delay；新增缓存兜底提示条（能力继承自 NoticeService）。
/// </summary>
public sealed class NoticePopupView : Component<NoticePopupProps>
{
    /// <summary>自动关闭时长（旧版 AUTO_DISMISS_MS = 8000）。</summary>
    private static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(8);

    public override Element Render()
    {
        var services = UseContext(AppHost.Context) ?? AppHost.Services;
        var themeState = UseContext(AppHost.ThemeContext);

        // 主题色取「谁用谁取」约定（ADR-12）：读不到主题上下文时退回默认调色板。
        var accent = (themeState?.Current ?? YuumiTheme.Default).Accent;
        var accentAlpha10 = WithAlpha(accent, 0x1A); // 10% 透明度（旧版 --primary-color-alpha-10）。

        var (attempt, setAttempt) = UseState(0);
        var (status, setStatus) = UseState(NoticeStatus.Loading, threadSafe: true);
        var (content, setContent) = UseState<NoticeContent?>(null, threadSafe: true);

        // attempt 变化（首次打开 / 点击重试）时重新拉取公告并重置自动关闭计时。
        // 拉取在后台线程，set 系列用 threadSafe 封送回 UI 线程（ADR-07）。
        UseEffect(
            () =>
            {
                var cancellation = new CancellationTokenSource();
                var dismissCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var notice = await services.NoticeService
                            .GetNoticeAsync(cancellation.Token)
                            .ConfigureAwait(false);

                        if (cancellation.IsCancellationRequested)
                        {
                            return;
                        }

                        setContent(notice);
                        setStatus(NoticeStatus.Loaded);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception)
                    {
                        if (!cancellation.IsCancellationRequested)
                        {
                            setStatus(NoticeStatus.Failed);
                        }
                    }
                });

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(AutoDismissDelay, dismissCts.Token).ConfigureAwait(false);
                        Props.OnClose();
                    }
                    catch (OperationCanceledException)
                    {
                        // 正常路径：用户手动关闭 / 重试 / 组件卸载时取消计时。
                    }
                });

                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                    dismissCts.Cancel();
                    dismissCts.Dispose();
                };
            },
            attempt);

        // ── 标题栏：铃铛 + 标题 + 关闭（旧版 notice-popup-header）──
        var header = Border(
                HStack(7,
                    Body("\uE7E7")
                        .FontFamily("Segoe Fluent Icons")
                        .Foreground(accent)
                        .VAlign(VerticalAlignment.Center),
                    BodyStrong("公告")
                        
                        .Foreground(Theme.Ref("TextFillColorPrimaryBrush"))
                        .VAlign(VerticalAlignment.Center),
                    // 弹性占位：把关闭按钮推到最右（旧版 justify-content: space-between）。
                    (Element)Empty().HAlign(HorizontalAlignment.Stretch),
                    Button(
                        Caption("\uE711")
                            .FontFamily("Segoe Fluent Icons")
                            .Foreground(Theme.Ref("TextFillColorSecondaryBrush")),
                        () => Props.OnClose())
                    .SubtleButton()
                    .Width(22)
                    .Height(22)
                    .CornerRadius(6)
                    .AutomationName("关闭公告")))
            .Padding(new Thickness(14, 10, 8, 10))
            .Background(accentAlpha10)
            .WithBorder(Theme.DividerStroke, 1)
            // WinUI 的 Border 不像 CSS overflow:hidden 那样裁剪子内容，
            // 标题栏需要自己收掉上侧两个圆角，避免方角顶出 14px 圆角卡片。
            .CornerRadius(14, 14, 0, 0);

        // ── 正文：加载中 / 失败 / 内容三态（旧版 notice-popup-body）──
        Element body = status switch
        {
            NoticeStatus.Loaded => RenderContentBody(content, accent),

            NoticeStatus.Failed => VStack(10,
                    Caption("公告获取失败，请检查网络或代理设置")
                        .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                        .TextWrapping(),
                    Button("重试", () => setAttempt(attempt + 1))
                        .CornerRadius(6)
                        .Padding(new Thickness(16, 4, 16, 4)))
                .HAlign(HorizontalAlignment.Center)
                .Padding(new Thickness(16, 24, 16, 24)),

            _ => VStack(10,
                    ProgressRing().Width(22).Height(22),
                    Caption("正在获取公告…")
                        .Foreground(Theme.Ref("TextFillColorSecondaryBrush")))
                .HAlign(HorizontalAlignment.Center)
                .Padding(new Thickness(16, 24, 16, 24)),
        };

        // ── 卡片本体：320px 宽、圆角 14，吸右下角 20px（旧版 notice-popup）──
        return VStack(0, header, body)
            .Width(320)
            .CornerRadius(14)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Bottom)
            .Margin(new Thickness(0, 0, 20, 20));
    }

    /// <summary>内容态正文：缓存提示条（可选）+ Markdown 滚动区（旧版 markdown-body）。</summary>
    private static Element RenderContentBody(NoticeContent? content, string accent)
    {
        var blocks = new List<Element>();

        if (content?.FromCache == true)
        {
            blocks.Add(Border(
                    HStack(6,
                        Caption("\uE7BA")
                            .FontFamily("Segoe Fluent Icons")
                            .Foreground(Theme.Ref("SystemFillColorCautionBrush"))
                            .VAlign(VerticalAlignment.Center),
                        Caption("网络获取失败，显示上次缓存内容")
                            .Foreground(Theme.Ref("TextFillColorSecondaryBrush"))
                            .VAlign(VerticalAlignment.Center)))
                .Padding(new Thickness(14, 6, 14, 6))
                .Background(Theme.Ref("SubtleFillColorSecondaryBrush")));
        }

        blocks.Add(
            ScrollViewer(
                    VStack(7, NoticeMarkdown.RenderBlocks(content?.Markdown ?? string.Empty))
                        .Padding(new Thickness(14, 12, 14, 12)))
                // 滚动容器焦点约定：阻止点击正文时把焦点下发给第一个可聚焦控件（见项目记忆）。
                // 用修饰符而非 .Set(...)：ScrollViewer 走池回收，Set 写入会在重渲染时被重置（REACTOR_POOL_001）。
                .IsTabStop(true)
                .MaxHeight(240));

        return VStack(0, blocks.ToArray());
    }

    /// <summary>
    /// 把 8 位 <c>#AARRGGBB</c> 的 alpha 通道替换为指定值。
    /// 输入由 <see cref="YuumiPalette"/> 归一化过，长度恒为 9。
    /// </summary>
    private static string WithAlpha(string argb, byte alpha) =>
        $"#{alpha:X2}{argb.AsSpan(3)}";
}
