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
}
