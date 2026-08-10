using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Rendering.Text;

public class MultiBlockFormatterTests
{
    // ---- HorizontalRule ----

    [Fact]
    public void HorizontalRule_FillsColumnBudget()
    {
        var doc = new RichTextBuilder().HorizontalRule().Build();
        var ft = new TextFormatter().Format(doc, 10);

        var rule = Assert.IsType<FormattedHorizontalRule>(Assert.Single(ft.Blocks));
        Assert.Equal("─", rule.Glyph);
        Assert.Equal(new Size(10, 1), rule.Size);
    }

    [Fact]
    public void HorizontalRule_CustomGlyphAndStyle()
    {
        var redStyle = CellStyle.Default.WithForeground(Color.FromRgb(255, 0, 0));
        var doc = new RichTextBuilder()
            .HorizontalRule(glyph: "═", style: redStyle, alignment: TextAlignment.Center)
            .Build();
        var ft = new TextFormatter().Format(doc, 8);
        var rule = (FormattedHorizontalRule) ft.Blocks[0];

        Assert.Equal("═", rule.Glyph);
        Assert.Equal(255, rule.Style.ResolveFlat().Foreground.Red);
        Assert.Equal(TextAlignment.Center, rule.Alignment);
    }

    [Fact]
    public void HorizontalRule_PaintsRepeatedGlyph()
    {
        var doc = new RichTextBuilder().HorizontalRule(glyph: "-").Build();
        var ft = new TextFormatter().Format(doc, 5);

        var buffer = new CellBuffer(10, 3);
        ft.Paint(buffer, new Rect(0, 0, 10, 3), OutputCapabilities.None);

        for (int c = 0; c < 5; c++)
            Assert.Equal("-", buffer[c, 0].Grapheme);
    }

    // ---- Block stacking + margins ----

    [Fact]
    public void Blocks_StackVertically()
    {
        var doc = new RichTextBuilder()
            .Run("first")
            .Paragraph(margin: Margins.Zero)
            .Run("second")
            .Build();
        var ft = new TextFormatter().Format(doc, 20);

        Assert.Equal(2, ft.Blocks.Length);
        Assert.Equal(2, ft.Size.Rows);
    }

    [Fact]
    public void Margins_ApplyBetweenBlocks()
    {
        var doc = new RichTextBuilder()
            .Run("first")
            .Paragraph(margin: new Margins(0, 2))
            .Run("second")
            .Build();
        var ft = new TextFormatter().Format(doc, 20);

        Assert.Equal(2, ft.Blocks.Length);
        // First block: 1 row. Top margin of second block: 2. Second block: 1 row. Total: 4.
        Assert.Equal(4, ft.Size.Rows);
    }

    [Fact]
    public void Margin_OnFirstBlock_IsSuppressed()
    {
        var doc = new RichTextBuilder()
            .Paragraph(margin: new Margins(0, 3))
            .Run("only")
            .Build();
        var ft = new TextFormatter().Format(doc, 20);

        Assert.Single(ft.Blocks);
        Assert.Equal(1, ft.Size.Rows);
    }

    // ---- MaxRows across multi-block layouts ----

    [Fact]
    public void MaxRows_DropsBlocksPastBudget()
    {
        var doc = new RichTextBuilder()
            .Run("first").Paragraph(/* default margins t=1, b=1 */)
            .Run("second").Paragraph(/* default margins t=1, b=1 */)
            .Run("third")
            .Build();
        var ft = new TextFormatter().Format(doc, 20, maxRows: 3);

        Assert.Equal(2, ft.Blocks.Length);
        Assert.Equal(3, ft.Size.Rows);
    }

    [Fact]
    public void MaxRows_DropsPartialBlockEntirely()
    {
        // HR is a single row; if there's no budget for it, it should be dropped entirely.
        var doc = new RichTextBuilder()
            .Run("first")
            .HorizontalRule()
            .Build();
        var ft = new TextFormatter().Format(doc, 20, maxRows: 1);

        Assert.Single(ft.Blocks);
    }

    // ---- BlockContent ----

    [Fact]
    public void BlockContent_HostsArbitraryIContent()
    {
        var content = new TestContent(new Size(8, 2));
        var doc = new RichTextBuilder().Content(content).Build();
        var ft = new TextFormatter().Format(doc, 20);

        var formatted = Assert.IsType<FormattedContentBlock>(Assert.Single(ft.Blocks));
        Assert.Equal(8, formatted.Size.Columns);
        Assert.Equal(2, formatted.Size.Rows);
        Assert.Same(content, formatted.Content);
    }

    [Fact]
    public void BlockContent_PaintCallsIContentPaint()
    {
        var content = new TestContent(new Size(4, 1));
        var doc = new RichTextBuilder().Content(content, alignment: TextAlignment.Right).Build();
        var ft = new TextFormatter().Format(doc, 10);

        var buffer = new CellBuffer(10, 3);
        ft.Paint(buffer, new Rect(0, 0, 10, 3), OutputCapabilities.None);

        Assert.True(content.PaintCalled);
        // Right-aligned: anchor at column 10 - 4 = 6.
        Assert.Equal(6, content.LastPaintBounds.Column);
        Assert.Equal(0, content.LastPaintBounds.Row);
    }

    // ---- FigletBlock ----

    [Fact]
    public void FigletBlock_UsesGlyphFontMeasure()
    {
        var doc = new RichTextBuilder()
            .Figlet("HI", MonospaceFont.Default)
            .Build();
        var ft = new TextFormatter().Format(doc, 20);

        // Block sugar: figlet formats as a one-run paragraph whose band height is the face's.
        var fig = Assert.IsType<FormattedParagraph>(Assert.Single(ft.Blocks));
        var expected = MonospaceFont.Default.Measure("HI");
        Assert.Equal(expected.Rows, fig.Size.Rows);
    }

    [Fact]
    public void FigletBlock_Paint_DelegatesToGlyphFont()
    {
        var doc = new RichTextBuilder()
            .Figlet("HI", MonospaceFont.Default)
            .Build();
        var ft = new TextFormatter().Format(doc, 20);

        var buffer = new CellBuffer(20, 3);
        ft.Paint(buffer, new Rect(0, 0, 20, 3), OutputCapabilities.None);

        // Monospace renders "HI" verbatim into cells (0,0) and (1,0).
        Assert.Equal("H", buffer[0, 0].Grapheme);
        Assert.Equal("I", buffer[1, 0].Grapheme);
    }

    [Fact]
    public void FigletBlock_AlignmentCenter()
    {
        var doc = new RichTextBuilder()
            .Figlet("HI", MonospaceFont.Default, alignment: TextAlignment.Center)
            .Build();
        var ft = new TextFormatter().Format(doc, 10);

        var buffer = new CellBuffer(10, 3);
        ft.Paint(buffer, new Rect(0, 0, 10, 3), OutputCapabilities.None);

        // 2-wide block centered in 10 cols → anchor at (10-2)/2 = 4.
        Assert.Equal("H", buffer[4, 0].Grapheme);
        Assert.Equal("I", buffer[5, 0].Grapheme);
    }

    // ---- Mixed multi-block layouts ----

    [Fact]
    public void MixedDocument_ParagraphHRParagraph_StacksCorrectly()
    {
        var doc = new RichTextBuilder()
            .Run("alpha")
            .HorizontalRule(/* default margins t=1, b=1 */)
            .Paragraph(/* default margins t=1, b=1 */)
            .Run("beta")
            .Build();
        var ft = new TextFormatter().Format(doc, 10);

        Assert.Equal(3, ft.Blocks.Length);
        Assert.IsType<FormattedParagraph>(ft.Blocks[0]);
        Assert.IsType<FormattedHorizontalRule>(ft.Blocks[1]);
        Assert.IsType<FormattedParagraph>(ft.Blocks[2]);
        Assert.Equal(5, ft.Size.Rows);
    }

    // ---- Test helper ----

    private sealed class TestContent : IContent
    {
        private readonly Size _size;

        public TestContent(Size size) => _size = size;

        public bool PaintCalled { get; private set; }
        public Rect LastPaintBounds { get; private set; }

        public Size Measure(Size availableSpace, OutputCapabilities capabilities) => _size;

        public Rect Paint(in CellBufferView buffer, in Rect bounds, in CellStyle style, OutputCapabilities capabilities)
        {
            PaintCalled = true;
            LastPaintBounds = bounds;
            return bounds.WithSize(_size);
        }
    }
}
