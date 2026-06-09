using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Drawing;

/// <summary>
/// A line chart: the data connected by an interpolated curve, rasterized into braille at sub-cell
/// resolution. <see cref="Interpolation"/> selects straight segments, a centripetal Catmull-Rom spline,
/// or a no-overshoot Fritsch-Carlson monotone cubic. Optionally stamps markers at the data points.
/// Auto-ranges X and Y unless ranges are given; the <see cref="Brush"/> samples across the chart area.
/// </summary>
/// <remarks>Non-finite points are skipped (gap-as-break is a later refinement). Area-fill under the
/// curve is not yet supported (a later addition).</remarks>
public sealed class LineChart : IChart
{
    private readonly PointD[] _points;

    /// <summary>Create a line chart over <paramref name="points"/> painted with <paramref name="brush"/>.</summary>
    public LineChart(ReadOnlySpan<PointD> points, IBrush brush)
    {
        _points = points.ToArray();
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
    public IReadOnlyList<PointD> Points => _points;

    /// <summary>The line brush (never null).</summary>
    public IBrush Brush { get; init; }

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
    /// with lower layers (e.g. overlapping <c>MultiLineChart.ToLayers</c> fills compose, red∩blue → purple).
    /// </summary>
    public bool FillArea { get; init; }

    /// <summary>The brush for the filled area (when <see cref="FillArea"/>); null → the line <see cref="Brush"/>.
    /// Pass a translucent brush for readable overlapping fills.</summary>
    public IBrush? AreaBrush { get; init; }

    /// <inheritdoc/>
    public void Render(DrawingContext context, in Rect area)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (area.Columns <= 0 || area.Rows <= 0) return;

        var finite = _points.Where(ChartMath.Finite).ToList();
        if (finite.Count == 0) return;

        var (xRange, yRange) = ChartMath.AutoRange(_points, XRange, YRange);
        var projector = new PlotProjector(area, xRange, yRange, 2, 4);

        // Area fill first (cell backgrounds), so the foreground braille curve draws over it.
        if (FillArea) PaintArea(context, area, xRange, yRange);

        // Plot the curve in maximal runs of consecutive finite points, so a non-finite point (NaN / ±∞ —
        // "missing data") BREAKS the line into a gap rather than the curve jumping straight across it. Each
        // run is sampled independently; a run shorter than 2 points has no segment (a marker shows it).
        if (finite.Count >= 2)
        {
            int recordId = context.AddBrailleRecord(new Pen(Brush), area, overwrite: false);
            foreach (var run in FiniteRuns(_points))
            {
                if (run.Count < 2) continue;
                int per = Math.Max(2, area.Columns * 2 / Math.Max(1, run.Count - 1));
                var samples = Curves.Sample(Interpolation, run, per);
                for (int i = 0; i + 1 < samples.Count; i++)
                {
                    var (sx0, sy0) = projector.ToSub(samples[i].X, samples[i].Y);
                    var (sx1, sy1) = projector.ToSub(samples[i + 1].X, samples[i + 1].Y);
                    context.PlotBrailleSegment(sx0, sy0, sx1, sy1, recordId);
                }
            }
        }

        if (ShowMarkers || finite.Count == 1)
        {
            // Markers project through the same 2×4 grid (via ToCell) as the curve, so they sit on it.
            var cells = new PlotProjector(area, xRange, yRange, 2, 4);
            string glyph = string.IsNullOrEmpty(MarkerGlyph) ? ChartMath.MarkerGlyph(Marker) : MarkerGlyph;
            foreach (var p in finite)
            {
                var (column, row) = cells.ToCell(p.X, p.Y);
                if ((uint) column >= (uint) context.Bounds.Columns || (uint) row >= (uint) context.Bounds.Rows) continue;

                var color = Brush.ColorAt(column, row, area);
                context.Set(column, row, glyph, Style.Default.WithForeground(color).WithBackground(Colors.Transparent));
            }
        }
    }

    // Fill the cell backgrounds between the curve and the zero baseline (clamped into the Y range). The
    // curve's height is tracked at CONTINUOUS (sub-cell) resolution, and a boundary cell is shaded only when
    // the curve covers at least MinCellCoverage of it — so a cell the curve only clips isn't fully colored.
    private void PaintArea(DrawingContext context, in Rect area, AxisRange xRange, AxisRange yRange)
    {
        var fillBrush = AreaBrush ?? Brush;
        var proj = new PlotProjector(area, xRange, yRange, 2, 4);   // columns align with the braille curve
        double baseFrac = RowFraction(Math.Clamp(0.0, yRange.Min, yRange.Max), yRange, area.Rows);

        var curveFrac = new double[area.Columns];
        var hasCurve = new bool[area.Columns];
        foreach (var run in FiniteRuns(_points))
        {
            if (run.Count == 0) continue;
            int per = Math.Max(2, area.Columns * 2 / Math.Max(1, run.Count - 1));
            IReadOnlyList<PointD> samples = run.Count >= 2 ? Curves.Sample(Interpolation, run, per) : run;
            foreach (var s in samples)
            {
                int idx = proj.ToCell(s.X, s.Y).Column - area.Column;
                if ((uint) idx >= (uint) area.Columns) continue;
                double f = RowFraction(s.Y, yRange, area.Rows);
                // Keep the row furthest from the baseline — the curve's peak in this column.
                if (!hasCurve[idx] || Math.Abs(f - baseFrac) > Math.Abs(curveFrac[idx] - baseFrac))
                    curveFrac[idx] = f;
                hasCurve[idx] = true;
            }
        }

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
                // Sample the brush against the whole chart area (not the 1-column paint rect) so a gradient
                // area-fill flows across the chart rather than restarting per column.
                context.FillRectangle(new Rect(area.Column + idx, area.Row + first, 1, last - first + 1), fillBrush, area);
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
}
