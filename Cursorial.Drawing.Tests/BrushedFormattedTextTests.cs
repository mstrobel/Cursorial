using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Media;
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

    // ---- B (per-run BrushedStyle) ----------------------------------------------------------------

    private static LinearGradientBrush LeftToRight() =>
        new(Red, Blue, startPoint: RelativePoint.Left, endPoint: RelativePoint.Right);

    [Fact]
    public void PerRunBrush_InlineScope_SamplesTheRunNotTheBlock()
    {
        // "xxxxxxxxxxAB" on one line; "AB" carries an Inline-scoped L→R gradient. Inline scope sweeps AB's own
        // 2-cell rect (A red, B blue); block scope would put A near the right of the block (blue). Per-run-only
        // overload, so the leading x's stay flat.
        var bs = new BrushedStyle(LeftToRight());   // default Inline
        var doc = new RichTextBuilder().Run("xxxxxxxxxx").BrushedRun("AB", bs).Build();
        var ft = new TextFormatter().Format(doc, 14);

        var b = DrawHarness.Render(14, 2, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 14, 2), OutputCapabilities.None));

        Assert.Equal("A", b[10, 0].Grapheme);
        Assert.Equal("B", b[11, 0].Grapheme);
        var a = b[10, 0].Style.Foreground;
        var bb = b[11, 0].Style.Foreground;
        Assert.True(a.Red > a.Blue, $"A should be red (inline scope), was {a}");
        Assert.True(bb.Blue > bb.Red, $"B should be blue, was {bb}");
    }

    [Fact]
    public void PerRunBrush_BlockScope_SamplesTheWholeBlock()
    {
        // Same layout, but Block scope: "AB" sits near the right of the ~12-wide block, so it reads blue.
        var bs = new BrushedStyle(LeftToRight(), DeclarationScope.Block);
        var doc = new RichTextBuilder().Run("xxxxxxxxxx").BrushedRun("AB", bs).Build();
        var ft = new TextFormatter().Format(doc, 14);

        var b = DrawHarness.Render(14, 2, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 14, 2), OutputCapabilities.None));

        var a = b[10, 0].Style.Foreground;
        Assert.True(a.Blue > a.Red, $"A should be blue (block scope, right side), was {a}");
    }

    [Fact]
    public void PerRunBrush_WinsOverTheDocumentBrush()
    {
        var green = Color.FromRgb(0, 200, 0);
        var doc = new RichTextBuilder().Run("gg ").BrushedRun("RR", new BrushedStyle(new SolidColorBrush(Red))).Build();
        var ft = new TextFormatter().Format(doc, 14);

        var b = DrawHarness.Render(14, 2, ctx =>
            ctx.DrawFormattedText(ft, new Rect(0, 0, 14, 2), new SolidColorBrush(green), OutputCapabilities.None));

        Assert.Equal(green, b[0, 0].Style.Foreground);   // 'g' — untagged → document brush
        Assert.Equal(Red, b[3, 0].Style.Foreground);     // 'R' — per-run brush wins over the document brush
    }

    // ---- A (inline 1-D wrap-invariant sampling) --------------------------------------------------

    [Fact]
    public void PerRunBrush_InlineScope_IsWrapInvariant()
    {
        // The same Inline-scoped run, laid out unwrapped vs wrapped, colors each grapheme identically — the
        // gradient flows across the wrap as ONE reading-order strip, not restarting per line-piece.
        var bs = new BrushedStyle(LeftToRight());   // Inline L→R, Red→Blue
        FormattedText Build(int width) =>
            new TextFormatter().Format(new RichTextBuilder().BrushedRun("aaaa bbbb cccc", bs).Build(), width);

        var unwrapped = DrawHarness.Render(20, 2, ctx => ctx.DrawFormattedText(Build(20), new Rect(0, 0, 20, 2), OutputCapabilities.None));
        var wrapped = DrawHarness.Render(12, 3, ctx => ctx.DrawFormattedText(Build(9), new Rect(0, 0, 12, 3), OutputCapabilities.None));

        // "cccc" wraps to line 1. Its FIRST 'c' sits at logical offset 10 of 14 → blue-dominant under the 1-D
        // strip; per-line-piece sampling (the old behavior) would restart it at red. This is the discriminator.
        Assert.Equal("c", wrapped[0, 1].Grapheme);
        var firstC = wrapped[0, 1].Style.Foreground;
        Assert.True(firstC.Blue > firstC.Red, $"first wrapped 'c' should be blue (logical 10/14), not red (per-piece), was {firstC}");

        // Identity: a grapheme's color is independent of where it wrapped — the last 'c' (logical 13) matches
        // between the unwrapped (col 13, row 0) and wrapped (col 3, row 1) layouts.
        Assert.Equal("c", unwrapped[13, 0].Grapheme);
        Assert.Equal("c", wrapped[3, 1].Grapheme);
        Assert.Equal(unwrapped[13, 0].Style.Foreground, wrapped[3, 1].Style.Foreground);
    }

    [Fact]
    public void PerRunBrush_InlineScope_IsWrapInvariant_WithWideGlyphs()
    {
        // Pins the width hinge: the run's logical-offset accounting (GraphemeWidth) must match the painter's
        // cursor advance for WIDE glyphs (CJK 字 = 2 cells). If they drift, the strip slides past the wrap and
        // a wide glyph's wrapped color won't match its unwrapped color.
        var bs = new BrushedStyle(LeftToRight());
        FormattedText Build(int width) =>
            new TextFormatter().Format(new RichTextBuilder().BrushedRun("字字字 字字字", bs).Build(), width);

        var unwrapped = DrawHarness.Render(20, 2, ctx => ctx.DrawFormattedText(Build(20), new Rect(0, 0, 20, 2), OutputCapabilities.None));
        var wrapped = DrawHarness.Render(10, 3, ctx => ctx.DrawFormattedText(Build(7), new Rect(0, 0, 10, 3), OutputCapabilities.None));

        // The second group's first 字 — unwrapped at screen col 7 (past 3 wide glyphs + a space), wrapped at
        // (0,1) on the second line. Both are logical offset 7, so the colors must match.
        Assert.Equal("字", unwrapped[7, 0].Grapheme);
        Assert.Equal("字", wrapped[0, 1].Grapheme);
        Assert.Equal(unwrapped[7, 0].Style.Foreground, wrapped[0, 1].Style.Foreground);
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
