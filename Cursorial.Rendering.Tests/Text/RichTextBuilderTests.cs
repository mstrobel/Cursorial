using Cursorial.Output;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Rendering.Text;

public class RichTextBuilderTests
{
    [Fact]
    public void EmptyBuilder_BuildsEmptyDocument()
    {
        var doc = new RichTextBuilder().Build();
        Assert.True(doc.IsEmpty);
    }

    [Fact]
    public void Run_WithoutExplicitParagraph_OpensOneImplicitly()
    {
        var doc = new RichTextBuilder().Run("hello").Build();
        var para = Assert.IsType<TextParagraph>(Assert.Single(doc.Blocks));
        var run = Assert.IsType<TextRun>(Assert.Single(para.Inlines));
        Assert.Equal("hello", run.Text);
    }

    [Fact]
    public void Run_EmptyString_IsNoOp()
    {
        var doc = new RichTextBuilder().Run("").Build();
        Assert.True(doc.IsEmpty);
    }

    [Fact]
    public void MultipleRuns_AccumulateIntoSingleParagraph()
    {
        var doc = new RichTextBuilder()
            .Run("hello ")
            .Run("world")
            .Build();
        var para = Assert.IsType<TextParagraph>(Assert.Single(doc.Blocks));
        Assert.Equal(2, para.Inlines.Length);
    }

    [Fact]
    public void Paragraph_ClosesOpenInlinesAndStartsNew()
    {
        var doc = new RichTextBuilder()
            .Run("first")
            .Paragraph()
            .Run("second")
            .Build();
        Assert.Equal(2, doc.Blocks.Length);
        var p1 = Assert.IsType<TextParagraph>(doc.Blocks[0]);
        var p2 = Assert.IsType<TextParagraph>(doc.Blocks[1]);
        Assert.Equal("first", Assert.IsType<TextRun>(Assert.Single(p1.Inlines)).Text);
        Assert.Equal("second", Assert.IsType<TextRun>(Assert.Single(p2.Inlines)).Text);
    }

    [Fact]
    public void Paragraph_CarriesOptionsToBlock()
    {
        var doc = new RichTextBuilder()
            .Paragraph(wrap: WrapMode.CharacterWrap, alignment: TextAlignment.Right,
                       trim: TextTrimming.WordEllipsis, maxLines: 3)
            .Run("text")
            .Build();
        var para = Assert.IsType<TextParagraph>(Assert.Single(doc.Blocks));
        Assert.Equal(WrapMode.CharacterWrap, para.Wrap);
        Assert.Equal(TextAlignment.Right, para.Alignment);
        Assert.Equal(TextTrimming.WordEllipsis, para.Trim);
        Assert.Equal(3, para.MaxLines);
    }

    [Fact]
    public void Push_AppliesStyleToSubsequentRun()
    {
        var bold = Style.Default.WithAttributes(TextAttributes.Bold);
        var builder = new RichTextBuilder();
        builder.Push(in bold);
        builder.Run("bold");

        var doc = builder.Build();
        var run = (TextRun)((TextParagraph)doc.Blocks[0]).Inlines[0];
        Assert.True(run.Style.Attributes.HasFlag(TextAttributes.Bold));
    }

    [Fact]
    public void Push_ScopeDispose_PopsStyle()
    {
        var bold = Style.Default.WithAttributes(TextAttributes.Bold);
        var builder = new RichTextBuilder();
        using (builder.Push(in bold))
            builder.Run("inside");
        builder.Run("outside");

        var doc = builder.Build();
        var inlines = ((TextParagraph)doc.Blocks[0]).Inlines;
        Assert.True(((TextRun)inlines[0]).Style.Attributes.HasFlag(TextAttributes.Bold));
        Assert.False(((TextRun)inlines[1]).Style.Attributes.HasFlag(TextAttributes.Bold));
    }

    [Fact]
    public void Push_NestedStyles_Merge()
    {
        var bold = Style.Default.WithAttributes(TextAttributes.Bold);
        var italic = Style.Default.WithAttributes(TextAttributes.Italic);
        var builder = new RichTextBuilder();
        using (builder.Push(in bold))
        using (builder.Push(in italic))
            builder.Run("both");

        var doc = builder.Build();
        var run = (TextRun)((TextParagraph)doc.Blocks[0]).Inlines[0];
        Assert.True(run.Style.Attributes.HasFlag(TextAttributes.Bold));
        Assert.True(run.Style.Attributes.HasFlag(TextAttributes.Italic));
    }

    [Fact]
    public void Push_ChildColorWins()
    {
        var red = Style.Default.WithForeground(Color.FromRgb(255, 0, 0));
        var blue = Style.Default.WithForeground(Color.FromRgb(0, 0, 255));
        var builder = new RichTextBuilder();
        using (builder.Push(in red))
        using (builder.Push(in blue))
            builder.Run("blue");

        var doc = builder.Build();
        var run = (TextRun)((TextParagraph)doc.Blocks[0]).Inlines[0];
        Assert.Equal(0, run.Style.Foreground.Red);
        Assert.Equal(255, run.Style.Foreground.Blue);
    }

    [Fact]
    public void PushMap_AppliesGlyphMapToRuns()
    {
        var builder = new RichTextBuilder();
        using (builder.PushMap(GlyphMaps.Fullwidth))
            builder.Run("a");
        builder.Run("a");

        var doc = builder.Build();
        var inlines = ((TextParagraph)doc.Blocks[0]).Inlines;
        Assert.Same(GlyphMaps.Fullwidth, ((TextRun)inlines[0]).Map);
        Assert.Null(((TextRun)inlines[1]).Map);
    }

    [Fact]
    public void PushHyperlink_AttachesUriToRuns()
    {
        var builder = new RichTextBuilder();
        using (builder.PushHyperlink("https://example.com"))
            builder.Run("click");

        var doc = builder.Build();
        var run = (TextRun)((TextParagraph)doc.Blocks[0]).Inlines[0];
        Assert.Equal("https://example.com", run.Hyperlink);
    }

    [Fact]
    public void Hyperlink_OneShot_SetsUriOnlyOnThatRun()
    {
        var builder = new RichTextBuilder();
        builder.Hyperlink("here", "https://example.com");
        builder.Run("there");

        var doc = builder.Build();
        var inlines = ((TextParagraph)doc.Blocks[0]).Inlines;
        Assert.Equal("https://example.com", ((TextRun)inlines[0]).Hyperlink);
        Assert.Null(((TextRun)inlines[1]).Hyperlink);
    }

    [Fact]
    public void LineBreak_AppendsLineBreakInline()
    {
        var doc = new RichTextBuilder().Run("a").LineBreak().Run("b").Build();
        var inlines = ((TextParagraph)doc.Blocks[0]).Inlines;
        Assert.Equal(3, inlines.Length);
        Assert.IsType<LineBreak>(inlines[1]);
        Assert.Same(LineBreak.Instance, inlines[1]);
    }

    [Fact]
    public void HorizontalRule_ClosesOpenParagraph()
    {
        var doc = new RichTextBuilder()
            .Run("before")
            .HorizontalRule()
            .Run("after")
            .Build();
        Assert.Equal(3, doc.Blocks.Length);
        Assert.IsType<TextParagraph>(doc.Blocks[0]);
        Assert.IsType<HorizontalRule>(doc.Blocks[1]);
        Assert.IsType<TextParagraph>(doc.Blocks[2]);
    }

    [Fact]
    public void HorizontalRule_DefaultGlyphIsLightBoxDrawing()
    {
        var doc = new RichTextBuilder().HorizontalRule().Build();
        var rule = Assert.IsType<HorizontalRule>(Assert.Single(doc.Blocks));
        Assert.Equal("─", rule.Glyph);
    }

    [Fact]
    public void Build_IsIdempotent_AndFlushesOpenParagraph()
    {
        var builder = new RichTextBuilder().Run("hello");
        var doc1 = builder.Build();
        var doc2 = builder.Build();

        Assert.Single(doc1.Blocks);
        // Build does not freeze the builder; subsequent calls produce equivalent documents.
        Assert.Equal(doc1.Blocks.Length, doc2.Blocks.Length);
    }
}
