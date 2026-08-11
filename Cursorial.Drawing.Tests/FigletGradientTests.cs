using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Drawing;

// Capability #8: a FIGlet headline in a formatted-text document, painted with a brush, samples the brush
// PER rendered cell — so a gradient flows across the big glyphs instead of the whole headline taking one
// center-sampled color. The seam is now a BrushedStyle handed to the face (GlyphStyleProvider, the
// callback that predated IBrush living in Cursorial.Rendering, is retired — see proposal-partial-style §11.7).
public class FigletGradientTests
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);

    private static FormattedText FigletDoc(string text, int width)
    {
        var doc = new RichTextBuilder().Figlet(text, FigletFonts.Standard).Build();
        return new TextFormatter().Format(doc, width, maxRows: null, OutputCapabilities.None);
    }

    private static (Color Left, Color Right, int LeftCol, int RightCol) InkExtremes(CellBuffer b)
    {
        Color left = default, right = default;
        int leftCol = int.MaxValue, rightCol = int.MinValue;
        for (int r = 0; r < b.Rows; r++)
        for (int c = 0; c < b.Columns; c++)
        {
            var cell = b[c, r];
            if (string.IsNullOrEmpty(cell.Grapheme) || cell.Grapheme == " ") continue;
            if (c < leftCol) { leftCol = c; left = cell.Style.Foreground; }
            if (c > rightCol) { rightCol = c; right = cell.Style.Foreground; }
        }
        return (left, right, leftCol, rightCol);
    }

    [Fact]
    public void Figlet_WithGradientBrush_FlowsColorAcrossGlyphs()
    {
        // The preference samples the painted docBounds, so the paint rect is tightened to the document's
        // own width — the ramp then spans exactly the ink it colors.
        var ft = FigletDoc("HI", width: 40);
        var gradient = new LinearGradientBrush(Red, Blue, startPoint: RelativePoint.Left, endPoint: RelativePoint.Right);
        var b = DrawHarness.Render(40, 10, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, ft.Size.Columns, 10), gradient, OutputCapabilities.None));

        var (left, right, leftCol, rightCol) = InkExtremes(b);
        Assert.True(rightCol > leftCol, "the FIGlet spans multiple columns");
        Assert.True(left.Red > left.Blue, $"leftmost ink should be red-dominant, was {left}");
        Assert.True(right.Blue > right.Red, $"rightmost ink should be blue-dominant, was {right}");
    }

    /// <summary>
    /// Task #15's vertical axis: a RUN-declared vertical gradient on a figlet run samples the piece's own
    /// BAND (the strip is reading-order column × band top × total run width × band height — mixed axes,
    /// no brush classification). Two pins: rows of ONE glyph differ (the ramp spans the band's rows, not a
    /// 1-row strip), and a wrapped piece REPEATS the ramp per band (band-local, not document-continuing).
    /// </summary>
    [Fact]
    public void FigletRun_VerticalRunBrush_SamplesTheBandLocally_AndRepeatsPerWrappedBand()
    {
        // "A A" in Mini at an 8-column budget wraps into two 4-row bands, one 'A' each. Mini's 'A' inks
        // row offsets 1 and 2 of its band; offset 1 samples t = 1.5/4 and offset 2 t = 2.5/4 on a
        // Red -> Blue Top -> Bottom ramp: (159,0,96) and (96,0,159).
        var doc = new RichTextBuilder()
                  .Run("A A", new GlyphSource(FigletFonts.Mini),
                       new BrushedStyle
                       {
                           Foreground = new LinearGradientBrush(Red, Blue,
                                                                startPoint: RelativePoint.Top,
                                                                endPoint: RelativePoint.Bottom)
                       })
                  .Build();
        var ft = new TextFormatter().Format(doc, 8, maxRows: null, OutputCapabilities.None);
        var b = DrawHarness.Render(8, 8, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 8, 8), OutputCapabilities.None));

        var rowOffset1 = Color.FromRgb(159, 0, 96);   // t = 1.5/4
        var rowOffset2 = Color.FromRgb(96, 0, 159);   // t = 2.5/4

        // Band 1 (rows 0-3): the glyph's two ink rows differ — the ramp spans the band's rows. A 1-row
        // strip would clamp both rows flat to the END colour.
        Assert.Equal(rowOffset1, b[2, 1].Style.Foreground);
        Assert.Equal(rowOffset2, b[2, 2].Style.Foreground);
        Assert.NotEqual(b[2, 1].Style.Foreground, b[2, 2].Style.Foreground);

        // Band 2 (rows 4-7): the wrapped piece REPEATS the ramp at its own band — band-local, per the
        // mixed-axes ruling, not a continuation down the document.
        Assert.Equal(rowOffset1, b[2, 5].Style.Foreground);
        Assert.Equal(rowOffset2, b[2, 6].Style.Foreground);
    }

    [Fact]
    public void Figlet_WithoutBrush_RendersFlatColor()
    {
        // No document brush → the headline keeps its single block style; per-cell sampling is opt-in via the brush.
        var doc = new RichTextBuilder()
            .Figlet("HI", FigletFonts.Standard, CellStyle.Default.WithForeground(Red))
            .Build();
        var ft = new TextFormatter().Format(doc, 40, maxRows: null, OutputCapabilities.None);
        var b = DrawHarness.Render(40, 10, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 40, 10), OutputCapabilities.None));

        var (left, right, leftCol, rightCol) = InkExtremes(b);
        Assert.True(rightCol > leftCol);
        Assert.Equal(Red, left);
        Assert.Equal(Red, right);   // flat — every ink cell is the single block color
    }

    // ---- Task #15's horizontal axis: the wrapped run-declared gradient (corpus retirement pins) ----

    private static readonly Color Teal = Color.FromRgb(0, 128, 128);

    /// <summary>Formats the wrapped-gradient document the corpus pinned: "A B" in Mini at an 8-column budget.</summary>
    private static FormattedText WrappedMiniGradient() =>
        new TextFormatter().Format(
            new RichTextBuilder()
                .Run("A B", new GlyphSource(FigletFonts.Mini),
                     new BrushedStyle
                     {
                         Foreground = new LinearGradientBrush(Red, Blue,
                                                              startPoint: RelativePoint.Left,
                                                              endPoint: RelativePoint.Right)
                     })
                .Build(),
            8, maxRows: null, OutputCapabilities.None);

    /// <summary>
    /// The task-#15 fix's central property, pinned: a WRAPPED figlet run with a run-declared horizontal
    /// gradient. 'A B' in Mini at an 8-column budget wraps into two 4-row bands — 'A' (width 6, logical
    /// 0-5) and 'B' (width 5, logical 8-12; the space, logical 6-7, is consumed by the wrap) — and the
    /// run's strip spans its TOTAL width (13), rebased per piece (band 1 samples (0,0,13x4), band 2
    /// (-8,4,13x4)), so the ramp CONTINUES across the wrap in reading order instead of restarting per
    /// piece: band 2's ink picks up blue-dominant past band 1's red-dominant tail. Under piece-rect
    /// sampling band 2 would restart near red (t=0.5/5); under the pre-fix 1×1 anchor rect both bands
    /// were flat end-colour blue. The resolver path's half of the pair; its resolver-null twin below must
    /// hold byte-identical ink.
    /// </summary>
    /// <remarks>Migrated from the characterisation corpus (figlet-run-brushed-gradient-wrapped).</remarks>
    [Fact]
    public void FigletRun_WrappedHorizontalRunBrush_ContinuesTheRampAcrossTheWrap()
    {
        var ft = WrappedMiniGradient();
        var bounds = new Rect(0, 0, 8, 8);

        // The resolver arm, installed the production way — a solid teal document preference under the
        // run's declared gradient, exactly as DrawFormattedText folds a caller's brush.
        var b = new CellBuffer(8, 8);
        ft.Paint(b.AsView(), bounds, OutputCapabilities.None,
                 DrawingContext.CreateBrushResolver(
                     new BrushedStyle { Foreground = new SolidColorBrush(Teal) }, ft, bounds));

        // A cell at logical offset o samples t = (o + 0.5)/13 on the Red -> Blue ramp; band 2's cells sit
        // at o = column + 8. Exact values, worked: o=1 → (226,0,29); o=5 → (147,0,108); o=9 → (69,0,186);
        // o=12 → (10,0,245).
        Assert.Equal("/", b[1, 2].Grapheme);                              // band 1, 'A' ink
        Assert.Equal(Color.FromRgb(226, 0, 29), b[1, 2].Style.Foreground);
        Assert.Equal(Color.FromRgb(147, 0, 108), b[5, 2].Style.Foreground); // band 1's red-dominant tail
        Assert.Equal("|", b[1, 5].Grapheme);                              // band 2, 'B' ink
        Assert.Equal(Color.FromRgb(69, 0, 186), b[1, 5].Style.Foreground);  // o = 1 + 8 — the rebased strip
        Assert.Equal(Color.FromRgb(10, 0, 245), b[4, 5].Style.Foreground);  // o = 12, the run's last column

        // The continuation itself: band 2 picks up PAST band 1's tail rather than restarting the ramp.
        Assert.True(b[1, 5].Style.Foreground.Blue > b[5, 2].Style.Foreground.Blue,
                    "band 2's first ink continues the ramp past band 1's tail");
        Assert.True(b[1, 5].Style.Foreground.Blue > b[1, 5].Style.Foreground.Red,
                    "band 2 is blue-dominant from its first column");
    }

    /// <summary>
    /// figlet-run-brushed-gradient-wrapped with NO resolver installed — the FIGlet arm's resolver-null
    /// path, which the task-#15 ruling changed to match (option (b)'s stated cost): the face is handed
    /// the run's strip, not the piece rect. The ink bytes here must be IDENTICAL to the resolver case
    /// above — the two paths agreeing about one document is the closure of the disagreement
    /// figlet-inline-run-brushed-gradient originally pinned. Before the fix this path sampled the PIECE
    /// rect, so band 2 restarted its ramp near red while the resolver path painted flat blue.
    /// </summary>
    /// <remarks>Migrated from the characterisation corpus (figlet-run-brushed-gradient-wrapped-null-resolver).</remarks>
    [Fact]
    public void FigletRun_WrappedHorizontalRunBrush_PaintsIdenticalInkWithAndWithoutAResolver()
    {
        var ft = WrappedMiniGradient();
        var bounds = new Rect(0, 0, 8, 8);

        var resolved = new CellBuffer(8, 8);
        ft.Paint(resolved.AsView(), bounds, OutputCapabilities.None,
                 DrawingContext.CreateBrushResolver(
                     new BrushedStyle { Foreground = new SolidColorBrush(Teal) }, ft, bounds));

        var bare = new CellBuffer(8, 8);
        ft.Paint(bare.AsView(), bounds, OutputCapabilities.None);   // resolver: null

        int inkCells = 0;
        for (int r = 0; r < 8; r++)
        for (int c = 0; c < 8; c++)
        {
            Assert.Equal(resolved[c, r].Grapheme, bare[c, r].Grapheme);
            Assert.Equal(resolved[c, r].Style, bare[c, r].Style);
            if (!string.IsNullOrEmpty(resolved[c, r].Grapheme)) inkCells++;
        }

        Assert.True(inkCells > 0, "the wrapped glyph run painted ink");
    }
}
