using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Drawing;

// Phase 6a.1: DrawFormattedText colors a laid-out document's text with a brush sampled per cell against
// each BLOCK's rect (block-scoped 2-D). These pin that the gradient spans the block (across wrapped lines
// and across width), not each line/run independently.
public class BrushedFormattedTextTests
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);

    // "aaaa bbbb cccc dddd" word-wrapped at width 9 → two lines: "aaaa bbbb" / "cccc dddd".
    private static FormattedText Wrapped() =>
        new TextFormatter().Format(new RichTextBuilder().Run("aaaa bbbb cccc dddd").Build(), 9);

    [Fact]
    public void WrapsToOneParagraphTwoLines()
    {
        var ft = Wrapped();
        Assert.Single(ft.Blocks);
        var paragraph = Assert.IsType<FormattedParagraph>(ft.Blocks[0]);
        Assert.Equal(2, paragraph.Lines.Length);
    }

    [Fact]
    public void VerticalGradient_SpansTheBlock_AcrossWrappedLines()
    {
        var b = DrawHarness.Render(12, 4, ctx =>
            ctx.DrawFormattedText(Wrapped(), new Rect(0, 0, 12, 4),
                new LinearGradientBrush(Red, Blue, startPoint: RelativePoint.Top, endPoint: RelativePoint.Bottom),
                OutputCapabilities.None));

        // 'a' is the first glyph of line 0 (top of block); 'c' the first glyph of line 1 (bottom).
        Assert.Equal("a", b[0, 0].Grapheme);
        Assert.Equal("c", b[0, 1].Grapheme);

        var top = b[0, 0].Style.Foreground;
        var bottom = b[0, 1].Style.Foreground;
        Assert.True(top.Red > top.Blue, $"top should be red-dominant, was {top}");
        Assert.True(bottom.Blue > bottom.Red, $"bottom should be blue-dominant, was {bottom}");
    }

    [Fact]
    public void HorizontalGradient_SpansTheBlockWidth()
    {
        var b = DrawHarness.Render(12, 4, ctx =>
            ctx.DrawFormattedText(Wrapped(), new Rect(0, 0, 12, 4),
                new LinearGradientBrush(Red, Blue, startPoint: RelativePoint.Left, endPoint: RelativePoint.Right),
                OutputCapabilities.None));

        // Line 0 is "aaaa bbbb": col 0 = first 'a' (left edge), col 8 = last 'b' (right edge).
        Assert.Equal("a", b[0, 0].Grapheme);
        Assert.Equal("b", b[8, 0].Grapheme);

        var left = b[0, 0].Style.Foreground;
        var right = b[8, 0].Style.Foreground;
        Assert.True(left.Red > left.Blue, $"left should be red-dominant, was {left}");
        Assert.True(right.Blue > right.Red, $"right should be blue-dominant, was {right}");
    }

    [Fact]
    public void NullArguments_Throw()
    {
        DrawHarness.Render(4, 2, ctx =>
        {
            Assert.Throws<ArgumentNullException>(() =>
                ctx.DrawFormattedText(null!, new Rect(0, 0, 4, 2), new SolidColorBrush(Red), OutputCapabilities.None));
            Assert.Throws<ArgumentNullException>(() =>
                ctx.DrawFormattedText(Wrapped(), new Rect(0, 0, 4, 2), null!, OutputCapabilities.None));
        });
    }

    // ---- 6a.2 (C): all block/run types routed through the resolver --------------------------------

    [Fact]
    public void HorizontalRule_IsColoredPerCell()
    {
        var ft = new TextFormatter().Format(new RichTextBuilder().HorizontalRule().Build(), 10);
        var b = DrawHarness.Render(10, 2, ctx =>
            ctx.DrawFormattedText(ft, new Rect(0, 0, 10, 2),
                new LinearGradientBrush(Red, Blue, startPoint: RelativePoint.Left, endPoint: RelativePoint.Right),
                OutputCapabilities.None));

        Assert.False(string.IsNullOrEmpty(b[0, 0].Grapheme));   // the rule glyph is present
        var left = b[0, 0].Style.Foreground;
        var right = b[9, 0].Style.Foreground;
        Assert.True(left.Red > left.Blue, $"rule left should be red-dominant, was {left}");
        Assert.True(right.Blue > right.Red, $"rule right should be blue-dominant, was {right}");
    }

    [Fact]
    public void InlineContentFallbackGlyph_PicksUpTheBrush()
    {
        // A content whose Paint writes a glyph with the style it's handed — i.e. a "fallback glyph".
        var ft = new TextFormatter().Format(
            new RichTextBuilder().InlineContent(new GlyphContent("█")).Build(), 8);
        var green = Color.FromRgb(0, 200, 0);
        var b = DrawHarness.Render(8, 2, ctx =>
            ctx.DrawFormattedText(ft, new Rect(0, 0, 8, 2), new SolidColorBrush(green), OutputCapabilities.None));

        Assert.Equal("█", b[0, 0].Grapheme);
        Assert.Equal(green, b[0, 0].Style.Foreground);   // the fallback glyph inherited the document brush
    }

    [Fact]
    public void Brush_AppliesOverADocumentDefaultForeground()
    {
        // Regression: a document whose DefaultStyle sets a foreground must still receive the brush on its
        // inherited text — the brush overrides the document default; only a run's OWN explicit color wins.
        var gray = Color.FromRgb(180, 180, 180);
        var doc = new RichTextBuilder(Style.Default.WithForeground(gray)).Run("aaaa bbbb").Build();
        var ft = new TextFormatter().Format(doc, 9);

        var b = DrawHarness.Render(12, 3, ctx =>
            ctx.DrawFormattedText(ft, new Rect(0, 0, 12, 3),
                new LinearGradientBrush(Red, Blue, startPoint: RelativePoint.Left, endPoint: RelativePoint.Right),
                OutputCapabilities.None));

        var left = b[0, 0].Style.Foreground;
        var right = b[8, 0].Style.Foreground;
        Assert.NotEqual(gray, left);                       // the brush applied — NOT the document default
        Assert.True(left.Red > left.Blue, $"left {left}");
        Assert.True(right.Blue > right.Red, $"right {right}");
    }

    [Fact]
    public void ExplicitForeground_WinsOverTheBrush()
    {
        var green = Color.FromRgb(0, 200, 0);
        var ft = new TextFormatter().Format(
            new RichTextBuilder().Run("X", Style.Default.WithForeground(green)).Build(), 8);
        var b = DrawHarness.Render(8, 2, ctx =>
            ctx.DrawFormattedText(ft, new Rect(0, 0, 8, 2), new SolidColorBrush(Red), OutputCapabilities.None));

        Assert.Equal("X", b[0, 0].Grapheme);
        Assert.Equal(green, b[0, 0].Style.Foreground);   // explicit green is kept; the red brush does NOT override
    }

    // A minimal IContent that paints a single glyph with whatever style it's given — stands in for an
    // image/icon's glyph fallback so we can verify it inherits the document brush.
    private sealed class GlyphContent(string glyph) : IContent
    {
        public Size Measure(Size availableSpace, OutputCapabilities capabilities) => new(1, 1);

        public Rect Paint(in CellBufferView buffer, in Rect bounds, in Style style, OutputCapabilities capabilities)
        {
            buffer.Set(bounds.Column, bounds.Row, glyph, style);
            return new Rect(bounds.Column, bounds.Row, 1, 1);
        }
    }
}
