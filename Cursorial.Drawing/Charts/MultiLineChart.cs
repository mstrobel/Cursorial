using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;

namespace Cursorial.Drawing.Charts;

/// <summary>
/// Several line series sharing one axis range, drawn into a single braille surface by <see cref="Render"/>.
/// Each series keeps its own color; where two series cross a cell their dots OR-merge into one glyph painted
/// in the later series' color (a terminal cell has one foreground — see §6 for why per-layer compositing
/// can't give crossings a distinct color until area-fills land).
/// All series share one range (the union of their data, or explicit <see cref="XRange"/>/<see cref="YRange"/>),
/// so they align; that same range is what you'd hand an <see cref="Axes"/> frame.
/// </summary>
public sealed class MultiLineChart : ILayeredChart
{
    // The per-series LineCharts the last Render/ToLayers drew with, kept for HitTest: each line owns
    // the record of the cells it painted, so the multi-chart's hit test is just the union of theirs.
    // The area is kept alongside to sample each series' brush at the hit cell for the tooltip's
    // colored series indicator.
    private readonly List<LineChart> _renderedLines = [];
    private Rect _renderedArea;

    /// <summary>Create a multi-series line chart over <paramref name="series"/>.</summary>
    public MultiLineChart(IEnumerable<ChartSeries> series)
    {
        ArgumentNullException.ThrowIfNull(series);
        Series = [.. series];
    }

    public IReadOnlyList<ChartSeries> Series { get; }

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
        foreach (var s in Series)
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

        _renderedLines.Clear();   // before ANY early return — no stale hit records from a prior frame

        var series = Series;
        if (series.Count == 0) return;

        var (x, y) = ResolveRange();
        _renderedArea = area;
        foreach (var s in series)
        {
            var line = LineFor(s, x, y);
            _renderedLines.Add(line);
            line.Render(context, area);
        }
    }

    /// <summary>
    /// Render each series into its OWN scene (a layer) sharing the resolved range, for the caller to
    /// composite with a <see cref="SceneCompositor"/>. Unlike <see cref="Render"/> (one surface, last-writer
    /// color at crossings), per-layer compositing lets <b>translucent area fills</b> alpha-blend where series
    /// overlap (red∩blue → purple — see §6), so this pays off when series set <see cref="ChartSeries.FillArea"/>
    /// with a translucent <see cref="ChartSeries.AreaBrush"/>. Each scene is sized to <paramref name="area"/>
    /// and drawn at its own origin; the caller offsets/clips via <c>CompositeParameters</c> and owns disposal.
    /// </summary>
    public IReadOnlyList<Scene> ToLayers(in Rect area)
    {
        _renderedLines.Clear();   // before ANY early return — no stale hit records from a prior frame

        if (area.Columns <= 0 || area.Rows <= 0) return [];   // degenerate area → nothing to lay out (matches Render)
        var (x, y) = ResolveRange();
        var series = Series;
        var layers = new List<Scene>(series.Count);
        var local = new Rect(0, 0, Math.Max(1, area.Columns), Math.Max(1, area.Rows));
        _renderedArea = local;   // the layers composite at this local frame — the same frame hits arrive in
        foreach (var s in series)
        {
            var scene = Scene.Create(local.Columns, local.Rows);
            var line = LineFor(s, x, y);
            _renderedLines.Add(line);
            scene.Draw(ctx => line.Render(ctx, local));
            layers.Add(scene);
        }
        return layers;
    }

    /// <inheritdoc/>
    public bool HitTest(CellPosition position, out object? hitObject)
    {
        hitObject = null;

        // Series can intersect: where several lines pass through the queried cell, report one hit per
        // line, together (drawing order), so a crossing's tooltip carries each series' value there.
        List<(LineChart Line, string Coordinates)>? hits = null;
        foreach (var line in _renderedLines)
        {
            if (line.HitCoordinates(position) is {} coordinates)
                (hits ??= []).Add((line, coordinates));
        }

        if (hits is null)
            return false;

        // RichText, one line per hit series: the series' marker glyph in its brush color (sampled at
        // the hit cell, so gradients read true) as the indicator, then the coordinates. The tooltip's
        // content presenter renders RichText natively.
        var rtb = new RichTextBuilder();
        bool first = true;
        foreach (var (line, coordinates) in hits)
        {
            if (!first) rtb.LineBreak();
            first = false;

            var color = line.Brush.ColorAt(position.Column, position.Row, _renderedArea);
            rtb.Run(line.EffectiveMarkerGlyph, Style.Default.WithForeground(color));
            rtb.Run(" " + coordinates);
        }

        hitObject = rtb.Build();
        return true;
    }

    private LineChart LineFor(ChartSeries series, AxisRange x, AxisRange y) =>
        new([.. series.Points], series.Brush)
        {
            Interpolation = Interpolation,
            ShowMarkers = ShowMarkers,
            XRange = x,
            YRange = y,
            FillArea = series.FillArea,
            AreaBrush = series.AreaBrush,
        };
}
