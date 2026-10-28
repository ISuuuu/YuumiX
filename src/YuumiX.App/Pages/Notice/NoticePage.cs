using System.Text;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.Core.Features.Notice;

using YuumiX.App.Theming;

namespace YuumiX.App.Pages.Notice;

/// <summary>
/// 公告页：侧边栏「公告」入口的落点。
///
/// 对应旧 NoticePopup.vue（右下角弹窗）。差异说明：旧版把公告做成悬浮弹窗，
/// 新版按迁移架构把「公告」定为独立路由页，展示语义保持一致——
/// 进入即拉取 GitHub 仓库的 NOTICE.md 最新内容，加载/错误/重试三态齐全。
///
/// 状态机（对齐旧实现）：
///   加载中 → 内容（成功）/ 错误（失败，且无缓存兜底）；
///   失败但缓存可用时直接展示缓存内容并给出提示条（旧版没有缓存兜底，属新增能力）。
/// </summary>
public sealed class NoticePage : Component
{
    private sealed record NoticeViewState(bool IsLoading, string? Markdown = null, bool FromCache = false, string? Error = null);

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);

        // threadSafe: true —— 拉取在后台线程完成，回调直接封送回 UI 线程（ADR-07）。
        // 初值只被首次渲染读取，必须用 UseMemo 固定实例，否则每次渲染都会新分配一个 state 对象。
        var initialState = UseMemo(() => new NoticeViewState(IsLoading: true), []);
        var (view, setView) = UseState(initialState, threadSafe: true);
        var (reloadTick, setReloadTick) = UseState(0);

        // 首次挂载与「重试 / 刷新」（reloadTick 递增）都会触发拉取。
        UseEffect(
            () =>
            {
                var noticeService = services.NoticeService;
                var cancellation = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var content = await noticeService
                            .GetNoticeAsync(cancellation.Token)
                            .ConfigureAwait(false);

                        if (!cancellation.IsCancellationRequested)
                        {
                            setView(new NoticeViewState(false, content.Markdown, content.FromCache));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        if (!cancellation.IsCancellationRequested)
                        {
                            setView(new NoticeViewState(false, Error: ex.Message));
                        }
                    }
                });

                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                };
            },
            reloadTick);

        void Reload()
        {
            setView(new NoticeViewState(IsLoading: true));
            setReloadTick(reloadTick + 1);
        }

        return view.IsLoading
            ? RenderLoading()
            : view.Error is not null
                ? RenderError(view.Error, Reload)
                : RenderContent(view, Reload);
    }

    /// <summary>加载态：对齐旧 notice-popup-status 的 spinner + 提示。</summary>
    private static Element RenderLoading()
    {
        return VStack(
            12,
            ProgressRing()
                .Width(24)
                .Height(24)
                .IsActive(true),
            Body("正在获取最新公告...")
                .Foreground(Theme.SecondaryText))
        .HAlign(HorizontalAlignment.Center)
        .VAlign(VerticalAlignment.Center);
    }

    /// <summary>错误态：对齐旧 notice-popup-status 的错误提示 + 重试按钮。</summary>
    private static Element RenderError(string message, Action retry)
    {
        return VStack(
            12,
            Subtitle(Icons.Error)
                .FontFamily(Icons.FontFamily)
                .Foreground(Theme.Ref("SystemFillColorCriticalBrush"))
                .HAlign(HorizontalAlignment.Center),
            BodyStrong("公告获取失败")
                .HAlign(HorizontalAlignment.Center),
            Caption(message)
                .Foreground(Theme.SecondaryText)
                .TextWrapping()
                .MaxWidth(420)
                .HAlign(HorizontalAlignment.Center),
            Button("重试", retry)
                .Padding(24, 6))
        .HAlign(HorizontalAlignment.Center)
        .VAlign(VerticalAlignment.Center);
    }

    /// <summary>内容态：页头（标题 + 刷新）+ 缓存提示（可选）+ 公告卡片。</summary>
    private static Element RenderContent(NoticeViewState view, Action reload)
    {
        var header = PageHeaders.PageHeader(
            "公告",
            description: "内容来自 GitHub 仓库 NOTICE.md，进入页面时自动获取最新",
            actions: Button(Body("刷新"), reload)
                .AutomationName("刷新")
                .Padding(16, 5),
            includeBottomMargin: false);

        var blocks = new List<Element>();
        if (view.FromCache)
        {
            blocks.Add(Border(
                    HStack(
                        8,
                        Caption(Icons.Warning)
                            .FontFamily(Icons.FontFamily)
                            .Foreground(Theme.Ref("SystemFillColorCautionBrush"))
                            .VAlign(VerticalAlignment.Center),
                        Caption("网络获取失败，当前显示上次缓存的公告内容")
                            .Foreground(Theme.SecondaryText)
                            .VAlign(VerticalAlignment.Center)))
                .Padding(12, 8)
                .CornerRadius(YuumiTheme.OverlayRadius)
                .Background(Theme.Ref("SubtleFillColorSecondaryBrush")));
        }

        blocks.Add(Border(
                VStack(12, NoticeMarkdown.RenderBlocks(view.Markdown ?? string.Empty)))
            .Padding(24)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1));

        return (ScrollViewer(
            VStack(16, [header, .. blocks]).Padding(28).HAlign(HorizontalAlignment.Stretch)) with
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            })
            .HorizontalScrollMode(ScrollMode.Disabled)
            .IsTabStop(true);
    }
}

/// <summary>
/// 公告 Markdown 的轻量渲染器：已收敛委托给统一的 <see cref="MarkdownView"/>。
/// </summary>
internal static class NoticeMarkdown
{
    /// <summary>渲染为按块排列的元素序列（VStack 子项）。</summary>
    public static Element[] RenderBlocks(string markdown)
    {
        return MarkdownView.RenderBlockElements(markdown);
    }

    /// <summary>行内解析：对外保留兼容签名。</summary>
    internal static List<RichTextInline> ParseInlines(string text)
    {
        return MarkdownView.ParseInlines(text);
    }
}
