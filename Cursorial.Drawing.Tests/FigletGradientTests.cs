using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Drawing;

// Capability #8: a FIGlet headline in a formatted-text document, painted with a brush, samples the brush
// PER rendered cell — so a gradient flows across the big glyphs instead of the whole headline taking one
// center-sampled color. The §9 invariant holds: the seam is a brush-blind GlyphStyleProvider; IBrush never
// enters Cursorial.Rendering.
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
        var ft = FigletDoc("HI", width: 40);
        var gradient = new LinearGradientBrush(Red, Blue, startPoint: RelativePoint.Left, endPoint: RelativePoint.Right);
        var b = DrawHarness.Render(40, 10, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 40, 10), gradient, OutputCapabilities.None));

        var (left, right, leftCol, rightCol) = InkExtremes(b);
        Assert.True(rightCol > leftCol, "the FIGlet spans multiple columns");
        Assert.True(left.Red > left.Blue, $"leftmost ink should be red-dominant, was {left}");
        Assert.True(right.Blue > right.Red, $"rightmost ink should be blue-dominant, was {right}");
    }

    [Fact]
    public void Figlet_WithoutBrush_RendersFlatColor()
    {
        // No document brush → the headline keeps its single block style; per-cell sampling is opt-in via the brush.
        var doc = new RichTextBuilder()
            .Figlet("HI", FigletFonts.Standard, Style.Default.WithForeground(Red))
            .Build();
        var ft = new TextFormatter().Format(doc, 40, maxRows: null, OutputCapabilities.None);
        var b = DrawHarness.Render(40, 10, ctx => ctx.DrawFormattedText(ft, new Rect(0, 0, 40, 10), OutputCapabilities.None));

        var (left, right, leftCol, rightCol) = InkExtremes(b);
        Assert.True(rightCol > leftCol);
        Assert.Equal(Red, left);
        Assert.Equal(Red, right);   // flat — every ink cell is the single block color
    }
}
