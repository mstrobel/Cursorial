using System.Buffers;
using System.Text;

using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;

namespace Cursorial.Tests.Drawing;

// DrawFormattedText colors a laid-out document's text down the foreground ladder: the caller's brush is
// the weakest rung, sampled per cell against the document's DERIVED EXTENT — where the document lands in
// the paint rect, not the rect itself — and declarations in the object model (run carrier, block style,
// document default) win wherever they speak, each at its own declaration site's scope: the run's
// wrap-invariant strip, the block's ink-anchored extent, the document's extent. These pin that the
// gradient spans the document's ink (across wrapped lines and across blocks), not each line/run
// independently, not the element box — and that the ladder's precedence holds.
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
    public void VerticalGradient_SamplesTheDerivedExtent_AcrossWrappedLines()
    {
        // The two text rows sit at the TOP of a 4-row paint rect. The ramp runs over the document's
        // DERIVED EXTENT — (0,0,9,2), the two inked rows — not the 4-row paint rect: row 1 samples
        // t=0.75 (blue-dominant). `bottom.Blue > bottom.Red` is the discriminator against paint-rect
        // sampling, which put row 1 at t=0.375 and kept it red-dominant.
        var b = DrawHarness.Render(12, 4, ctx =>
            ctx.DrawFormattedText(Wrapped(), new Rect(0, 0, 12, 4),
                new LinearGradientBrush(Red, Blue, startPoint: RelativePoint.Top, endPoint: RelativePoint.Bottom),
                OutputCapabilities.None));

        // 'a' is the first glyph of line 0 (top of document); 'c' the first glyph of line 1.
        Assert.Equal("a", b[0, 0].Grapheme);
        Assert.Equal("c", b[0, 1].Grapheme);

        var top = b[0, 0].Style.Foreground;
        var bottom = b[0, 1].Style.Foreground;
        Assert.True(top.Red > top.Blue, $"top should be red-dominant, was {top}");
        Assert.True(bottom.Blue > bottom.Red, $"bottom row samples the extent's lower half (t=0.75), was {bottom}");
        Assert.True(bottom.Blue > top.Blue, $"row 1 samples further down the ramp than row 0: {top} vs {bottom}");
        Assert.True(top.Red > bottom.Red, $"row 1 samples further down the ramp than row 0: {top} vs {bottom}");
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
    public void DocumentDefaultForeground_BeatsThePreferenceBrush()
    {
        // The ladder, at its most visible: a document default that STATES a foreground is a declaration
        // at document scope, and the object model wins wherever it speaks — the caller's brush colors
        // only text no level declared for. The whole document therefore paints flat grey and the
        // gradient appears nowhere.
        var gray = Color.FromRgb(180, 180, 180);
        var doc = new RichTextBuilder(PartialStyle.WithForeground(gray)).Run("aaaa bbbb").Build();
        var ft = new TextFormatter().Format(doc, 9);

        var b = DrawHarness.Render(12, 3, ctx =>
            ctx.DrawFormattedText(ft, new Rect(0, 0, 12, 3),
                new LinearGradientBrush(Red, Blue, startPoint: RelativePoint.Left, endPoint: RelativePoint.Right),
                OutputCapabilities.None));

        Assert.Equal("a", b[0, 0].Grapheme);
        Assert.Equal("b", b[8, 0].Grapheme);
        Assert.Equal(gray, b[0, 0].Style.Foreground);
        Assert.Equal(gray, b[8, 0].Style.Foreground);
    }

    [Fact]
    public void ExplicitForeground_WinsOverTheBrush()
    {
        var green = Color.FromRgb(0, 200, 0);
        var ft = new TextFormatter().Format(
            new RichTextBuilder().Run("X", PartialStyle.WithForeground(green)).Build(), 8);
        var b = DrawHarness.Render(8, 2, ctx =>
            ctx.DrawFormattedText(ft, new Rect(0, 0, 8, 2), new SolidColorBrush(Red), OutputCapabilities.None));

        Assert.Equal("X", b[0, 0].Grapheme);
        Assert.Equal(green, b[0, 0].Style.Foreground);   // explicit green is kept; the red brush does NOT override
    }

    // ---- B (run-declared brushes) ----------------------------------------------------------------

    private static LinearGradientBrush LeftToRight() =>
        new(Red, Blue, startPoint: RelativePoint.Left, endPoint: RelativePoint.Right);

    [Fact]
    public void PerRunBrush_SamplesTheRunNotTheBlock()
    {
        // "xxxxxxxxxxAB" on one line; "AB" declares an L→R gradient on its own carrier. The declaration
        // site is the run, so the gradient sweeps AB's own 2-cell strip (A red, B blue); block-rect
        // sampling would put A near the right of the block (blue). No document/preference brush, so the
        // leading x's stay flat.
        var doc = new RichTextBuilder()
            .Run("xxxxxxxxxx")
            .Run("AB", new BrushedStyle { Foreground = LeftToRight() })
            .Build();
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
    public void BlockStyleBrush_SamplesTheWholeBlock()
    {
        // Same layout, but the gradient is declared on the PARAGRAPH's own style — the block rung. Every
        // run that declares no foreground samples the block's 2-D rect, so "A" near the right of the
        // ~12-wide block reads blue.
        var doc = new RichTextBuilder()
            .Paragraph(style: new BrushedStyle { Foreground = LeftToRight() })
            .Run("xxxxxxxxxx")
            .Run("AB")
            .Build();
        var ft = new TextFormatter().Format(doc, 14);

        var b = DrawHarness.Render(14, 2, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 14, 2), OutputCapabilities.None));

        var x = b[0, 0].Style.Foreground;
        var a = b[10, 0].Style.Foreground;
        Assert.True(x.Red > x.Blue, $"leading x should be red (block left edge), was {x}");
        Assert.True(a.Blue > a.Red, $"A should be blue (block scope, right side), was {a}");
    }

    [Fact]
    public void BlockStyleBrush_CentredParagraph_SamplesTheInk()
    {
        // The defect-1 behavioural pin the walker-level divergence test promised: a block-declared
        // gradient on a CENTRED paragraph samples the ink. "abcd" centres at columns 4-7 of a 12-wide
        // rect; the block rung samples the walk's ink-anchored Extent (4,0,4,1), so 'a' reads the ramp's
        // start (t=0.125, Red 223) and 'd' its end. The deleted Left-forced sampling rect (0,0,4,1)
        // would have put every glyph PAST the ramp — 'a' flat end-colour blue, nowhere red — so the
        // first assertion is the one that catches a regression to box-anchored sampling.
        var doc = new RichTextBuilder()
            .Paragraph(alignment: TextAlignment.Center, style: new BrushedStyle { Foreground = LeftToRight() })
            .Run("abcd")
            .Build();
        var ft = new TextFormatter().Format(doc, 12);

        var b = DrawHarness.Render(12, 2, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 12, 2), OutputCapabilities.None));

        Assert.Equal("a", b[4, 0].Grapheme);
        Assert.Equal("d", b[7, 0].Grapheme);

        var left = b[4, 0].Style.Foreground;
        var right = b[7, 0].Style.Foreground;
        Assert.True(left.Red > left.Blue, $"leftmost ink should be red-dominant (ramp start), was {left}");
        Assert.True(left.Red > 200, $"leftmost ink samples the ramp's START, not its midpoint or end, was {left}");
        Assert.True(right.Blue > right.Red, $"rightmost ink should be blue-dominant (ramp end), was {right}");
    }

    [Fact]
    public void PerRunBrush_WinsOverTheDocumentBrush()
    {
        var green = Color.FromRgb(0, 200, 0);
        var doc = new RichTextBuilder()
            .Run("gg ")
            .Run("RR", new BrushedStyle { Foreground = new SolidColorBrush(Red) })
            .Build();
        var ft = new TextFormatter().Format(doc, 14);

        var b = DrawHarness.Render(14, 2, ctx =>
            ctx.DrawFormattedText(ft, new Rect(0, 0, 14, 2), new SolidColorBrush(green), OutputCapabilities.None));

        Assert.Equal(green, b[0, 0].Style.Foreground);   // 'g' — undeclared → the preference brush
        Assert.Equal(Red, b[3, 0].Style.Foreground);     // 'R' — the run's declaration wins
    }

    // ---- A (inline 1-D wrap-invariant sampling) --------------------------------------------------

    [Fact]
    public void PerRunBrush_InlineScope_IsWrapInvariant()
    {
        // The same run-declared gradient, laid out unwrapped vs wrapped, colors each grapheme identically —
        // the gradient flows across the wrap as ONE reading-order strip, not restarting per line-piece.
        FormattedText Build(int width) =>
            new TextFormatter().Format(
                new RichTextBuilder()
                    .Run("aaaa bbbb cccc", new BrushedStyle { Foreground = LeftToRight() })
                    .Build(),
                width);

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
        FormattedText Build(int width) =>
            new TextFormatter().Format(
                new RichTextBuilder()
                    .Run("字字字 字字字", new BrushedStyle { Foreground = LeftToRight() })
                    .Build(),
                width);

        var unwrapped = DrawHarness.Render(20, 2, ctx => ctx.DrawFormattedText(Build(20), new Rect(0, 0, 20, 2), OutputCapabilities.None));
        var wrapped = DrawHarness.Render(10, 3, ctx => ctx.DrawFormattedText(Build(7), new Rect(0, 0, 10, 3), OutputCapabilities.None));

        // The second group's first 字 — unwrapped at screen col 7 (past 3 wide glyphs + a space), wrapped at
        // (0,1) on the second line. Both are logical offset 7, so the colors must match.
        Assert.Equal("字", unwrapped[7, 0].Grapheme);
        Assert.Equal("字", wrapped[0, 1].Grapheme);
        Assert.Equal(unwrapped[7, 0].Style.Foreground, wrapped[0, 1].Style.Foreground);
    }

    // ---- The sized arm: one centre sample, into the fragment (corpus retirement pins) ----

    private static readonly Color Teal = Color.FromRgb(0, 128, 128);

    private static OutputCapabilities SizingCaps() =>
        OutputCapabilities.None with
        {
            Color = ColorCapabilities.None with { Depth = ColorDepth.Truecolor },
            TextSizing = new TextSizingCapabilities(Width: true, Scale: true),
        };

    /// <summary>
    /// A brushed sized run takes ONE colour sampled at its centre — the single-style arm. That sampled
    /// colour is what reaches the OSC 66 backdrop SGR.
    /// </summary>
    /// <remarks>Migrated from the characterisation corpus (sized-brushed-block-scope).</remarks>
    [Fact]
    public void SizedText_UnderADocumentBrush_TakesOneCentreSampledColour_IntoTheFragmentsSgr()
    {
        var caps = SizingCaps();
        var doc = new RichTextBuilder().SizedText("Grad", new TextSizing(Scale: 2)).Build();
        var ft = new TextFormatter().Format(doc, 24, maxRows: null, caps);
        var b = DrawHarness.Render(24, 4, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 24, 4), LeftToRight(), caps));

        // "Grad" at scale 2 is an 8-cell piece; the centre sample is column 4 against the document's
        // derived 8-wide extent: t = 4.5/8 → rgb(112, 0, 143). One colour for the whole piece —
        // not a per-cell ramp.
        var fragment = Assert.IsType<SizedTextFragment>(b.Fragments[(0, 0)].Fragment);
        Assert.Equal(Color.FromRgb(112, 0, 143), fragment.Style.Foreground);

        // And that one colour is what the emission states for the fragment.
        var writer = new ArrayBufferWriter<byte>();
        new FrameRenderer(caps).Render(b, writer);
        Assert.Contains("38;2;112;0;143", Encoding.UTF8.GetString(writer.WrittenSpan));
    }

    /// <summary>
    /// The same tag reaching the SIZED arm, authored the way the sized overload allows it — a PushTag
    /// scope the run inherits (RichTextBuilder.cs:183-184 omits the tag argument, so it defaults to null,
    /// and :174 falls back to CurrentTag). The sized arm passes the run's tag into
    /// FormattedText.ResolveStyle just as the FIGlet arm does, so the declared red is what reaches the
    /// sampled style — and, at tier 3, the OSC 66 backdrop SGR. Authored in phase 0a while both arms
    /// hard-coded tag: null and the document's teal reached the style instead.
    /// </summary>
    /// <remarks>Migrated from the characterisation corpus (sized-inline-run-tagged-brush).</remarks>
    [Fact]
    public void SizedRun_DeclaredBrush_BeatsTheDocumentBrush_OnTheSizedArm()
    {
        var caps = SizingCaps();
        var builder = new RichTextBuilder();
        builder.Run("go ");
        builder.Run("UP", new TextSizing(Scale: 2), new BrushedStyle { Foreground = new SolidColorBrush(Red) });
        builder.Run(" now");
        var ft = new TextFormatter().Format(builder.Build(), 20, maxRows: null, caps);

        var b = DrawHarness.Render(20, 4, ctx =>
            ctx.DrawFormattedText(ft, new Rect(0, 0, 20, 4), new SolidColorBrush(Teal), caps));

        // The sized piece anchors after "go " at column 3; its sampled style is the run's declared red…
        var fragment = Assert.IsType<SizedTextFragment>(b.Fragments[(3, 0)].Fragment);
        Assert.Equal(Red, fragment.Style.Foreground);

        // …while the document brush stays live on the untagged cells around it (bottom row of the band).
        Assert.Equal("g", b[0, 1].Grapheme);
        Assert.Equal(Teal, b[0, 1].Style.Foreground);
    }

    // A minimal IContent that paints a single glyph with whatever style it's given — stands in for an
    // image/icon's glyph fallback so we can verify it inherits the document brush.
    private sealed class GlyphContent(string glyph) : IContent
    {
        public Size Measure(Size availableSpace, OutputCapabilities capabilities) => new(1, 1);

        public Rect Paint(in CellBufferView buffer, in Rect bounds, in BrushedStyle style, OutputCapabilities capabilities)
        {
            buffer.Set(bounds.Column, bounds.Row, glyph,
                       style.Resolve(bounds.Column, bounds.Row, bounds).ApplyTo(CellStyle.Default));
            return new Rect(bounds.Column, bounds.Row, 1, 1);
        }
    }
}
