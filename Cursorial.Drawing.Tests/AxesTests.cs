using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

public class AxesTests
{
    [Fact]
    public void Nice_RoundsRangeAndPicksNiceTicks()
    {
        var (r1, step1, ticks1) = new AxisRange(0, 9).Nice(5);
        Assert.Equal(new AxisRange(0, 10), r1);
        Assert.Equal(2.0, step1);
        Assert.Equal(new double[] { 0, 2, 4, 6, 8, 10 }, ticks1);

        var (r2, step2, _) = new AxisRange(0, 100).Nice(5);
        Assert.Equal(new AxisRange(0, 100), r2);
        Assert.Equal(20.0, step2);

        var (r3, _, ticks3) = new AxisRange(2, 9).Nice(5);
        Assert.Equal(new AxisRange(2, 10), r3);   // niceMin floors to a step multiple
        Assert.Equal(new double[] { 2, 4, 6, 8, 10 }, ticks3);
    }

    [Fact]
    public void Nice_DegenerateRange_ReturnsItself()
    {
        var (r, _, ticks) = new AxisRange(5, 5).Nice(5);
        Assert.Equal(new AxisRange(5, 5), r);
        Assert.Equal([5, 5], ticks);
    }

    [Fact]
    public void Axes_Render_InsetsPlotAndDrawsFrame()
    {
        PlotLayout layout = default;
        var b = DrawHarness.Render(20, 10, ctx =>
        {
            var axes = new Axes(AxisRange.FromValues([0, 10]), AxisRange.FromValues([0, 10]))
            {
                LabelColor = Color.FromRgb(200, 200, 200),
            };
            layout = axes.Render(ctx, new Rect(0, 0, 20, 10));
        });

        // Y labels up to "10" → left gutter 3; one bottom row for X labels → plot is (4,0,16,8).
        Assert.Equal(new Rect(4, 0, 16, 8), layout.Plot);
        Assert.Equal(new AxisRange(0, 10), layout.X);
        Assert.Equal(new AxisRange(0, 10), layout.Y);

        Assert.Equal("└", b[3, 8].Grapheme);    // axes meet at the corner
        Assert.Equal("│", b[3, 4].Grapheme);    // Y axis
        Assert.Equal("─", b[10, 8].Grapheme);   // X axis
        Assert.Equal("0", b[1, 7].Grapheme);    // Y tick label (value 0, bottom)
        Assert.Equal("0", b[4, 9].Grapheme);    // X tick label (value 0, left)
    }

    [Fact]
    public void Axes_Gridlines_JunctionTheAxes()
    {
        var b = DrawHarness.Render(20, 10, ctx =>
            new Axes(AxisRange.FromValues([0, 10]), AxisRange.FromValues([0, 10]))
            {
                XAxis = new Axis { Gridlines = true },
                YAxis = new Axis { Gridlines = true },
            }.Render(ctx, new Rect(0, 0, 20, 10)));

        Assert.Equal("├", b[3, 6].Grapheme);   // a horizontal gridline meets the Y axis
        Assert.Equal("┴", b[7, 8].Grapheme);   // a vertical gridline meets the X axis
    }

    [Fact]
    public void Axes_TinyArea_IsANoOpFrame()
    {
        // No room for a plot after gutters → returns the area unchanged, doesn't throw.
        PlotLayout layout = default;
        DrawHarness.Render(3, 2, ctx =>
            layout = new Axes(AxisRange.FromValues([0, 1]), AxisRange.FromValues([0, 1])).Render(ctx, new Rect(0, 0, 3, 2)));
        Assert.Equal(new Rect(0, 0, 3, 2), layout.Plot);
    }
}
