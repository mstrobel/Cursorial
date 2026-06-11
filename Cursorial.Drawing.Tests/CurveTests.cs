using Cursorial.Drawing.Charts;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

public class CurveTests
{
    private static bool IsBraille(string? g) => !string.IsNullOrEmpty(g) && g[0] is >= '⠀' and <= '⣿';

    [Fact]
    public void Curves_Linear_ReturnsPointsVerbatim()
    {
        PointD[] pts = [new(0, 0), new(1, 2), new(2, 1)];
        var s = Curves.Sample(CurveInterpolation.Linear, pts, 8);
        Assert.Equal(pts, s);
    }

    [Fact]
    public void Curves_MonotoneCubic_MatchesOracle_AndDoesNotOvershoot()
    {
        // Step data x=[0,1,2,3] y=[0,0,1,1]; per=2 puts samples at x = 0,0.5,1,1.5,2,2.5,3.
        PointD[] pts = [new(0, 0), new(1, 0), new(2, 1), new(3, 1)];
        var s = Curves.Sample(CurveInterpolation.MonotoneCubic, pts, 2);

        // index 3 is segment[1,2] at t=0.5 → x=1.5; oracle y=0.5.
        Assert.Equal(1.5, s[3].X, 6);
        Assert.Equal(0.5, s[3].Y, 6);
        Assert.Equal(0.0, s[1].Y, 6);   // x=0.5 on the flat
        Assert.Equal(1.0, s[5].Y, 6);   // x=2.5 on the flat

        Assert.All(s, p => Assert.InRange(p.Y, -1e-9, 1 + 1e-9));   // shape-preserving: no overshoot
    }

    [Fact]
    public void Curves_CatmullRom_MatchesOracle()
    {
        // Centripetal Catmull-Rom (α=0.5); oracle midpoint of segment P1→P2 = (1.5, 1.096028).
        PointD[] pts = [new(0, 0), new(1, 1), new(2, 1), new(3, 0)];
        var s = Curves.Sample(CurveInterpolation.CatmullRom, pts, 2);
        Assert.Equal(1.5, s[3].X, 5);
        Assert.Equal(1.096028, s[3].Y, 5);
    }

    [Fact]
    public void Curves_MonotoneCubic_SortsByX()
    {
        // Unsorted X is sorted (function-graph contract), never throws.
        PointD[] pts = [new(2, 1), new(0, 0), new(1, 0), new(3, 1)];
        var s = Curves.Sample(CurveInterpolation.MonotoneCubic, pts, 2);
        Assert.True(s.Count > 2);
        for (int i = 1; i < s.Count; i++)
            Assert.True(s[i].X >= s[i - 1].X);   // monotone X out
    }

    [Fact]
    public void Curves_MonotoneCubic_TwoPoints_IsTheSegment()
    {
        // The n==2 early return: a single segment is just its endpoints.
        var s = Curves.Sample(CurveInterpolation.MonotoneCubic, [new(0, 0), new(1, 5)], 4);
        Assert.Equal(2, s.Count);
        Assert.Equal(new PointD(0, 0), s[0]);
        Assert.Equal(new PointD(1, 5), s[1]);
    }

    [Fact]
    public void ScatterChart_PlacesMarkers_WithYFlip()
    {
        // (0,0) → bottom-left; (1,1) → top-right (Y increases upward in value space).
        var b = DrawHarness.Render(2, 2, ctx => ctx.ScatterChart(new Rect(0, 0, 2, 2), [new(0, 0), new(1, 1)], Color.FromRgb(0, 200, 0)));
        Assert.Equal("●", b[0, 1].Grapheme);
        Assert.Equal("●", b[1, 0].Grapheme);
    }

    [Fact]
    public void ScatterChart_CustomMarkerGlyph_Overrides()
    {
        var b = DrawHarness.Render(2, 2, ctx =>
            new ScatterChart([new(0, 0), new(1, 1)], Color.FromRgb(0, 200, 0)) { MarkerGlyph = "⨯" }
                .Render(ctx, new Rect(0, 0, 2, 2)));
        Assert.Equal("⨯", b[0, 1].Grapheme);
        Assert.Equal("⨯", b[1, 0].Grapheme);
    }

    [Fact]
    public void LineChart_RasterizesBrailleBetweenPoints()
    {
        var b = DrawHarness.Render(4, 4, ctx => ctx.LineChart(new Rect(0, 0, 4, 4), [new(0, 0), new(3, 3)], Color.FromRgb(0, 200, 0)));
        Assert.True(IsBraille(b[0, 3].Grapheme));   // bottom-left (value 0,0)
        Assert.True(IsBraille(b[3, 0].Grapheme));   // top-right (value 3,3)
    }

    [Fact]
    public void LineChart_MonotoneCubic_DrawsBraille()
    {
        var b = DrawHarness.Render(12, 6, ctx =>
            ctx.LineChart(new Rect(0, 0, 12, 6), [new(0, 0), new(1, 0), new(2, 5), new(3, 5)], Color.FromRgb(0, 200, 0), CurveInterpolation.MonotoneCubic));
        int braille = 0;
        for (int r = 0; r < 6; r++)
            for (int c = 0; c < 12; c++)
                if (IsBraille(b[c, r].Grapheme)) braille++;
        Assert.True(braille > 4);   // a visible curve
    }

    [Fact]
    public void LineChart_Markers_AlignWithTheBrailleRow()
    {
        // Regression: markers must project through the same 2×4 grid as the braille (they used a 1×1
        // grid that rounded a cell off). A flat line at y=1 in a tall plot → markers on the braille row.
        var b = DrawHarness.Render(8, 6, ctx =>
            new LineChart([new(0, 1), new(7, 1)], Color.FromRgb(0, 200, 0))
            { XRange = new AxisRange(0, 7), YRange = new AxisRange(0, 10), ShowMarkers = true }
            .Render(ctx, new Rect(0, 0, 8, 6)));

        var brailleRows = new HashSet<int>();
        var markerRows = new HashSet<int>();
        for (int r = 0; r < 6; r++)
            for (int c = 0; c < 8; c++)
            {
                string? g = b[c, r].Grapheme;
                if (string.IsNullOrEmpty(g)) continue;
                if (g[0] is >= '⠀' and <= '⣿') brailleRows.Add(r);
                else if (g == "●") markerRows.Add(r);
            }
        Assert.NotEmpty(markerRows);
        Assert.Subset(brailleRows, markerRows);   // every marker sits on a braille row
    }

    [Fact]
    public void LineChart_SinglePoint_DrawsAMarker()
    {
        var b = DrawHarness.Render(3, 3, ctx =>
            new LineChart([new(1, 1)], Color.FromRgb(0, 200, 0)) { ShowMarkers = true }.Render(ctx, new Rect(0, 0, 3, 3)));
        int markers = 0;
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                if (b[c, r].Grapheme == "●") markers++;
        Assert.Equal(1, markers);
    }

    [Fact]
    public void PointSeriesCharts_EmptyOrNonFinite_AreNoOps()
    {
        DrawHarness.Render(4, 4, ctx =>
        {
            ctx.LineChart(new Rect(0, 0, 4, 4), ReadOnlySpan<PointD>.Empty, Color.FromRgb(0, 200, 0));
            ctx.ScatterChart(new Rect(0, 0, 4, 4), [new(double.NaN, 1), new(2, double.PositiveInfinity)], Color.FromRgb(0, 200, 0));
        });   // must not throw
    }
}
