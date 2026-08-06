using Cursorial.Drawing;
using Cursorial.Drawing.Charts;
using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Rendering;

// ReSharper disable CheckNamespace

// Chart showcase: block-element bar charts, sparklines, axed braille line/scatter charts, and the deferred
// features now landed — signed bars (negatives below a zero baseline) with category labels, line-chart area
// fill, NaN-as-gap breaks, and MultiLineChart.ToLayers (two translucent filled series composited so their
// overlap blends). Most content is one cached scene composited each frame; the ToLayers fills are separate
// cached scenes layered over it. Event-driven (static): repaints only on resize.
internal sealed class ChartsDemo : InteractiveDemo
{
    public override string Name => "charts";
    public override IReadOnlyList<string> Aliases => ["chart"];
    public override string Description =>
        "Bars (signed + categories), sparklines, lines/scatter + axes, area fill, gaps, layered translucent fills.";

    protected override string IntroMessage =>
        "Charts demo. Opening alt screen — press q or Ctrl+C to exit.";

    private static readonly double[] Bars = [3, 7, 4, 9, 5, 8, 2, 6];
    private static readonly double[] Spark = [2, 5, 3, 8, 6, 9, 4, 7, 5, 10, 3, 6, 8, 4, 7];
    private static readonly double[] Signed = [4, -2, 5, -3, 2, -1];
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun"];

    // Two filled series for the ToLayers translucent-overlap showcase; OverlayA carries a NaN gap.
    private static readonly PointD[] OverlayA = [new(0, 2), new(1, 5), new(2, double.NaN), new(3, 4), new(4, 7), new(5, 3)];
    private static readonly PointD[] OverlayB = [new(0, 6), new(1, 3), new(2, 5), new(3, 8), new(4, 4), new(5, 6)];

    private Scene? _scene;
    private SceneCompositor? _compositor;
    private Scene[] _overlay = [];          // ToLayers scenes, composited over _scene
    private (int Column, int Row) _overlayAt;
    private const int NewBand = 21;         // row where the new-features band starts

    protected override void Initialize() => Build();

    protected override void OnResize(int columns, int rows)
    {
        base.OnResize(columns, rows);
        Build();
    }

    private void Build()
    {
        _compositor = new SceneCompositor(Style);
        _scene?.Dispose();
        _scene = Scene.Create(Buffer.Columns, Buffer.Rows);
        _scene.Draw(Paint);
        BuildOverlay();
    }

    // The ToLayers translucent-fill overlap: two filled series on separate cached scenes, so where the fills
    // overlap they alpha-blend (a single-surface render can't — one background per cell, last-writer-wins).
    private void BuildOverlay()
    {
        foreach (var s in _overlay) s.Dispose();
        _overlay = [];

        if (Buffer.Columns <= 44 || Buffer.Rows < NewBand + 8) return;   // no room for the right-column band

        int w = Math.Min(38, Buffer.Columns - 41);
        var chart = new MultiLineChart(
        [
            new ChartSeries(OverlayA, Color.FromRgb(235, 110, 150)) { FillArea = true, AreaBrush = new SolidColorBrush(Color.FromRgba(235, 110, 150, 110)) },
            new ChartSeries(OverlayB, Color.FromRgb(110, 200, 235)) { FillArea = true, AreaBrush = new SolidColorBrush(Color.FromRgba(110, 200, 235, 110)) },
        ]) { Interpolation = CurveInterpolation.MonotoneCubic };
        _overlay = [.. chart.ToLayers(new Rect(0, 0, w, 6))];
        _overlayAt = (40, NewBand + 1);
    }

    private void Paint(DrawingContext ctx)
    {
        var heading = Color.FromRgb(200, 210, 255);
        var label = Color.FromRgb(150, 160, 200);
        var green = Color.FromRgb(80, 210, 110);
        var amber = Color.FromRgb(235, 195, 90);
        var cyan = Color.FromRgb(120, 220, 232);

        ctx.DrawText(1, 0, "Cursorial Charts — bars · sparklines · lines · scatter · axes · signed bars · area fill · gaps", heading);

        // Vertical bar chart (eighth-block fractional heights), with a one-cell gap between bars.
        ctx.DrawText(1, 2, "Vertical bars:", label);
        new BarChart(Bars, green) { Gap = 1 }.Render(ctx, new Rect(1, 3, 26, 6));

        // Single-row sparkline.
        ctx.DrawText(1, 10, "Sparkline:", label);
        ctx.Sparkline(1, 11, Math.Min(40, Math.Max(1, ctx.Bounds.Columns - 2)), Spark, amber);

        // Gradient-filled bars — the brush samples per cell across the chart area (bottom→top fade).
        ctx.DrawText(1, 13, "Gradient bars:", label);
        var gradient = new LinearGradientBrush(Color.FromRgb(60, 180, 90), Color.FromRgb(80, 200, 235),
                                               startPoint: RelativePoint.Bottom, endPoint: RelativePoint.Top);
        new BarChart(Bars, gradient) { Gap = 1 }.Render(ctx, new Rect(1, 14, 26, 6));

        // Right column: an axed braille line chart (monotone-cubic, gridlines) + a scatter plot.
        if (ctx.Bounds.Columns > 44)
        {
            int w = Math.Min(38, ctx.Bounds.Columns - 41);

            ctx.DrawText(40, 2, "Lines (multi-series) + axes:", label);
            var magenta = Color.FromRgb(230, 120, 200);
            ChartSeries[] series = [new ChartSeries(LineData, cyan), new ChartSeries(LineData2, magenta)];
            var (ux, uy) = new MultiLineChart(series).ResolveRange();
            var axes = new Axes(ux, uy)
            {
                XAxis = new Axis { Gridlines = true },
                YAxis = new Axis { Gridlines = true },
                LabelColor = label,
            };
            var layout = axes.Render(ctx, new Rect(40, 3, w, 8));
            new MultiLineChart(series)
            {
                Interpolation = CurveInterpolation.MonotoneCubic, ShowMarkers = true,
                XRange = layout.X, YRange = layout.Y,
            }.Render(ctx, layout.Plot);

            ctx.DrawText(40, 12, "Scatter:", label);
            ctx.ScatterChart(new Rect(40, 13, w, 6), Scatter, amber);
        }

        // ---- Deferred features now landed: signed bars + category labels, area fill + NaN gap, layered fills ----
        if (ctx.Bounds.Rows >= NewBand + 8)
        {
            ctx.DrawText(1, NewBand, "Signed bars + category labels:", label);
            new BarChart(Signed, cyan) { Gap = 1, Categories = Months, LabelColor = label }
                .Render(ctx, new Rect(1, NewBand + 1, 26, 7));

            // The area-fill + NaN-gap + layered-blend charts are composited as separate ToLayers scenes
            // (see BuildOverlay / RenderFrame); here we just label that region.
            if (ctx.Bounds.Columns > 44)
                ctx.DrawText(40, NewBand, "Area fill + NaN gap, two layers blended:", label);
        }
    }

    private static readonly PointD[] LineData =
        [new(0, 1), new(1, 1), new(2, 4), new(3, 6), new(4, 6), new(5, 9), new(6, 9), new(7, 10)];

    private static readonly PointD[] LineData2 =
        [new(0, 8), new(1, 6), new(2, 7), new(3, 3), new(4, 5), new(5, 2), new(6, 3), new(7, 1)];

    private static readonly PointD[] Scatter =
    [
        new(0, 2), new(1, 5), new(2, 3), new(3, 8), new(4, 6), new(5, 9),
        new(6, 4), new(7, 7), new(2, 7), new(5, 2), new(6, 8), new(3, 1),
    ];

    protected override void RenderFrame(long frame)
    {
        if (_scene is not {} scene)
            return;

        if (_overlay.Length == 0)
        {
            _compositor?.Composite([new SceneLayer(scene)], Buffer.AsView());
            return;
        }

        // Main scene first, then each ToLayers fill layer offset into the area-fill region so their
        // translucent backgrounds compose (overlap blends rather than last-writer-wins).
        var layers = new SceneLayer[1 + _overlay.Length];

        layers[0] = new SceneLayer(_scene);

        for (int i = 0; i < _overlay.Length; i++)
        {
            layers[i + 1] = new SceneLayer(_overlay[i],
                                           new CompositeParameters(offsetColumn: _overlayAt.Column, offsetRow: _overlayAt.Row));
        }

        _compositor?.Composite(layers, Buffer.AsView());
    }
}
