using Cursorial.Rendering;

namespace Cursorial.Drawing;

/// <summary>
/// Several line series sharing one axis range, drawn into a single braille surface by <see cref="Render"/>.
/// Each series keeps its own color; where two series cross a cell their dots OR-merge into one glyph painted
/// in the later series' color (a terminal cell has one foreground — see §6 for why per-layer compositing
/// can't give crossings a distinct color until area-fills land).
/// All series share one range (the union of their data, or explicit <see cref="XRange"/>/<see cref="YRange"/>),
/// so they align; that same range is what you'd hand an <see cref="Axes"/> frame.
/// </summary>
public sealed class MultiLineChart : IChart
{
    private readonly ChartSeries[] _series;

    /// <summary>Create a multi-series line chart over <paramref name="series"/>.</summary>
    public MultiLineChart(IEnumerable<ChartSeries> series)
    {
        ArgumentNullException.ThrowIfNull(series);
        _series = [.. series];
    }

    /// <summary>How every series connects its points (default <see cref="CurveInterpolation.Linear"/>).</summary>
    public CurveInterpolation Interpolation { get; init; } = CurveInterpolation.Linear;

    /// <summary>Stamp a marker at each data point.</summary>
    public bool ShowMarkers { get; init; }

    /// <summary>Explicit X range; null (default) unions all series' X.</summary>
    public AxisRange? XRange { get; init; }

    /// <summary>Explicit Y range; null (default) unions all series' Y.</summary>
    public AxisRange? YRange { get; init; }

    /// <summary>The shared (union or explicit) X/Y range — pass these to an <see cref="Axes"/> frame.</summary>
    public (AxisRange X, AxisRange Y) ResolveRange()
    {
        AxisRange x = new(0, 1), y = new(0, 1);
        bool any = false;
        foreach (var s in _series)
        {
            var (sx, sy) = ChartMath.AutoRange(s.Points, null, null);
            (x, y) = any ? (x.Union(sx), y.Union(sy)) : (sx, sy);
            any = true;
        }
        return (XRange ?? x, YRange ?? y);
    }

    /// <inheritdoc/>
    public void Render(DrawingContext context, in Rect area)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_series.Length == 0) return;

        var (x, y) = ResolveRange();
        foreach (var s in _series)
            LineFor(s, x, y).Render(context, area);
    }

    private LineChart LineFor(ChartSeries series, AxisRange x, AxisRange y) =>
        new([.. series.Points], series.Brush)
        {
            Interpolation = Interpolation,
            ShowMarkers = ShowMarkers,
            XRange = x,
            YRange = y,
        };
}
