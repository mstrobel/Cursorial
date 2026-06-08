using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

public class MultiSeriesTests
{
    private static readonly Color Red = Color.FromRgb(220, 60, 60);
    private static readonly Color Blue = Color.FromRgb(60, 120, 220);

    private static bool IsBraille(string? g) => !string.IsNullOrEmpty(g) && g[0] is >= '⠀' and <= '⣿';

    // Two flat lines at different heights → top line red (row 0), bottom line blue (row 5).
    private static MultiLineChart TwoLines() => new(
    [
        new ChartSeries([new(0, 9), new(7, 9)], Red),    // high → top
        new ChartSeries([new(0, 1), new(7, 1)], Blue),   // low → bottom
    ])
    { XRange = new AxisRange(0, 7), YRange = new AxisRange(0, 10) };

    [Fact]
    public void ResolveRange_UnionsAllSeries()
    {
        var chart = new MultiLineChart([new ChartSeries([new(0, 1), new(3, 4)], Red),
                                        new ChartSeries([new(2, -1), new(7, 6)], Blue)]);
        var (x, y) = chart.ResolveRange();
        Assert.Equal(new AxisRange(0, 7), x);
        Assert.Equal(new AxisRange(-1, 6), y);
    }

    [Fact]
    public void Render_SingleSurface_KeepsEachSeriesColor()
    {
        var b = DrawHarness.Render(8, 6, ctx => TwoLines().Render(ctx, new Rect(0, 0, 8, 6)));

        Assert.True(IsBraille(b[3, 0].Grapheme));
        Assert.Equal(Red, b[3, 0].Style.Foreground);    // top line
        Assert.True(IsBraille(b[3, 5].Grapheme));
        Assert.Equal(Blue, b[3, 5].Style.Foreground);   // bottom line
    }

    [Fact]
    public void Empty_IsANoOp()
    {
        var b = DrawHarness.Render(4, 4, ctx => new MultiLineChart([]).Render(ctx, new Rect(0, 0, 4, 4)));
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                Assert.False(IsBraille(b[c, r].Grapheme));
    }
}
