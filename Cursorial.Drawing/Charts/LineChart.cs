using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;

namespace Cursorial.Drawing.Charts;

/// <summary>
/// A line chart: the data connected by an interpolated curve, rasterized into braille at sub-cell
/// resolution. <see cref="Interpolation"/> selects straight segments, a centripetal Catmull-Rom spline,
/// or a no-overshoot Fritsch-Carlson monotone cubic. Optionally stamps markers at the data points.
/// Auto-ranges X and Y unless ranges are given; the <see cref="Brush"/> samples across the chart area.
/// </summary>
/// <remarks>A non-finite point (NaN / ±∞ — "missing data") breaks the curve into a gap rather than
/// interpolating across it. Set <see cref="FillArea"/> to shade the region between the curve and the
/// zero baseline.</remarks>
public sealed class LineChart : IChart
{
    // What the last Render put where, for HitTest: the cells the curve passed through (each remembering
    // the sample that entered it — sub-cell-accurate data, not the cell-quantized readback), with actual
    // DATA points overriding samples in their cells (the tooltip shows the datum, not an interpolation).
    private readonly Dictionary<CellPosition, PointD> _renderedCells = new();

    // The braille stroke is a quarter-cell tall, so demanding the exact cell makes the curve fiddly to
    // point at. Each painted cell therefore also claims the NEIGHBOUR on the side its ink leans toward
    // — dots in the cell's upper half claim the cell above, lower half the cell below — giving every
    // point on the curve a two-cell-tall target roughly centred on the ink. Kept apart from the exact
    // record so real ink always answers first: a spilled claim can never shadow the curve's own cell.
    private readonly Dictionary<CellPosition, PointD> _adjacentCells = new();
    private (AxisRange x, AxisRange y) _renderedRange;
    private Rect _renderedArea;

    /// <summary>Create a line chart over <paramref name="points"/> painted with <paramref name="brush"/>.</summary>
    public LineChart(ReadOnlySpan<PointD> points, IBrush? brush = null)
    {
        Points = points.ToArray();
        Brush = brush ?? Brushes.Default;
    }

    /// <summary>Create a line chart over <paramref name="points"/> painted with <paramref name="brush"/>.</summary>
    public LineChart(IReadOnlyList<PointD> points, IBrush? brush = null)
    {
        Points = points.ToArray();
        Brush = brush ?? Brushes.Default;
    }

    /// <summary>Create a line chart over <paramref name="points"/> in a solid <paramref name="color"/>.</summary>
    public LineChart(ReadOnlySpan<PointD> points, Color color) : this(points, new SolidColorBrush(color)) { }

    /// <summary>Create a line chart from y-values, using each value's index as X.</summary>
    public static LineChart FromValues(ReadOnlySpan<double> values, IBrush brush)
    {
        var points = new PointD[values.Length];
        for (int i = 0; i < values.Length; i++)
            points[i] = new PointD(i, values[i]);
        return new LineChart(points, brush);
    }

    /// <summary>Create a line chart from y-values (X = index) in a solid <paramref name="color"/>.</summary>
    public static LineChart FromValues(ReadOnlySpan<double> values, Color color) =>
        FromValues(values, new SolidColorBrush(color));

    /// <summary>The data points (defensively copied).</summary>
    public IReadOnlyList<PointD> Points { get; }

    /// <summary>The line brush (never null).</summary>
    public IBrush Brush { get; }

    /// <summary>How points are connected (default <see cref="CurveInterpolation.Linear"/>).</summary>
    public CurveInterpolation Interpolation { get; init; } = CurveInterpolation.Linear;

    /// <summary>When true, stamp a marker at each data point.</summary>
    public bool ShowMarkers { get; init; }

    /// <summary>The marker glyph when <see cref="ShowMarkers"/> is set (default <see cref="MarkerStyle.Dot"/>).</summary>
    public MarkerStyle Marker { get; init; } = MarkerStyle.Dot;

    /// <summary>A custom marker glyph that overrides <see cref="Marker"/> when set (e.g. <c>"⨯"</c>).</summary>
    public string? MarkerGlyph { get; init; }

    /// <summary>Explicit X range; null (default) auto-ranges from the data.</summary>
    public AxisRange? XRange { get; init; }

    /// <summary>Explicit Y range; null (default) auto-ranges from the data.</summary>
    public AxisRange? YRange { get; init; }

    /// <summary>
    /// Fill the area between the curve and the zero baseline. The fill is a cell <b>background</b>, so the
    /// foreground braille curve still draws over it and a translucent <see cref="AreaBrush"/> alpha-blends
    /// with lower layers (e.g., overlapping <c>MultiLineChart.ToLayers</c> fills compose, red∩blue → purple).
    /// </summary>
    public bool FillArea { get; init; }

    /// <summary>The brush for the filled area (when <see cref="FillArea"/>); null → the line <see cref="Brush"/>.
    /// Pass a translucent brush for readable overlapping fills.</summary>
    public IBrush? AreaBrush { get; init; }

    /// <inheritdoc/>
    public void Render(DrawingContext context, in Rect area)
    {
        ArgumentNullException.ThrowIfNull(context);

        _renderedCells.Clear();
        _adjacentCells.Clear();

        if (area.Columns <= 0 || area.Rows <= 0) return;

        var points = Points;

        var finite = points.Where(ChartMath.Finite).ToList();
        if (finite.Count == 0) return;

        var (xRange, yRange) = ChartMath.AutoRange(points, XRange, YRange);
        var projector = new PlotProjector(area, xRange, yRange, 2, 4);

        _renderedRange = (xRange, yRange);
        _renderedArea = area;

        // Area-fill accumulators: the curve's height (continuous, sub-cell) vs the zero baseline, per column.
        // Allocated only when filling; both the fill and the line read the SAME single sampling pass below.
        double baseFrac = 0;
        double[]? curveFrac = null;
        bool[]? hasCurve = null;
        if (FillArea)
        {
            baseFrac = RowFraction(Math.Clamp(0.0, yRange.Min, yRange.Max), yRange, area.Rows);
            curveFrac = new double[area.Columns];
            hasCurve = new bool[area.Columns];
        }

        // The braille line is a DEFERRED record (flushed at scene end), so it always paints over the area-fill
        // backgrounds written afterward regardless of ordering here. -1 = no line (fewer than two points).
        int recordId = finite.Count >= 2 ? context.AddBrailleRecord(new Pen(Brush), area, overwrite: false) : -1;

        // ONE pass over the maximal finite runs (a non-finite point — NaN / ±∞, "missing data" — ends a run, so
        // the curve breaks into a gap instead of jumping across it). Each run's curve is sampled ONCE, and one
        // walk over the samples AND the segments between them feeds every consumer — the braille line, the
        // area-fill accumulator, and the hit-test record. Samples alone are not enough for the latter two: for
        // Linear the sampler returns the data points themselves and the braille plotter rasterizes the
        // connecting cells, so any per-sample consumer would skip every column without a data point in it
        // (the area fill visibly gapped between sparse points).
        var areaRect = area;   // a local function cannot capture the 'in' parameter

        // One visited sub-cell dot feeds both non-paint consumers: the hit record (cell-keyed) and the
        // area-fill accumulator. The cell comes from the WALKED sub position — never a reprojection,
        // which could round to a neighboring cell and desynchronize the record from the painted raster.
        void Visit(int subColumn, int subRow, in PointD p)
        {
            int column = subColumn >> 1, row = subRow >> 2;
            _renderedCells.TryAdd(new CellPosition(column, row), p);

            // Widen the target by one cell on the side the ink leans toward (braille rows 0-1 are the
            // cell's upper half, 2-3 the lower), clamped to the plot so the curve never claims a cell
            // outside the chart.
            int neighbour = (subRow & 3) < 2 ? row - 1 : row + 1;
            if (neighbour >= areaRect.Row && neighbour < areaRect.RowEnd)
                _adjacentCells.TryAdd(new CellPosition(column, neighbour), p);

            if (curveFrac is null)
                return;

            int idx = column - areaRect.Column;
            if ((uint) idx >= (uint) areaRect.Columns) return;

            double f = RowFraction(p.Y, yRange, areaRect.Rows);
            // Keep the row furthest from the baseline — the curve's peak in this column.
            if (!hasCurve![idx] || Math.Abs(f - baseFrac) > Math.Abs(curveFrac[idx] - baseFrac))
                curveFrac[idx] = f;
            hasCurve[idx] = true;
        }

        foreach (var run in FiniteRuns(points))
        {
            if (run.Count == 0) continue;
            int per = Math.Max(2, area.Columns * 2 / Math.Max(1, run.Count - 1));
            IReadOnlyList<PointD> samples = run.Count >= 2 ? Curves.Sample(Interpolation, run, per) : run;

            if (samples.Count == 1)
            {
                // A lone finite point (isolated between gaps) paints only when a marker will stamp it —
                // record it only then, so tooltips never fire on blank cells.
                if (ShowMarkers || finite.Count == 1)
                {
                    var (mx, my) = projector.ToSub(samples[0].X, samples[0].Y);
                    Visit(mx, my, samples[0]);
                }
                continue;
            }

            for (int i = 0; i + 1 < samples.Count; i++)
            {
                var a = samples[i];
                var b = samples[i + 1];
                var (ax, ay) = projector.ToSub(a.X, a.Y);
                var (bx, by) = projector.ToSub(b.X, b.Y);

                if (recordId >= 0)
                    context.PlotBrailleSegment(ax, ay, bx, by, recordId);

                // Walk the segment with the SAME Bresenham the braille plotter rasterizes with, so the
                // record's cell set matches the painted cells exactly (a rounded parametric walk visibly
                // diverged on steep segments). Each dot reports the segment's value at its position,
                // parametrized along the dominant axis.
                int wx = ax, wy = ay;
                int dxw = Math.Abs(bx - ax), dyw = -Math.Abs(by - ay);
                int sxw = ax < bx ? 1 : -1, syw = ay < by ? 1 : -1;
                int errW = dxw + dyw;
                double dominant = Math.Max(dxw, -dyw);

                while (true)
                {
                    double t = dominant == 0 ? 0.0
                             : dxw >= -dyw ? Math.Abs(wx - ax) / dominant
                             : Math.Abs(wy - ay) / dominant;
                    Visit(wx, wy, new PointD(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));

                    if (wx == bx && wy == by) break;
                    int e2 = 2 * errW;
                    if (e2 >= dyw) { errW += dyw; wx += sxw; }
                    if (e2 <= dxw) { errW += dxw; wy += syw; }
                }
            }
        }

        // Paint the accumulated fill (cell backgrounds); the deferred braille line flushes over it at scene end.
        if (curveFrac is not null)
            PaintAreaFill(context, area, baseFrac, curveFrac, hasCurve!, AreaBrush ?? Brush);

        // The data points override interpolated samples in their cells — hovering a datum's cell must
        // report the datum itself. Replace-only: a datum whose cell was never painted (a lone run
        // without markers) must not become hit-testable here.
        foreach (var p in finite)
        {
            var (column, row) = projector.ToCell(p.X, p.Y);
            var cell = new CellPosition(column, row);
            if (_renderedCells.ContainsKey(cell))
                _renderedCells[cell] = p;
        }

        if (ShowMarkers || finite.Count == 1)
        {
            // Markers project through the same 2×4 grid (via ToCell) as the curve, so they sit on it.
            var cells = new PlotProjector(area, xRange, yRange, 2, 4);
            string glyph = string.IsNullOrEmpty(MarkerGlyph) ? ChartMath.MarkerGlyph(Marker) : MarkerGlyph;
            foreach (var p in finite)
            {
                var (column, row) = cells.ToCell(p.X, p.Y);
                if (!context.IsVisible(column, row)) continue;

                var color = Brush.ColorAt(column, row, area);
                context.Set(column, row, glyph, Style.Default.WithForeground(color).WithBackground(Colors.Transparent));
            }
        }
    }

    // Shade the cell backgrounds between the curve and the zero baseline, from the per-column heights
    // accumulated during Render's single sampling pass. A boundary cell is shaded only when the curve covers
    // at least MinCellCoverage of it — so a cell the curve only clips isn't fully colored.
    private static void PaintAreaFill(DrawingContext context, in Rect area, double baseFrac,
                                      double[] curveFrac, bool[] hasCurve, IBrush fillBrush)
    {
        for (int idx = 0; idx < area.Columns; idx++)
        {
            if (!hasCurve[idx]) continue;   // gap column (missing data) → no fill
            double lo = Math.Min(curveFrac[idx], baseFrac);
            double hi = Math.Max(curveFrac[idx], baseFrac);

            // Shade the contiguous run of cells whose under-curve coverage clears the threshold. Only the two
            // boundary cells can fall short (interior cells are fully covered), so the kept run stays contiguous.
            int first = int.MaxValue, last = int.MinValue;
            for (int r = (int) Math.Floor(lo); r < (int) Math.Ceiling(hi); r++)
            {
                if ((uint) r >= (uint) area.Rows) continue;
                double coverage = Math.Min(hi, r + 1) - Math.Max(lo, r);   // fraction of cell r under the curve
                if (coverage >= MinCellCoverage) { first = Math.Min(first, r); last = Math.Max(last, r); }
            }
            if (first <= last)
            {
                // Sample the brush against the whole chart area (not the 1-column paint rect) so a gradient
                // area-fill flows across the chart rather than restarting per column.
                context.FillRectangle(new Rect(area.Column + idx, area.Row + first, 1, last - first + 1), fillBrush, area);
            }
        }
    }

    // The curve must cover at least this fraction (0–1) of a boundary cell before it's shaded — moderate
    // rounding so a cell the curve only clips isn't colored, while a clearly-covered cell still is.
    private const double MinCellCoverage = 0.35;

    // Continuous fractional cell row for a value (0 = top of the area, rows = bottom), matching the projector's
    // Y-flip so the fill tracks the curve's true height rather than a cell-quantized one.
    private static double RowFraction(double y, AxisRange yRange, int rows) =>
        (1.0 - Math.Clamp(yRange.Normalize(y), 0.0, 1.0)) * rows;

    // Split the points into maximal runs of consecutive finite points; non-finite points (NaN / ±∞) are the
    // gap boundaries. Used so the curve breaks at missing data instead of interpolating across it.
    private static IEnumerable<List<PointD>> FiniteRuns(IReadOnlyList<PointD> points)
    {
        var run = new List<PointD>();
        foreach (var p in points)
        {
            if (ChartMath.Finite(p))
                run.Add(p);
            else if (run.Count > 0)
            {
                yield return run;
                run = [];
            }
        }
        if (run.Count > 0) yield return run;
    }

    /// <inheritdoc/>
    public bool HitTest(CellPosition position, out object? hitObject)
    {
        hitObject = null;

        if (HitCoordinates(position) is not {} coordinates)
            return false;

        // RichText, matching MultiLineChart's shape: the marker glyph in the line's brush color
        // (sampled at the hit cell) as the indicator, then the coordinates.
        var rtb = new RichTextBuilder();
        rtb.Run(EffectiveMarkerGlyph, Style.Default.WithForeground(Brush.ColorAt(position.Column, position.Row, _renderedArea)));
        rtb.Run(" " + coordinates);
        hitObject = rtb.Build();
        return true;
    }

    // The coordinate half of a hit — MultiLineChart composes its own multi-series RichText from
    // these, one per intersecting line.
    internal string? HitCoordinates(CellPosition position)
    {
        // Exact ink first, then the leaned-toward neighbour — a spilled claim never shadows a cell
        // the curve actually painted.
        if (_renderedCells.TryGetValue(position, out var p) is false &&
            _adjacentCells.TryGetValue(position, out p) is false)
        {
            return null;
        }

        var (xRange, yRange) = _renderedRange;
        if (xRange.IsDegenerate || yRange.IsDegenerate) return null;

        return ChartMath.FormatPoint(p, xRange, yRange);
    }

    /// <summary>The marker glyph this line stamps (custom override, else the <see cref="Marker"/> style's).</summary>
    internal string EffectiveMarkerGlyph => string.IsNullOrEmpty(MarkerGlyph) ? ChartMath.MarkerGlyph(Marker) : MarkerGlyph;
}
