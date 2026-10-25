using System.Text;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.Core.Features.Notice;

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
            Subtitle("\uE783")
                .FontFamily("Segoe Fluent Icons")
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
        var header = Grid(
            [GridSize.Star()],
            [GridSize.Star(), GridSize.Auto],
            [
                VStack(
                    4,
                    Title("公告"),
                    Caption("内容来自 GitHub 仓库 NOTICE.md，进入页面时自动获取最新"))
                .Grid(row: 0, column: 0),

                Button("刷新", reload)
                    .Padding(16, 5)
                    .VAlign(VerticalAlignment.Top)
                    .Grid(row: 0, column: 1),
            ]);

        var blocks = new List<Element>();
        if (view.FromCache)
        {
            blocks.Add(Border(
                    HStack(
                        8,
                        Caption("\uE7BA")
                            .FontFamily("Segoe Fluent Icons")
                            .Foreground(Theme.Ref("SystemFillColorCautionBrush"))
                            .VAlign(VerticalAlignment.Center),
                        Caption("网络获取失败，当前显示上次缓存的公告内容")
                            .Foreground(Theme.SecondaryText)
                            .VAlign(VerticalAlignment.Center)))
                .Padding(12, 8)
                .CornerRadius(6)
                .Background(Theme.Ref("SubtleFillColorSecondaryBrush")));
        }

        blocks.Add(Border(
                VStack(12, NoticeMarkdown.RenderBlocks(view.Markdown ?? string.Empty)))
            .Padding(24)
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1));

        return ScrollViewer(
            VStack(16, [header, .. blocks]).Padding(28).HAlign(HorizontalAlignment.Stretch));
    }
}

/// <summary>
/// 公告 Markdown 的轻量渲染器：把 NOTICE.md 翻译成 Reactor 元素。
///
/// 对应旧 utils/markdown.ts（marked + DOMPurify 渲染成 HTML）。公告内容是仓库自留的
/// 受控文本（非用户输入），因此不需要 HTML 消毒；渲染子集按 NOTICE.md 实际用到的语法取：
/// <list type="bullet">
///   <item>标题 # / ## / ###（对应旧 markdown-body 的 h1-h3 字号梯度）；</item>
///   <item>段落与软换行（连续非空行合并为一段）；</item>
///   <item>无序 / 有序列表（每项一个块，对齐旧 li 的 margin）；</item>
///   <item>行内 <c>**加粗**</c>、<c>`代码`</c>、<c>[链接](url)</c>。</item>
/// </list>
/// 已知限制：Reactor 的行内 Run 不支持独立字体族/前景色，`代码` 以加粗近似旧版样式。
/// </summary>
internal static class NoticeMarkdown
{
    private enum BlockKind { Heading1, Heading2, Heading3, Paragraph, ListItem }

    private sealed record Block(BlockKind Kind, string Text, string? Marker = null);

    /// <summary>渲染为按块排列的元素序列（VStack 子项）。</summary>
    public static Element[] RenderBlocks(string markdown)
    {
        return ParseBlocks(markdown).Select(RenderBlock).ToArray();
    }

    private static List<Block> ParseBlocks(string markdown)
    {
        var blocks = new List<Block>();
        var paragraph = new StringBuilder();

        foreach (string rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.TrimEnd();

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            // 标题：允许 h1-h6 语法，h4 及以下按 h3 呈现（公告实际只用到 h1-h2）。
            int headingLevel = 0;
            while (headingLevel < line.Length && line[headingLevel] == '#')
            {
                headingLevel++;
            }

            if (headingLevel is >= 1 and <= 6
                && headingLevel < line.Length
                && line[headingLevel] == ' ')
            {
                FlushParagraph();
                string text = line[(headingLevel + 1)..].Trim();
                blocks.Add(new Block(
                    headingLevel switch
                    {
                        1 => BlockKind.Heading1,
                        2 => BlockKind.Heading2,
                        _ => BlockKind.Heading3,
                    },
                    text));
                continue;
            }

            // 无序列表：- / * / + 前缀。
            if (line.Length > 2 && line[0] is '-' or '*' or '+' && line[1] == ' ')
            {
                FlushParagraph();
                blocks.Add(new Block(BlockKind.ListItem, line[2..].Trim(), "•"));
                continue;
            }

            // 有序列表：数字 + . / ) 前缀。
            int digits = 0;
            while (digits < line.Length && char.IsDigit(line[digits]))
            {
                digits++;
            }

            if (digits > 0
                && digits + 1 < line.Length
                && line[digits] is '.' or ')'
                && line[digits + 1] == ' ')
            {
                FlushParagraph();
                blocks.Add(new Block(BlockKind.ListItem, line[(digits + 2)..].Trim(), line[..digits]));
                continue;
            }

            // 段落：markdown 软换行按空格拼接（对齐 marked 的默认行为）。
            if (paragraph.Length > 0)
            {
                paragraph.Append(' ');
            }

            paragraph.Append(line.Trim());
        }

        FlushParagraph();
        return blocks;

        void FlushParagraph()
        {
            if (paragraph.Length > 0)
            {
                blocks.Add(new Block(BlockKind.Paragraph, paragraph.ToString()));
                paragraph.Clear();
            }
        }
    }

    private static Element RenderBlock(Block block)
    {
        return block.Kind switch
        {
            // 标题层级按 AGENTS.md §1 的语义工厂收敛：h1 → BodyLarge、h2/h3 → BodyStrong。
            BlockKind.Heading1 => BodyLarge(block.Text).SemiBold().TextWrapping(),
            BlockKind.Heading2 => BodyStrong(block.Text).TextWrapping(),
            BlockKind.Heading3 => BodyStrong(block.Text).TextWrapping(),
            BlockKind.ListItem => RenderRichText($"{block.Marker}  {block.Text}"),
            _ => RenderRichText(block.Text),
        };
    }

    /// <summary>段落与列表项共用：单段富文本（正文取 RichTextBlock 默认 Body 字号，行距对齐旧 1.7 ≈ 22px）。</summary>
    private static Element RenderRichText(string text)
    {
        // 注意：RichTextBlockElement 的 Foreground 不生效（REACTOR_MOD_003，只对
        // Control / TextBlock 生效），正文颜色交给 Run 的默认前景色继承。
        // 字号同样不写字面量：RichTextBlock 继承正文默认字号（Body 14）。
        return RichTextBlock(
                new[] { Paragraph(ParseInlines(text).ToArray()) })
            .LineHeight(22);
    }

    /// <summary>行内解析：<c>**加粗**</c> / <c>`代码`</c> / <c>[文本](url)</c>，其余按纯文本。</summary>
    internal static List<RichTextInline> ParseInlines(string text)
    {
        var inlines = new List<RichTextInline>();
        var plain = new StringBuilder();
        int i = 0;

        while (i < text.Length)
        {
            // **加粗**
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 1)
                {
                    FlushPlain();
                    inlines.Add(new RichTextRun(text[(i + 2)..end]) { IsBold = true });
                    i = end + 2;
                    continue;
                }
            }

            // `代码`
            if (text[i] == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    FlushPlain();
                    inlines.Add(new RichTextRun(text[(i + 1)..end]) { IsBold = true });
                    i = end + 1;
                    continue;
                }
            }

            // [文本](url)
            if (text[i] == '[')
            {
                int close = text.IndexOf(']', i + 1);
                if (close > i && close + 1 < text.Length && text[close + 1] == '(')
                {
                    int urlEnd = text.IndexOf(')', close + 2);
                    if (urlEnd > close + 1
                        && Uri.TryCreate(text[(close + 2)..urlEnd], UriKind.Absolute, out var uri))
                    {
                        FlushPlain();
                        inlines.Add(new RichTextHyperlink(text[(i + 1)..close], uri));
                        i = urlEnd + 1;
                        continue;
                    }
                }
            }

            plain.Append(text[i]);
            i++;
        }

        FlushPlain();
        return inlines;

        void FlushPlain()
        {
            if (plain.Length > 0)
            {
                inlines.Add(new RichTextRun(plain.ToString()));
                plain.Clear();
            }
        }
    }
}
