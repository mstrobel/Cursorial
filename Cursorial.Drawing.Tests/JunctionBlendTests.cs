using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;

namespace Cursorial.Tests.Drawing;

// JunctionMode.Blend: where two strokes from different draw calls cross, the junction glyph still forms
// (arms max-union, like Merge), but the cell's colour is the average of the crossing strokes rather than
// the last writer's.
public class JunctionBlendTests
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);

    [Fact]
    public void Blend_CrossingStrokes_AveragesTheColors()
    {
        var b = DrawHarness.Render(9, 9, ctx =>
        {
            ctx.DrawLine(0, 4, 8, 4, Red);                                          // horizontal red
            ctx.DrawLine(4, 0, 4, 8, new Pen(Blue) { Junction = JunctionMode.Blend }); // vertical blue, blends
        });

        Assert.Equal("┼", b[4, 4].Grapheme);                                  // the junction still forms
        Assert.Equal(Color.FromRgb(128, 0, 128), b[4, 4].Style.Foreground);   // red+blue averaged (premultiplied)
    }

    [Fact]
    public void Merge_CrossingStrokes_KeepsLastWriterColor()
    {
        // Contrast: the default Merge leaves the junction the last stroke's colour (no blend).
        var b = DrawHarness.Render(9, 9, ctx =>
        {
            ctx.DrawLine(0, 4, 8, 4, Red);
            ctx.DrawLine(4, 0, 4, 8, Blue);   // default Merge
        });

        Assert.Equal("┼", b[4, 4].Grapheme);
        Assert.Equal(Blue, b[4, 4].Style.Foreground);
    }

    [Fact]
    public void Blend_NonJunctionCells_KeepTheirOwnColor()
    {
        // Only the crossing cell blends; the arms away from it stay each stroke's own colour.
        var b = DrawHarness.Render(9, 9, ctx =>
        {
            ctx.DrawLine(0, 4, 8, 4, Red);
            ctx.DrawLine(4, 0, 4, 8, new Pen(Blue) { Junction = JunctionMode.Blend });
        });

        Assert.Equal(Red, b[1, 4].Style.Foreground);    // a horizontal-arm cell → red
        Assert.Equal(Blue, b[4, 1].Style.Foreground);   // a vertical-arm cell → blue
    }
}
