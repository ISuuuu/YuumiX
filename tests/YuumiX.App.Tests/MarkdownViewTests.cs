using System;
using System.Linq;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Xunit;
using YuumiX.App.Controls;
using YuumiX.App.Pages.Notice;

namespace YuumiX.App.Tests;

public sealed class MarkdownViewTests
{
    [Fact]
    public void Render_NullOrWhitespace_ReturnsEmptyElement()
    {
        var e1 = MarkdownView.Render(null);
        var e2 = MarkdownView.Render("   \n\t  ");

        Assert.IsType<EmptyElement>(e1);
        Assert.IsType<EmptyElement>(e2);
    }

    [Fact]
    public void RenderBlocks_Heading_CreatesAppropriateTextBlocks()
    {
        var md = @"# Title 1
## Title 2
### Title 3";
        var blocks = MarkdownView.RenderBlockElements(md);

        Assert.Equal(3, blocks.Length);
        var tb1 = Assert.IsType<TextBlockElement>(blocks[0]);
        var tb2 = Assert.IsType<TextBlockElement>(blocks[1]);
        var tb3 = Assert.IsType<TextBlockElement>(blocks[2]);

        Assert.Equal("Title 1", tb1.Content);
        Assert.Equal("Title 2", tb2.Content);
        Assert.Equal("Title 3", tb3.Content);
    }

    [Fact]
    public void RenderBlocks_FencedCodeBlock_CreatesBorderWithMonoCaption()
    {
        var md = @"```csharp
var x = 123;
return x;
```";
        var blocks = MarkdownView.RenderBlockElements(md);

        Assert.Single(blocks);
        var border = Assert.IsType<BorderElement>(blocks[0]);
        var child = Assert.IsType<TextBlockElement>(border.Child);
        Assert.Contains("var x = 123;\nreturn x;", child.Content);
    }

    [Fact]
    public void RenderBlocks_HorizontalRule_CreatesDividerBorder()
    {
        var md = @"---";
        var blocks = MarkdownView.RenderBlockElements(md);

        Assert.Single(blocks);
        var border = Assert.IsType<BorderElement>(blocks[0]);
        Assert.NotNull(border);
    }

    [Fact]
    public void RenderBlocks_CalloutAndQuote_CreatesBorderWithExpectedStructure()
    {
        var md = @"> [!NOTE] This is a note
> Detailed message here
>
> Regular quote line";

        var blocks = MarkdownView.RenderBlockElements(md);
        Assert.Single(blocks);
        var border = Assert.IsType<BorderElement>(blocks[0]);
        var stack = Assert.IsType<StackElement>(border.Child);
        Assert.True(stack.Children.Length >= 2);
    }

    [Fact]
    public void RenderBlocks_ListItems_CreatesRichTextBlocks()
    {
        var md = @"- First item
- Second item
1. Numbered item";

        var blocks = MarkdownView.RenderBlockElements(md);
        Assert.Equal(3, blocks.Length);
        Assert.All(blocks, b => Assert.IsType<RichTextBlockElement>(b));
    }

    [Fact]
    public void ParseInlines_RecognizesFormattingCorrectly()
    {
        var text = "Plain **bold** *italic* ~~del~~ `code` [link](https://github.com) https://bing.com trailing";
        var inlines = MarkdownView.ParseInlines(text);

        Assert.Contains(inlines, i => i is RichTextRun r && r.Text == "bold" && r.IsBold);
        Assert.Contains(inlines, i => i is RichTextRun r && r.Text == "italic" && r.IsItalic);
        Assert.Contains(inlines, i => i is RichTextRun r && r.Text == "del" && r.IsStrikethrough);
        Assert.Contains(inlines, i => i is RichTextRun r && r.Text == "code" && r.IsBold);
        Assert.Contains(inlines, i => i is RichTextHyperlink h && h.Text == "link" && h.NavigateUri == new Uri("https://github.com"));
        Assert.Contains(inlines, i => i is RichTextHyperlink h && h.NavigateUri == new Uri("https://bing.com"));
    }

    [Fact]
    public void NoticeMarkdown_RenderBlocks_DelegatesToMarkdownView()
    {
        var md = "# 公告标题\n- 条目1\n- 条目2";
        var blocks = NoticeMarkdown.RenderBlocks(md);

        Assert.Equal(3, blocks.Length);
        Assert.IsType<TextBlockElement>(blocks[0]);
        Assert.IsType<RichTextBlockElement>(blocks[1]);
        Assert.IsType<RichTextBlockElement>(blocks[2]);
    }
}
