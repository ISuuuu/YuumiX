using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;

namespace YuumiX.App.Controls;

/// <summary>
/// 通用 Fluent 语义 Markdown 渲染器。
/// <para>
/// 严格对齐 AGENTS.md 约束：
/// 1. 排版走语义工厂（BodyLarge/BodyStrong/Body/Caption），禁止手写 FontSize 与 FontWeights.Bold；
/// 2. 块间距使用 4px 倍数（8, 12, 16），微调间距使用 1–7px 整数；
/// 3. 颜色一律走 Theme.* 语义令牌；
/// 4. 代码块使用 Icons.MonoFontFamily；
/// 5. 标题仅作为排版区分，不附加 HeadingLevel 以免污染读屏器大纲（AGENTS.md §1）；
/// 6. 列表项前缀整合在 RichTextBlock 段落内以保证换行自适应。
/// </para>
/// </summary>
public static class MarkdownView
{
    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex HorizontalRuleRegex = new(@"^(\-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Compiled);
    private static readonly Regex CalloutHeaderRegex = new(@"^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UnorderedListRegex = new(@"^(\s*)([\*\-\+])\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex OrderedListRegex = new(@"^(\s*)(\d+)\.\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex InlinePattern = new(
        @"(\*\*(?<bold>.+?)\*\*)|" +
        @"(\*(?<italic>.+?)\*)|" +
        @"(~~(?<strike>.+?)~~)|" +
        @"(`(?<code>[^`]+)`)|" +
        @"(\[(?<linkText>[^\]]+)\]\((?<linkUrl>https?://[^\s\)]+)\))|" +
        @"(?<bareUrl>https?://[^\s\)]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// 将 Markdown 文本渲染为单个 Reactor Element。
    /// </summary>
    public static Element Render(string? markdown, ElementTheme? theme = null)
    {
        var blocks = RenderBlockElements(markdown);
        if (blocks.Length == 0)
        {
            return Empty();
        }

        Element root = blocks.Length == 1
            ? blocks[0]
            : VStack(8, blocks);

        if (theme.HasValue)
        {
            root = root.RequestedTheme(theme.Value);
        }

        return root;
    }

    /// <summary>
    /// 将 Markdown 文本渲染为独立的块级元素数组。
    /// </summary>
    public static Element[] RenderBlockElements(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<Element>();
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            // 1. 空白行跳过
            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            var trimmed = line.Trim();

            // 2. Fenced Code Block (```)
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var codeLines = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length)
                {
                    i++; // 跳过闭合 ```
                }

                var codeText = string.Join("\n", codeLines);
                var textElement = Caption(codeText)
                    .Foreground(Theme.PrimaryText)
                    .TextWrapping();
                textElement = ApplyMonoFont(textElement);

                var codeBlock = Border(textElement)
                    .Background(Theme.SubtleFill)
                    .WithBorder(Theme.CardStroke, 1)
                    .CornerRadius(YuumiTheme.ControlRadius)
                    .Padding(8)
                    .HAlign(HorizontalAlignment.Stretch);

                blocks.Add(codeBlock);
                continue;
            }

            // 3. 水平分割线 (--- / *** / ___)
            if (HorizontalRuleRegex.IsMatch(trimmed))
            {
                var hr = Border(Empty())
                    .Height(1)
                    .Background(Theme.DividerStroke)
                    .HAlign(HorizontalAlignment.Stretch);

                blocks.Add(hr);
                i++;
                continue;
            }

            // 4. 标题 (H1-H6)
            var headingMatch = HeadingRegex.Match(trimmed);
            if (headingMatch.Success)
            {
                var level = headingMatch.Groups[1].Value.Length;
                var text = headingMatch.Groups[2].Value.Trim();

                Element titleElement = level == 1
                    ? Subtitle(text).TextWrapping()
                    : BodyStrong(text).TextWrapping();

                blocks.Add(titleElement);
                i++;
                continue;
            }

            // 5. 引用块与 GitHub Callout (> ...)
            if (trimmed.StartsWith('>'))
            {
                var quoteLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    var rawQuoteLine = lines[i].TrimStart();
                    // 去掉开头的 '>' 和可选的一个空格
                    var content = rawQuoteLine.Length > 1 && rawQuoteLine[1] == ' '
                        ? rawQuoteLine[2..]
                        : rawQuoteLine[1..];
                    quoteLines.Add(content);
                    i++;
                }

                blocks.Add(RenderQuoteBlock(quoteLines));
                continue;
            }

            // 6. 列表项 (- / * / + / 1.)
            var ulMatch = UnorderedListRegex.Match(line);
            var olMatch = OrderedListRegex.Match(line);
            if (ulMatch.Success || olMatch.Success)
            {
                var isOrdered = olMatch.Success;
                var indent = (isOrdered ? olMatch.Groups[1].Value : ulMatch.Groups[1].Value).Length;
                var prefix = isOrdered
                    ? $"{olMatch.Groups[2].Value}. "
                    : (indent >= 2 ? "  ◦ " : "• ");
                var content = isOrdered ? olMatch.Groups[3].Value : ulMatch.Groups[3].Value;

                var inlines = new List<RichTextInline>
                {
                    new RichTextRun(prefix)
                };
                inlines.AddRange(ParseInlines(content));

                var listElement = (RichTextBlock([Paragraph([.. inlines])]) with
                {
                    TextWrapping = TextWrapping.Wrap,
                }).LineHeight(22);

                blocks.Add(listElement);
                i++;
                continue;
            }

            // 7. 普通段落
            var paragraphLines = new List<string>();
            while (i < lines.Length)
            {
                var nextTrimmed = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(nextTrimmed)
                    || nextTrimmed.StartsWith("```", StringComparison.Ordinal)
                    || HorizontalRuleRegex.IsMatch(nextTrimmed)
                    || HeadingRegex.IsMatch(nextTrimmed)
                    || nextTrimmed.StartsWith('>')
                    || UnorderedListRegex.IsMatch(lines[i])
                    || OrderedListRegex.IsMatch(lines[i]))
                {
                    break;
                }

                paragraphLines.Add(lines[i].Trim());
                i++;
            }

            if (paragraphLines.Count > 0)
            {
                var fullParagraph = string.Join(" ", paragraphLines);
                var inlines = ParseInlines(fullParagraph);
                var paragraphElement = (RichTextBlock([Paragraph([.. inlines])]) with
                {
                    TextWrapping = TextWrapping.Wrap,
                }).LineHeight(22);

                blocks.Add(paragraphElement);
            }
        }

        return [.. blocks];
    }

    private static Element RenderQuoteBlock(List<string> lines)
    {
        if (lines.Count == 0)
        {
            return Empty();
        }

        var firstLine = lines[0].Trim();
        var calloutMatch = CalloutHeaderRegex.Match(firstLine);

        if (calloutMatch.Success)
        {
            var calloutType = calloutMatch.Groups[1].Value.ToUpperInvariant();
            var headerExtra = calloutMatch.Groups[2].Value.Trim();

            var isWarningOrCaution = calloutType is "WARNING" or "CAUTION";
            var borderBrush = isWarningOrCaution ? Theme.SystemCaution : Theme.Accent;
            var headerTitle = calloutType switch
            {
                "NOTE" => "Note",
                "TIP" => "Tip",
                "IMPORTANT" => "Important",
                "WARNING" => "Warning",
                "CAUTION" => "Caution",
                _ => calloutType,
            };

            var calloutChildren = new List<Element>();
            var headerText = string.IsNullOrEmpty(headerExtra) ? headerTitle : $"{headerTitle}: {headerExtra}";
            calloutChildren.Add(
                BodyStrong(headerText)
                    .Foreground(borderBrush)
                    .TextWrapping());

            for (var idx = 1; idx < lines.Count; idx++)
            {
                var contentLine = lines[idx].Trim();
                if (!string.IsNullOrWhiteSpace(contentLine))
                {
                    var inlines = ParseInlines(contentLine);
                    calloutChildren.Add(
                        (RichTextBlock([Paragraph([.. inlines])]) with
                        {
                            TextWrapping = TextWrapping.Wrap,
                        }).LineHeight(22));
                }
            }

            return Border(VStack(4, [.. calloutChildren]))
                .Background(Theme.SubtleFill)
                .BorderBrush(borderBrush)
                .BorderThickness(3, 0, 0, 0)
                .CornerRadius(YuumiTheme.ControlRadius)
                .Padding(8)
                .HAlign(HorizontalAlignment.Stretch);
        }

        var quoteChildren = new List<Element>();
        foreach (var qLine in lines)
        {
            var trimmed = qLine.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                var inlines = ParseInlines(trimmed);
                quoteChildren.Add(
                    (RichTextBlock([Paragraph([.. inlines])]) with
                    {
                        TextWrapping = TextWrapping.Wrap,
                    }).LineHeight(22));
            }
        }

        return Border(VStack(4, [.. quoteChildren]))
            .Background(Theme.SubtleFill)
            .BorderBrush(Theme.CardStroke)
            .BorderThickness(3, 0, 0, 0)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Padding(8)
            .HAlign(HorizontalAlignment.Stretch);
    }

    /// <summary>
    /// 行内解析：加粗、斜体、删除线、行内代码、超链接与裸 URL。
    /// </summary>
    public static List<RichTextInline> ParseInlines(string text)
    {
        var result = new List<RichTextInline>();
        var lastIndex = 0;

        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > lastIndex)
            {
                result.Add(new RichTextRun(text[lastIndex..match.Index]));
            }

            if (match.Groups["bold"].Success)
            {
                result.Add(new RichTextRun(match.Groups["bold"].Value) { IsBold = true });
            }
            else if (match.Groups["italic"].Success)
            {
                result.Add(new RichTextRun(match.Groups["italic"].Value) { IsItalic = true });
            }
            else if (match.Groups["strike"].Success)
            {
                result.Add(new RichTextRun(match.Groups["strike"].Value) { IsStrikethrough = true });
            }
            else if (match.Groups["code"].Success)
            {
                result.Add(new RichTextRun(match.Groups["code"].Value) { IsBold = true });
            }
            else if (match.Groups["linkText"].Success && match.Groups["linkUrl"].Success)
            {
                var linkText = match.Groups["linkText"].Value;
                var linkUrl = match.Groups["linkUrl"].Value;
                if (Uri.TryCreate(linkUrl, UriKind.Absolute, out var uri))
                {
                    result.Add(new RichTextHyperlink(linkText, uri));
                }
                else
                {
                    result.Add(new RichTextRun(linkText));
                }
            }
            else if (match.Groups["bareUrl"].Success)
            {
                var bareUrl = match.Groups["bareUrl"].Value;
                if (Uri.TryCreate(bareUrl, UriKind.Absolute, out var uri))
                {
                    result.Add(new RichTextHyperlink(bareUrl, uri));
                }
                else
                {
                    result.Add(new RichTextRun(bareUrl));
                }
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            result.Add(new RichTextRun(text[lastIndex..]));
        }

        return result;
    }

    private static TextBlockElement ApplyMonoFont(TextBlockElement element)
    {
        try
        {
            return element.FontFamily(Icons.MonoFontFamily);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // 单元测试/无 XAML 宿主环境兜底，避免非 UI 宿主崩溃
            return element;
        }
    }
}
