using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;

namespace Cursorial.Drawing.Charts;

/// <summary>
/// A scatter chart: a marker glyph at each point. Whole-cell markers (●○■◆▲✕) are written immediately;
/// <see cref="MarkerStyle.Braille"/> plots a single sub-cell braille dot per point (so several points can
/// share a cell at sub-cell precision). Auto-ranges X and Y from the data unless ranges are given. The
/// <see cref="Brush"/> samples per cell across the chart area.
/// </summary>
public sealed class ScatterChart : IChart
{
    private readonly Dictionary<CellPosition, PointD> _renderedPoints = new();
    private (AxisRange x, AxisRange y) _renderedRange;

    /// <summary>Create a scatter chart over <paramref name="points"/> painted with <paramref name="brush"/>.</summary>
    public ScatterChart(ReadOnlySpan<PointD> points, IBrush? brush = null)
    {
        Points = points.ToArray();
        Brush = brush ?? Brushes.Default;
    }

    /// <summary>Create a scatter chart over <paramref name="points"/> painted with <paramref name="brush"/>.</summary>
    public ScatterChart(IReadOnlyList<PointD> points, IBrush? brush = null)
    {
        Points = points.ToArray();
        Brush = brush ?? Brushes.Default;
    }

    /// <summary>Create a scatter chart over <paramref name="points"/> in a solid <paramref name="color"/>.</summary>
    public ScatterChart(ReadOnlySpan<PointD> points, Color color) : this(points, new SolidColorBrush(color)) { }

    /// <summary>The plotted points (defensively copied).</summary>
    public IReadOnlyList<PointD> Points { get; }

    /// <summary>The marker brush (never null).</summary>
    public IBrush Brush { get; }

    /// <summary>The marker glyph (default <see cref="MarkerStyle.Dot"/>).</summary>
    public MarkerStyle Marker { get; init; } = MarkerStyle.Dot;

    /// <summary>
    /// A custom marker glyph that overrides <see cref="Marker"/> when set — e.g. <c>"⨯"</c> for the
    /// cross-product symbol. A single display-width glyph is expected (wider clusters occupy their cells).
    /// </summary>
    public string? MarkerGlyph { get; init; }

    /// <summary>Explicit X range; null (default) auto-ranges from the data.</summary>
    public AxisRange? XRange { get; init; }

    /// <summary>Explicit Y range; null (default) auto-ranges from the data.</summary>
    public AxisRange? YRange { get; init; }

    /// <inheritdoc/>
    public void Render(DrawingContext context, in Rect area)
    {
        ArgumentNullException.ThrowIfNull(context);

        _renderedPoints.Clear();

        var points = Points;
        if (points.Count == 0 || area.Columns <= 0 || area.Rows <= 0) return;

        var (xRange, yRange) = ChartMath.AutoRange(points, XRange, YRange);

        _renderedRange = (xRange, yRange);

        string? custom = string.IsNullOrEmpty(MarkerGlyph) ? null : MarkerGlyph;

        if (custom is null && Marker == MarkerStyle.Braille)
        {
            var projector = new PlotProjector(area, xRange, yRange, 2, 4);
            int recordId = context.AddBrailleRecord(new Pen(Brush), area, overwrite: false);
            foreach (var p in points)
            {
                if (!ChartMath.Finite(p)) continue;
                var (subColumn, subRow) = projector.ToSub(p.X, p.Y);
                context.PlotBrailleDot(subColumn, subRow, recordId);
                // The pointer asks in CELLS, so the record is cell-keyed even though the dot is sub-cell.
                _renderedPoints[new CellPosition(subColumn / 2, subRow / 4)] = p;
            }
            return;
        }

        // Whole-cell markers project through the same 2×4 grid as braille (via ToCell), so a marker
        // lands in the same cell a braille dot for that point would — keeping markers aligned to curves.
        var cells = new PlotProjector(area, xRange, yRange, 2, 4);
        string glyph = custom ?? ChartMath.MarkerGlyph(Marker);
        foreach (var p in points)
        {
            if (!ChartMath.Finite(p)) continue;
            var (column, row) = cells.ToCell(p.X, p.Y);
            if (!context.IsVisible(column, row)) continue;

            var color = Brush.ColorAt(column, row, area);
            context.Set(column, row, glyph, Style.Default.WithForeground(color).WithBackground(Colors.Transparent));
            _renderedPoints[new CellPosition(column, row)] = p;
        }
    }

    /// <inheritdoc/>
    public bool HitTest(CellPosition position, out object? hitObject)
    {
        hitObject = null;

        if (_renderedPoints.TryGetValue(position, out var p) is false)
            return false;

        var (xRange, yRange) = _renderedRange;
        if (xRange.IsDegenerate || yRange.IsDegenerate) return false;

        hitObject = ChartMath.FormatPoint(p, xRange, yRange);
        return true;
    }
}
