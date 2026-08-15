using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Media;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Rendering.Text;

public class InlineContentTests
{
    private sealed class FixedSizeContent : IContent
    {
        private readonly int _width;
        public FixedSizeContent(int width) => _width = width;

        public bool PaintCalled { get; private set; }
        public Rect LastPaintBounds { get; private set; }

        public Size Measure(Size availableSpace, OutputCapabilities capabilities) => new(_width, 1);

        public Rect Paint(in CellBufferView buffer, in Rect bounds, in BrushedStyle style, OutputCapabilities capabilities)
        {
            PaintCalled = true;
            LastPaintBounds = bounds;
            // Tag the cells so tests can verify Paint reached the right anchor. The carrier
            // resolves per painted cell over its bounds — the value seam a cell write is.
            for (int c = bounds.Column; c < bounds.Column + bounds.Columns && c < buffer.Columns; c++)
                buffer.Set(c, bounds.Row, "■", style.Resolve(c, bounds.Row, bounds).ApplyTo(CellStyle.Default));
            return bounds;
        }
    }

    private sealed class ModeProbe : IContent
    {
        public IBlendingMode? Seen { get; private set; }
        public Size Measure(Size availableSpace, OutputCapabilities capabilities) => new(1, 1);
        public Rect Paint(in CellBufferView buffer, in Rect bounds, in BrushedStyle style, OutputCapabilities capabilities)
        {
            Seen = style.Mode;
            return bounds;
        }
    }

    [Fact]
    public void InlineContent_ReceivesTheDocumentBlendingMode()
    {
        var content = new ModeProbe();
        var doc = new RichTextBuilder(BrushedStyle.Identity with { Mode = BlendingModes.Multiply })
            .Run("ab ").InlineContent(content)
            .Build();

        var ft = new TextFormatter().Format(doc, 20);
        var buffer = new CellBuffer(20, 3);
        ft.Paint(buffer, new Rect(0, 0, 20, 3), OutputCapabilities.None);

        // The content arm preserves the run's Mode into the BrushedStyle handed to IContent.Paint (via
        // AsBrushed), so a content that recurses — a nested FormattedText — threads it to its own Set/face
        // application point. The framework's contract is that the Mode ARRIVES; applying it is the content's.
        Assert.Same(BlendingModes.Multiply, content.Seen);
    }

    [Fact]
    public void InlineContent_TreatedAsAtomicWord_OnSameLine()
    {
        var content = new FixedSizeContent(3);
        var doc = new RichTextBuilder()
            .Run("a ").InlineContent(content).Run(" b")
            .Build();

        var ft = new TextFormatter().Format(doc, 80);
        var para = (FormattedParagraph) ft.Blocks[0];

        Assert.Single(para.Lines);
        // Line should contain a content run.
        Assert.Contains(para.Lines[0].Runs, r => r is FormattedContentRun);
    }

    [Fact]
    public void InlineContent_WrapsToNextLine_WhenItDoesntFit()
    {
        var content = new FixedSizeContent(5);
        var doc = new RichTextBuilder()
            .Run("hello ").InlineContent(content)
            .Build();

        // Budget = 8 cells. "hello" = 5, then space = 6, then 5-cell content → 11. Should wrap.
        var ft = new TextFormatter().Format(doc, 8);
        var para = (FormattedParagraph) ft.Blocks[0];

        Assert.Equal(2, para.Lines.Length);
        // Second line carries the content.
        Assert.Contains(para.Lines[1].Runs, r => r is FormattedContentRun);
    }

    [Fact]
    public void InlineContent_PaintInvokesContentPaintAtCorrectAnchor()
    {
        var content = new FixedSizeContent(3);
        var doc = new RichTextBuilder()
            .Run("ab ").InlineContent(content)
            .Build();

        var ft = new TextFormatter().Format(doc, 20);
        var buffer = new CellBuffer(20, 3);
        ft.Paint(buffer, new Rect(0, 0, 20, 3), OutputCapabilities.None);

        Assert.True(content.PaintCalled);
        // "ab " + content → content anchors at column 3.
        Assert.Equal(3, content.LastPaintBounds.Column);
        Assert.Equal(0, content.LastPaintBounds.Row);
        Assert.Equal(3, content.LastPaintBounds.Columns);

        // Painter wrote our sentinel "■" at the content cells.
        Assert.Equal("■", buffer[3, 0].Grapheme);
        Assert.Equal("■", buffer[5, 0].Grapheme);
    }

    [Fact]
    public void InlineContent_PreservesSurroundingText()
    {
        var content = new FixedSizeContent(2);
        var doc = new RichTextBuilder()
            .Run("L ").InlineContent(content).Run(" R")
            .Build();

        var ft = new TextFormatter().Format(doc, 20);
        var buffer = new CellBuffer(20, 3);
        ft.Paint(buffer, new Rect(0, 0, 20, 3), OutputCapabilities.None);

        Assert.Equal("L", buffer[0, 0].Grapheme);
        Assert.Equal("■", buffer[2, 0].Grapheme);
        Assert.Equal("■", buffer[3, 0].Grapheme);
        Assert.Equal("R", buffer[5, 0].Grapheme);
    }

    [Fact]
    public void InlineContent_InheritsDocumentDefaultForeground_WithoutAResolver()
    {
        // The content-rung ruling: the document rung folds under a content run's carrier, so a
        // document-declared foreground reaches a fallback glyph with NO brush resolver installed —
        // the same declaration the resolver's generic ladder was already honouring when one was.
        // Before the fold, the sampled style handed to content ignored the document default and
        // the glyph painted in the terminal's own colour.
        var teal = Color.FromRgb(0, 128, 128);
        var content = new FixedSizeContent(2);
        var doc = new RichTextBuilder(CellStyle.Default.WithForeground(teal))
            .Run("a ").InlineContent(content)
            .Build();

        var ft = new TextFormatter().Format(doc, 20);
        var buffer = new CellBuffer(20, 3);
        ft.Paint(buffer, new Rect(0, 0, 20, 3), OutputCapabilities.None);

        Assert.Equal("■", buffer[2, 0].Grapheme);
        Assert.Equal(teal, buffer[2, 0].Style.Foreground);
    }

    [Fact]
    public void InlineContent_DoesNotSplitOnCharacterWrap()
    {
        // Even in CharacterWrap mode, content stays atomic.
        var content = new FixedSizeContent(5);
        var doc = new RichTextBuilder()
            .Paragraph(wrap: WrapMode.CharacterWrap)
            .Run("xx").InlineContent(content)
            .Build();

        // Budget = 4. "xx" = 2; content (5) doesn't fit; should push content to next line whole.
        var ft = new TextFormatter().Format(doc, 4);
        var para = (FormattedParagraph) ft.Blocks[0];

        Assert.Equal(2, para.Lines.Length);
        // Second line's content run is intact.
        var contentRun = (FormattedContentRun) para.Lines[1].Runs.Single();
        Assert.Equal(5, contentRun.Width);
    }
}
