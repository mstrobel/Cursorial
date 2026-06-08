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

    /// <inheritdoc/>
    public void Render(DrawingContext context, in Rect area)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (area.Columns <= 0 || area.Rows <= 0) return;

        var finite = _points.Where(ChartMath.Finite).ToList();
        if (finite.Count == 0) return;

        var (xRange, yRange) = ChartMath.AutoRange(_points, XRange, YRange);
        var projector = new PlotProjector(area, xRange, yRange, 2, 4);

        if (finite.Count >= 2)
        {
            int per = Math.Max(2, area.Columns * 2 / Math.Max(1, finite.Count - 1));
            var samples = Curves.Sample(Interpolation, finite, per);

            int recordId = context.AddBrailleRecord(new Pen(Brush), area, overwrite: false);
            for (int i = 0; i + 1 < samples.Count; i++)
            {
                var (sx0, sy0) = projector.ToSub(samples[i].X, samples[i].Y);
                var (sx1, sy1) = projector.ToSub(samples[i + 1].X, samples[i + 1].Y);
                context.PlotBrailleSegment(sx0, sy0, sx1, sy1, recordId);
            }
        }

        if (ShowMarkers || finite.Count == 1)
        {
            var cells = new PlotProjector(area, xRange, yRange, 1, 1);
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
}
