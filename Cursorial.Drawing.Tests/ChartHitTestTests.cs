using Cursorial.Drawing.Charts;
using Cursorial.Input;
using Cursorial.Media;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

// Chart hit-testing (tooltips): each chart records what it rendered where, and HitTest answers from
// that record — so hits track the painted geometry exactly (same projector, same clipping), not a
// recomputation that could drift from it.
public class ChartHitTestTests
{
    private static readonly Color Green = Color.FromRgb(0, 200, 0);
    private static readonly Color Red = Color.FromRgb(200, 0, 0);

    // ---- ScatterChart (the reference implementation) ----

    [Fact]
    public void Scatter_HitTest_HitsTheMarkerCell_AndMissesElsewhere()
    {
        var chart = new ScatterChart([new PointD(0, 0), new PointD(9, 9)], Green);
        DrawHarness.Render(10, 5, ctx => chart.Render(ctx, new Rect(0, 0, 10, 5)));

        Assert.True(chart.HitTest(new CellPosition(0, 4), out var tip));   // (0,0) → bottom-left
        Assert.Contains("X=0", (string)tip!);
        Assert.True(chart.HitTest(new CellPosition(9, 0), out _));         // (9,9) → top-right
        Assert.False(chart.HitTest(new CellPosition(0, 0), out _));        // top-left: nothing there
    }

    [Fact]
    public void Scatter_Braille_HitTest_AnswersAtCellPrecision()
    {
        // The braille path plots at sub-cell precision, but the pointer asks in CELLS — a hit must
        // match any point whose braille dot lands in the queried cell.
        var chart = new ScatterChart([new PointD(0, 0), new PointD(9, 9)], Green) { Marker = MarkerStyle.Braille };
        DrawHarness.Render(10, 5, ctx => chart.Render(ctx, new Rect(0, 0, 10, 5)));

        Assert.True(chart.HitTest(new CellPosition(0, 4), out var tip));
        Assert.Contains("Y=0", (string)tip!);
        Assert.False(chart.HitTest(new CellPosition(5, 4), out _));        // off both points
    }

    // ---- LineChart ----

    [Fact]
    public void Line_HitTest_HitsAlongTheCurve_AndMissesOffIt()
    {
        // A straight diagonal: every column has exactly one curve cell. The endpoints' cells are
        // pinned by the projector; the far corners are off the curve.
        var chart = new LineChart([new PointD(0, 0), new PointD(9, 9)], Green);
        DrawHarness.Render(10, 5, ctx => chart.Render(ctx, new Rect(0, 0, 10, 5)));

        Assert.True(chart.HitTest(new CellPosition(0, 4), out var tip));   // the (0,0) end
        Assert.Contains("X=0", TipText(tip));
        Assert.Contains("Y=0", TipText(tip));
        Assert.Contains(TipRuns(tip), r => HasForeground(r, Green) && r.Text == "●");   // the series indicator

        Assert.True(chart.HitTest(new CellPosition(9, 0), out _));         // the (9,9) end
        Assert.False(chart.HitTest(new CellPosition(9, 4), out _));        // bottom-right: off the curve
        Assert.False(chart.HitTest(new CellPosition(0, 0), out _));        // top-left: off the curve

        // The curve is continuous: every column between the endpoints has a hit-testable cell (the
        // connecting segments count, not just the sampled data points).
        for (int c = 1; c < 9; c++)
        {
            bool any = false;
            for (int r = 0; r < 5 && !any; r++)
                any = chart.HitTest(new CellPosition(c, r), out _);
            Assert.True(any, $"column {c} has no hit-testable curve cell");
        }
    }

    [Fact]
    public void Line_HitTest_PrefersTheDataPointInItsCell()
    {
        // Interpolated samples pass through the middle point's cell too, but the DATA point's exact
        // value is what the tooltip must report there — not a nearby sample's.
        var chart = new LineChart([new PointD(0, 0), new PointD(5, 7.3), new PointD(10, 0)], Green);
        DrawHarness.Render(11, 8, ctx => chart.Render(ctx, new Rect(0, 0, 11, 8)));

        var (column, row) = new PlotProjector(new Rect(0, 0, 11, 8), new AxisRange(0, 10), new AxisRange(0, 7.3), 2, 4)
            .ToCell(5, 7.3);
        Assert.True(chart.HitTest(new CellPosition(column, row), out var tip));
        Assert.Contains("7.3", TipText(tip));
    }

    [Fact]
    public void Line_HitTest_MissesAfterAnEmptyRender()
    {
        var chart = new LineChart([new PointD(0, 0), new PointD(9, 9)], Green);
        DrawHarness.Render(10, 5, ctx => chart.Render(ctx, new Rect(0, 0, 10, 5)));

        // A later render into a degenerate area must clear the record — no stale hits from the
        // previous frame's geometry.
        DrawHarness.Render(10, 5, ctx => chart.Render(ctx, new Rect(0, 0, 0, 0)));
        Assert.False(chart.HitTest(new CellPosition(0, 4), out _));
    }

    [Theory]
    [InlineData(5.0)]    // lands in one half of its cell
    [InlineData(7.5)]    // lands in the other
    public void Line_HitTest_ClaimsTheNeighbourTheInkLeansToward(double value)
    {
        // A braille stroke is a quarter-cell tall, so the exact-cell-only target was fiddly to point
        // at. Each painted cell also claims the neighbour on the side its ink sits — and ONLY that
        // side, so the band stays two cells tall rather than three.
        var chart = new LineChart([new PointD(0, value), new PointD(9, value)], Green)
        {
            YRange = new AxisRange(0, 10),
        };
        DrawHarness.Render(10, 5, ctx => chart.Render(ctx, new Rect(0, 0, 10, 5)));

        // Derive the ink's cell and leaning side from the projector the renderer itself uses.
        var (_, subRow) = new PlotProjector(new Rect(0, 0, 10, 5), new AxisRange(0, 9), new AxisRange(0, 10), 2, 4)
            .ToSub(4, value);
        int inkRow = subRow >> 2;
        int leaning = (subRow & 3) < 2 ? inkRow - 1 : inkRow + 1;
        int away = (subRow & 3) < 2 ? inkRow + 1 : inkRow - 1;

        Assert.True(chart.HitTest(new CellPosition(4, inkRow), out _), "the ink's own cell must hit");
        Assert.True(chart.HitTest(new CellPosition(4, leaning), out _), $"the leaned-toward cell (row {leaning}) must hit");

        if (away >= 0 && away < 5)
            Assert.False(chart.HitTest(new CellPosition(4, away), out _), $"the far side (row {away}) must not hit");
    }

    [Fact]
    public void Line_HitTest_TheBandNeverLeavesThePlot()
    {
        // A curve pinned to the top row must not claim row -1 (and the clamp must not silently
        // wrap into the plot's opposite edge).
        var chart = new LineChart([new PointD(0, 10), new PointD(9, 10)], Green) { YRange = new AxisRange(0, 10) };
        DrawHarness.Render(10, 3, ctx => chart.Render(ctx, new Rect(0, 0, 10, 3)));

        Assert.True(chart.HitTest(new CellPosition(4, 0), out _));
        Assert.False(chart.HitTest(new CellPosition(4, 2), out _));   // the far edge stays clear
    }

    // ---- MultiLineChart ----

    [Fact]
    public void MultiLine_HitTest_ReportsOneHitPerIntersectingLine()
    {
        // Two diagonals crossing at the center cell: the crossing reports BOTH series (one hit per
        // line, joined); a cell only one line passes through reports just that line.
        var chart = new MultiLineChart(
        [
            new ChartSeries([new PointD(0, 0), new PointD(10, 10)], Green),
            new ChartSeries([new PointD(0, 10), new PointD(10, 0)], Red),
        ]);
        DrawHarness.Render(11, 11, ctx => chart.Render(ctx, new Rect(0, 0, 11, 11)));

        // The crossing reports both series as RichText — each line led by the series' marker glyph
        // in that series' brush color, so the reader can tell WHICH line each value belongs to.
        Assert.True(chart.HitTest(new CellPosition(5, 5), out var crossing));
        var runs = TipRuns(crossing);
        Assert.Equal(2, runs.Count(r => r.Text.Contains("X=")));
        Assert.Contains(runs, r => HasForeground(r, Green) && r.Text == "●");
        Assert.Contains(runs, r => HasForeground(r, Red) && r.Text == "●");

        Assert.True(chart.HitTest(new CellPosition(0, 10), out var single));    // only the rising line
        var singleRuns = TipRuns(single);
        Assert.Equal(1, singleRuns.Count(r => r.Text.Contains("X=")));
        Assert.Contains(singleRuns, r => HasForeground(r, Green) && r.Text == "●");

        Assert.False(chart.HitTest(new CellPosition(10, 5), out _), "the right edge's middle is off both lines");
    }

    // The tooltip's full text, indicator glyphs included.
    private static string TipText(object? tip) => string.Concat(TipRuns(tip).Select(r => r.Text));

    // A run's carrier states its colors as brushes; the tooltip's indicators are solid.
    private static bool HasForeground(Cursorial.Rendering.Text.TextRun run, Color color)
        => run.Style.Foreground is Cursorial.Rendering.Media.SolidColorBrush s && s.Color == color;

    // Flattens a rich chart tooltip (RichText) into its text runs for assertion.
    private static List<Cursorial.Rendering.Text.TextRun> TipRuns(object? tip)
    {
        var rich = Assert.IsType<Cursorial.Rendering.Text.RichText>(tip);
        var runs = new List<Cursorial.Rendering.Text.TextRun>();
        foreach (var block in rich.Blocks)
            if (block is Cursorial.Rendering.Text.TextParagraph paragraph)
                foreach (var inline in paragraph.Inlines)
                    if (inline is Cursorial.Rendering.Text.TextRun run)
                        runs.Add(run);
        return runs;
    }

    [Fact]
    public void MultiLine_HitTest_WorksThroughTheLayeredPath()
    {
        // ChartPresenter renders an ILayeredChart via ToLayers, not Render — the hit record must be
        // populated by that path too.
        var chart = new MultiLineChart(
        [
            new ChartSeries([new PointD(0, 0), new PointD(10, 10)], Green),
            new ChartSeries([new PointD(0, 10), new PointD(10, 0)], Red),
        ]);

        var layers = chart.ToLayers(new Rect(0, 0, 11, 11));
        foreach (var layer in layers)
            layer.Dispose();

        Assert.True(chart.HitTest(new CellPosition(5, 5), out var crossing));
        Assert.Equal(2, TipRuns(crossing).Count(r => r.Text.Contains("X=")));
    }

    [Fact]
    public void Line_HitTest_MatchesThePaintedRaster_OnSteepSegments()
    {
        // The oracle pin: every braille cell the plotter painted must hit-test. The record walks the
        // SAME Bresenham as the rasterizer — a rounded parametric walk visibly diverged on steep
        // segments (missed painted cells → dead tooltip spots along the line).
        var chart = new LineChart([new PointD(0, 0), new PointD(2, 40), new PointD(4, 0), new PointD(6, 40), new PointD(8, 0)], Green);
        var b = DrawHarness.Render(10, 12, ctx => chart.Render(ctx, new Rect(0, 0, 10, 12)));

        int painted = 0, missed = 0;
        for (int r = 0; r < 12; r++)
        for (int c = 0; c < 10; c++)
        {
            var g = b[c, r].Grapheme;
            if (string.IsNullOrEmpty(g) || g[0] < '\u2800' || g[0] > '\u28FF') continue;
            painted++;
            if (!chart.HitTest(new CellPosition(c, r), out _)) missed++;
        }

        Assert.True(painted > 20, $"expected a substantial raster, got {painted} braille cells");
        Assert.True(missed == 0, $"{missed} of {painted} painted cells are not hit-testable");
    }

    [Fact]
    public void Line_HitTest_LonePointBetweenGaps_OnlyHitsWhenMarked()
    {
        // An isolated finite point between NaN gaps paints nothing without markers — and must not
        // hit-test; with markers it paints, and must.
        PointD[] points = [new(0, 0), new(1, 8), new(double.NaN, double.NaN), new(5, 5), new(double.NaN, double.NaN), new(8, 0), new(9, 8)];

        var bare = new LineChart(points, Green) { YRange = new AxisRange(0, 10) };
        DrawHarness.Render(10, 5, ctx => bare.Render(ctx, new Rect(0, 0, 10, 5)));
        var cell = new PlotProjector(new Rect(0, 0, 10, 5), new AxisRange(0, 9), new AxisRange(0, 10), 2, 4).ToCell(5, 5);
        Assert.False(bare.HitTest(new CellPosition(cell.Column, cell.Row), out _));

        var marked = new LineChart(points, Green) { YRange = new AxisRange(0, 10), ShowMarkers = true };
        DrawHarness.Render(10, 5, ctx => marked.Render(ctx, new Rect(0, 0, 10, 5)));
        Assert.True(marked.HitTest(new CellPosition(cell.Column, cell.Row), out var tip));
        Assert.Contains("Y=5", TipText(tip));
    }

    [Fact]
    public void Bar_HitTest_OutOfRangeValue_RecordsNoPhantomBar()
    {
        // A value entirely below an explicit zero-anchored range paints nothing — the unclamped
        // extent used to record a phantom bar over the label band below the plot.
        var chart = new BarChart([-20, 5], Green) { Range = new AxisRange(0, 10) };
        DrawHarness.Render(2, 2, ctx => chart.Render(ctx, new Rect(0, 0, 2, 2)));

        for (int r = 0; r < 6; r++)
            Assert.False(chart.HitTest(new CellPosition(0, r), out _), $"phantom hit for the out-of-range bar at row {r}");

        Assert.True(chart.HitTest(new CellPosition(1, 1), out var tip));   // the in-range bar still hits
        Assert.Equal("5", (string)tip!);
    }

    [Fact]
    public void MultiLine_HitTest_DegenerateToLayers_ClearsTheRecord()
    {
        // The layered path's degenerate-area early return must not leave last frame's record alive —
        // HitTest would report content that is no longer on screen.
        var chart = new MultiLineChart([new ChartSeries([new PointD(0, 0), new PointD(10, 10)], Green)]);
        DrawHarness.Render(11, 11, ctx => chart.Render(ctx, new Rect(0, 0, 11, 11)));
        Assert.True(chart.HitTest(new CellPosition(0, 10), out _));

        chart.ToLayers(Rect.Empty);
        Assert.False(chart.HitTest(new CellPosition(0, 10), out _));
    }

    // ---- area fill (gap regression from the maintainer's screenshot) ----

    [Fact]
    public void Line_AreaFill_FillsEveryColumnUnderALinearSegment()
    {
        // Two data points spanning ten columns: the fill must cover every column the SEGMENT crosses,
        // not just the two columns holding data points (Linear sampling returns only the data points —
        // the in-between geometry comes from walking the segments, same as the braille rasterizer).
        var chart = new LineChart([new PointD(0, 5), new PointD(9, 10)], Green)
        {
            FillArea = true,
            YRange = new AxisRange(0, 10),
        };
        var b = DrawHarness.Render(10, 5, ctx => chart.Render(ctx, new Rect(0, 0, 10, 5)));

        for (int c = 0; c < 10; c++)
        {
            bool filled = false;
            for (int r = 0; r < 5 && !filled; r++)
                filled = b[c, r].Style.Background == Green;
            Assert.True(filled, $"column {c} has no area fill");
        }
    }

    [Fact]
    public void MultiLine_AreaFill_FillsEveryColumn_PerSeries()
    {
        // The multi-line chart builds its per-series fills through the same LineChart plumbing — a
        // sparse series must not leave picket-fence gaps either.
        var chart = new MultiLineChart(
        [
            new ChartSeries([new PointD(0, 5), new PointD(9, 10)], Green) { FillArea = true },
        ])
        {
            YRange = new AxisRange(0, 10),
        };
        var b = DrawHarness.Render(10, 5, ctx => chart.Render(ctx, new Rect(0, 0, 10, 5)));

        for (int c = 0; c < 10; c++)
        {
            bool filled = false;
            for (int r = 0; r < 5 && !filled; r++)
                filled = b[c, r].Style.Background == Green;
            Assert.True(filled, $"column {c} has no area fill");
        }
    }

    // ---- BarChart ----

    [Fact]
    public void Bar_HitTest_ReportsTheValue_AcrossTheBarsCells()
    {
        // One bar, 12/16 tall over 2 rows: full bottom cell + half top cell — BOTH cells belong to
        // the bar; the cell beside it belongs to nothing.
        var chart = new BarChart([12], Green) { Range = new AxisRange(0, 16) };
        DrawHarness.Render(2, 2, ctx => chart.Render(ctx, new Rect(0, 0, 1, 2)));

        Assert.True(chart.HitTest(new CellPosition(0, 1), out var tip));   // the full cell
        Assert.Equal("12", (string)tip!);
        Assert.True(chart.HitTest(new CellPosition(0, 0), out _));         // the partial top cell
        Assert.False(chart.HitTest(new CellPosition(1, 1), out _));        // outside the chart area
    }

    [Fact]
    public void Bar_HitTest_ReportsTheCategory_AndSkipsTheLabelRow()
    {
        var chart = new BarChart([8, 4], Green) { Range = new AxisRange(0, 8), Categories = ["cpu", "mem"] };
        DrawHarness.Render(2, 3, ctx => chart.Render(ctx, new Rect(0, 0, 2, 3)));

        Assert.True(chart.HitTest(new CellPosition(0, 0), out var tip));   // "cpu" reaches the top
        Assert.Equal("cpu: 8", (string)tip!);
        Assert.True(chart.HitTest(new CellPosition(1, 1), out var tip2));  // "mem" fills the bottom plot row
        Assert.Equal("mem: 4", (string)tip2!);
        Assert.False(chart.HitTest(new CellPosition(0, 2), out _));        // the label row is not a bar
    }

    [Fact]
    public void Bar_HitTest_Horizontal_SpansTheBarsColumns()
    {
        var chart = new BarChart([6], Green) { Range = new AxisRange(0, 8), Orientation = BarOrientation.Horizontal };
        DrawHarness.Render(4, 1, ctx => chart.Render(ctx, new Rect(0, 0, 4, 1)));

        // 6/8 of 4 columns = 3 cells: the first three columns are the bar, the last is empty.
        Assert.True(chart.HitTest(new CellPosition(0, 0), out var tip));
        Assert.Equal("6", (string)tip!);
        Assert.True(chart.HitTest(new CellPosition(2, 0), out _));
        Assert.False(chart.HitTest(new CellPosition(3, 0), out _));
    }

    [Fact]
    public void Bar_HitTest_NegativeBar_HitsBelowTheBaseline()
    {
        // Signed bars: [-4, 4] over 2 rows puts the baseline mid-chart; the negative bar's cells
        // hang below it and must hit with the negative value.
        var chart = new BarChart([-4, 4], Green) { Range = new AxisRange(-4, 4) };
        DrawHarness.Render(2, 2, ctx => chart.Render(ctx, new Rect(0, 0, 2, 2)));

        Assert.True(chart.HitTest(new CellPosition(0, 1), out var tip));   // below the baseline
        Assert.Equal("-4", (string)tip!);
        Assert.True(chart.HitTest(new CellPosition(1, 0), out var tip2));  // above it
        Assert.Equal("4", (string)tip2!);
        Assert.False(chart.HitTest(new CellPosition(0, 0), out _));        // the negative bar's UPPER half: empty
        Assert.False(chart.HitTest(new CellPosition(1, 1), out _));        // the positive bar's LOWER half: empty
    }
}
