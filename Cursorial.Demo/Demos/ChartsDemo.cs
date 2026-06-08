using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;

// Phase-4a chart showcase: block-element bar charts (vertical + horizontal, gradient-filled) and a
// single-row sparkline, drawn once into a cached scene and composited each frame. Event-driven (static
// content): repaints only on resize. The charts are pure DrawingContext draw-ops (ctx.BarChart /
// ctx.Sparkline / chart.Render) over the Phase-4a BlockGlyphs resolver.
internal sealed class ChartsDemo : InteractiveDemo
{
    public override string Name => "charts";
    public override IReadOnlyList<string> Aliases => ["chart"];
    public override string Description => "Block-element bar charts (vertical / horizontal / gradient) and sparklines.";

    protected override string? IntroMessage =>
        "Charts demo. Opening alt screen — press q or Ctrl+C to exit.";

    private static readonly double[] Bars = [3, 7, 4, 9, 5, 8, 2, 6];
    private static readonly double[] Spark = [2, 5, 3, 8, 6, 9, 4, 7, 5, 10, 3, 6, 8, 4, 7];

    private Scene _scene = null!;
    private SceneCompositor _compositor = null!;

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
    }

    private void Paint(DrawingContext ctx)
    {
        var heading = Color.FromRgb(200, 210, 255);
        var label = Color.FromRgb(150, 160, 200);
        var green = Color.FromRgb(80, 210, 110);
        var amber = Color.FromRgb(235, 195, 90);
        var cyan = Color.FromRgb(120, 220, 232);

        ctx.DrawText(1, 0, "Cursorial Charts — bars · sparklines · lines · scatter · axes (Phase 4a–4d)", heading);

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

    protected override void RenderFrame(long frame) =>
        _compositor.Composite([new SceneLayer(_scene)], Buffer.AsView());
}
