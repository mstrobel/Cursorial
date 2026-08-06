using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using Cursorial.Drawing;
using Cursorial.Drawing.Charts;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;

// ReSharper disable CheckNamespace

// Probe 1 (docs/ui-layer-design.md §14 P0): the scene-raster benchmark. Headless — no
// TerminalSession, no TTY; everything renders into an ArrayBufferWriter so the numbers are pure
// CPU cost (raster / composite / diff+emit), uncontaminated by terminal I/O. The question it
// answers: "how expensive is whole-zone re-raster vs banded re-anchor at realistic sizes?" — the
// numbers size how much of S1 T3's render-zone machinery P1 needs to build.
//
// Two scenarios, both on a 200×60 viewport:
//   A — whole-zone re-raster: a realistic dashboard (~300 draw ops: shadowed panels, gradient
//       fills, formatted-text blocks, junction-merged table strokes, sparklines) freshly
//       re-rastered EVERY frame, composited onto a retained target, diffed by FrameRenderer.
//       Measured twice: steady state (identical content → diff emits ~nothing) and with a small
//       per-frame mutation (ticking counter + one panel's data → small delta bytes).
//   B — banded scroll viewport: a band of viewport+2K rows of document content scrolling one row
//       per frame via composite offset only; when the viewport reaches the band edge the band
//       re-anchors (full band re-raster). Offset-only frames and re-anchor frames are reported
//       separately — their gap is exactly what banding buys.

/// <summary>One instrumented benchmark frame: per-stage wall time, emitted bytes, allocations.</summary>
internal readonly record struct BenchFrameSample(
    double RasterMs, double CompositeMs, double DiffMs, int Bytes, long AllocBytes, bool ReRastered)
{
    public double TotalMs => RasterMs + CompositeMs + DiffMs;
}

/// <summary>
/// Base for the two probe scenarios: owns the scene → compositor → retained target →
/// <see cref="FrameRenderer"/> pipeline and the per-stage stopwatch instrumentation. Subclasses
/// supply the painter, the per-frame state advance, and the layer placement.
/// </summary>
internal abstract class RasterBenchScenario : IDisposable
{
    public const int ViewportColumns = 200;
    public const int ViewportRows = 60;

    /// <summary>
    /// Truecolor + full styling — a modern-terminal capability set, so the renderer's quantizer
    /// runs (as it would live) but passes RGB through. Synchronized output stays off: the probe
    /// measures cell work, not protocol framing.
    /// </summary>
    public static OutputCapabilities BenchCapabilities { get; } = OutputCapabilities.None with
    {
        Color = ColorCapabilities.None with
        {
            Depth = ColorDepth.Truecolor,
            TruecolorVerified = true,
            DefaultColorReset = true,
        },
        Styling = new TextStylingCapabilities(Italic: true,
                                              Underline: true,
                                              ExtendedUnderline: true,
                                              ColoredUnderline: true,
                                              Strikethrough: true,
                                              Overline: true,
                                              Hyperlinks: false),
    };

    private readonly FrameRenderer _renderer = new(BenchCapabilities);
    private readonly ArrayBufferWriter<byte> _output = new(256 * 1024);
    private readonly SceneLayer[] _layers = new SceneLayer[1];
    private readonly Action<DrawingContext> _paint;

    protected readonly Scene Scene;
    protected readonly SceneCompositor Compositor;

    protected RasterBenchScenario(int sceneRows)
    {
        Scene = Scene.Create(ViewportColumns, sceneRows);
        Target = new CellBuffer(ViewportColumns, ViewportRows) { CursorVisible = false };
        Compositor = new SceneCompositor(Style.Default.WithForeground(Color.FromHex("#c0caf5"))
                                                      .WithBackground(Color.FromHex("#0d0f18")));
        _paint = Paint;
    }

    /// <summary>The retained composite target — exposed so the live preview can blit it.</summary>
    public CellBuffer Target { get; }

    /// <summary>Draw-op count of the most recent raster (set by the painter).</summary>
    public int DrawOpsLastRaster { get; protected set; }

    /// <summary>Re-raster the scene content. Must set <see cref="DrawOpsLastRaster"/>.</summary>
    protected abstract void Paint(DrawingContext ctx);

    /// <summary>Advance one frame of scenario state; return true when the scene must re-raster.</summary>
    protected abstract bool Advance();

    /// <summary>Where the scene sits on the target this frame (scenario B animates the offset).</summary>
    protected virtual CompositeParameters LayerParameters => CompositeParameters.Default;

    /// <summary>Run one frame through raster → composite → diff+emit, timing each stage.</summary>
    public BenchFrameSample Step()
    {
        bool reRaster = Advance() || Scene.IsDirty;   // first frame rasters regardless

        long alloc0 = GC.GetAllocatedBytesForCurrentThread();

        long t0 = Stopwatch.GetTimestamp();
        if (reRaster)
        {
            Scene.Invalidate();
            Scene.Draw(_paint);
        }

        long t1 = Stopwatch.GetTimestamp();
        _layers[0] = new SceneLayer(Scene, LayerParameters);
        Compositor.Composite(_layers, Target.AsView());

        long t2 = Stopwatch.GetTimestamp();
        _output.ResetWrittenCount();
        _renderer.Render(Target, _output);
        long t3 = Stopwatch.GetTimestamp();

        long alloc1 = GC.GetAllocatedBytesForCurrentThread();

        return new BenchFrameSample(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds,
                                    Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds,
                                    Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds,
                                    _output.WrittenCount,
                                    alloc1 - alloc0,
                                    reRaster);
    }

    public void Dispose() => Scene.Dispose();
}

/// <summary>
/// Scenario A: a 200×60 dashboard (~300 draw ops) whose whole zone is re-rastered every frame.
/// With <paramref name="mutate"/> a counter ticks each frame (status chip, one panel's metrics +
/// sparkline, status bar), so the diff emits a small honest delta; without it, content is
/// identical frame-over-frame and the diff should emit ~nothing.
/// </summary>
internal sealed class DashboardScenario(bool mutate) : RasterBenchScenario(ViewportRows)
{
    private static readonly Color PanelBg = Color.FromRgb(26, 30, 44);
    private static readonly Color Label = Color.FromRgb(120, 130, 160);
    private static readonly Color Value = Color.FromRgb(192, 202, 245);
    private static readonly Color Accent = Color.FromRgb(122, 162, 247);
    private static readonly Color Good = Color.FromRgb(158, 206, 106);
    private static readonly Color Warn = Color.FromRgb(224, 175, 104);
    private static readonly Color Shadow = Color.FromRgb(0, 0, 0);

    private static readonly string[] MetricLabels =
        ["throughput", "latency p99", "error rate", "queue depth", "cache hit", "goroutines"];

    // Formatted once (text layout is a layout-time cost in a retained UI, not a raster cost);
    // DrawFormattedText paints the laid-out blocks per raster.
    private static readonly FormattedText Header = FormatDoc(b => b
        .Run("CURSORIAL ", Style.Default.WithForeground(Accent).WithAttributes(TextAttributes.Bold))
        .Run("raster benchmark", Style.Default.WithForeground(Value))
        .LineBreak()
        .Run("probe 1 — whole-zone re-raster · 200×60 dashboard · ", Style.Default.WithForeground(Label))
        .Run("12 panels, junction table, log block", Style.Default.WithForeground(Good).WithAttributes(TextAttributes.Italic)),
        columns: 90, maxRows: 4);

    private static readonly FormattedText Log = FormatDoc(b =>
    {
        for (int i = 0; i < 10; i++)
        {
            b.Run($"12:4{i % 10} ", Style.Default.WithForeground(Label))
             .Run(i % 3 == 0 ? "WARN " : "INFO ", Style.Default.WithForeground(i % 3 == 0 ? Warn : Good))
             .Run($"zone {i} recomposited in {i * 3 + 1} ms", Style.Default.WithForeground(Value));
            if (i < 9) b.LineBreak();
        }
    }, columns: 39, maxRows: 11);

    private readonly IBrush _backdrop = new LinearGradientBrush(Color.FromRgb(16, 18, 28), Color.FromRgb(10, 11, 17),
                                                                startPoint: RelativePoint.Top, endPoint: RelativePoint.Bottom);
    private readonly IBrush _panelFill = new SolidColorBrush(PanelBg);
    private readonly IBrush _barFill = new LinearGradientBrush(Color.FromRgb(125, 207, 255), Color.FromRgb(187, 154, 247),
                                                               startPoint: RelativePoint.Left, endPoint: RelativePoint.Right);
    private readonly Pen _panelPen = new Pen(Color.FromRgb(65, 72, 104)) { Corners = CornerStyle.Rounded };
    private readonly Pen _rulePen = new(Color.FromRgb(65, 72, 104));
    private readonly Pen _tablePen = new(Color.FromRgb(86, 95, 137));   // JunctionMode.Merge by default
    private readonly double[] _spark = new double[24];

    private int _tick;

    private static FormattedText FormatDoc(Action<RichTextBuilder> build, int columns, int maxRows)
    {
        var builder = new RichTextBuilder();
        build(builder);
        return new TextFormatter().Format(builder.Build(), columns, maxRows, BenchCapabilities);
    }

    protected override bool Advance()
    {
        if (mutate) _tick++;
        return true;   // whole-zone model: every frame is a fresh raster
    }

    protected override void Paint(DrawingContext ctx)
    {
        int ops = 0;
        const int w = ViewportColumns, h = ViewportRows;

        // Backdrop wash.
        ctx.FillRectangle(new Rect(0, 0, w, h), _backdrop); ops++;

        // Header band (rows 0–5): formatted-text title block, status chips, rule.
        ctx.DrawFormattedText(Header, new Rect(2, 0, 90, 4), BenchCapabilities); ops++;
        for (int i = 0; i < 6; i++)
        {
            var chip = new Rect(w - 16 * (6 - i) - 2, 1, 14, 1);
            string text = i == 5 ? $"TICK {_tick:D6}" : $"{MetricLabels[i][..4].ToUpperInvariant()} {Pseudo(i, 7) % 100:D2}%";
            ctx.FillRectangle(chip, new SolidColorBrush(PanelBg)); ops++;
            ctx.DrawText(chip.Column + 1, 1, text, i == 5 ? Warn : Accent); ops++;
        }
        ctx.DrawLine(0, 5, w - 1, 5, _rulePen); ops++;

        // Panel grid: 4 × 3 shadowed metric panels (rows 6–44).
        for (int pr = 0; pr < 3; pr++)
            for (int pc = 0; pc < 4; pc++)
                ops += PaintPanel(ctx, new Rect(1 + pc * 50, 6 + pr * 13, 47, 12), pr * 4 + pc);

        // Junction table (rows 45–57): outer box + merged internal strokes + 2 text lines per cell.
        var table = new Rect(1, 45, 153, 13);
        ctx.DrawBox(table, _tablePen); ops++;
        for (int i = 1; i <= 7; i++)
        {
            int x = table.Column + i * 19;
            ctx.DrawLine(x, table.Row, x, table.RowEnd - 1, _tablePen); ops++;
        }
        for (int i = 1; i <= 3; i++)
        {
            int y = table.Row + i * 3;
            ctx.DrawLine(table.Column, y, table.ColumnEnd - 1, y, _tablePen); ops++;
        }
        for (int band = 0; band < 4; band++)
            for (int col = 0; col < 8; col++)
            {
                int x = table.Column + col * 19 + 2, y = table.Row + band * 3 + 1;
                ctx.DrawText(x, y, $"shard-{band}{col:X}", Label); ops++;
                ctx.DrawText(x, y + 1, $"{Pseudo(band * 8 + col, 9973),8:N0} rq", band % 2 == 0 ? Value : Good); ops++;
            }

        // Log panel right of the table.
        var logBox = new Rect(156, 45, 43, 13);
        ctx.DrawPanel(logBox, _panelPen, _panelFill, new PanelTitle("log").WithColor(Accent)); ops++;
        ctx.DrawFormattedText(Log, new Rect(logBox.Column + 2, logBox.Row + 1, logBox.Columns - 4, logBox.Rows - 2),
                              BenchCapabilities); ops++;

        // Status bar (row 59).
        ctx.FillRectangle(new Rect(0, h - 1, w, 1), new SolidColorBrush(Color.FromRgb(35, 40, 60))); ops++;
        ctx.DrawText(2, h - 1, "cursorial · rasterbench", Accent); ops++;
        ctx.DrawText(30, h - 1, $"frame {_tick:D6}", Value); ops++;
        ctx.DrawText(46, h - 1, "zone 200×60", Label); ops++;
        ctx.DrawText(62, h - 1, $"ops {DrawOpsLastRaster}", Label); ops++;
        ctx.DrawText(w - 26, h - 1, "whole-zone re-raster", Warn); ops++;

        DrawOpsLastRaster = ops;
    }

    private int PaintPanel(DrawingContext ctx, in Rect rect, int index)
    {
        int ops = 0;
        bool mutating = index == 5;   // exactly one panel rides the tick

        ctx.DrawDropShadow(rect, ShadowGeometry.Drop(radius: 1, strength: 0.6,
                                                     ShadowEdges.Bottom | ShadowEdges.Right), Shadow); ops++;
        ctx.DrawPanel(rect, _panelPen, _panelFill, new PanelTitle($"svc-{index:D2}").WithColor(Accent)); ops++;

        int cx = rect.Column + 2, cy = rect.Row + 1, cw = rect.Columns - 4;

        for (int m = 0; m < 6; m++)
        {
            long v = Pseudo(index * 31 + m + (mutating ? _tick : 0), 99991);
            ctx.DrawText(cx, cy + m, MetricLabels[m], Label); ops++;
            ctx.DrawText(cx + 16, cy + m, $"{v,10:N0}", m == 2 ? Warn : Value); ops++;
        }

        ctx.DrawLine(rect.Column + 1, cy + 6, rect.ColumnEnd - 2, cy + 6, _rulePen); ops++;

        for (int i = 0; i < _spark.Length; i++)
            _spark[i] = Pseudo(index * 7 + i + (mutating ? _tick : 0), 251) % 100;
        ctx.Sparkline(cx, cy + 7, cw, _spark, Good); ops++;

        int pct = (int) (Pseudo(index + (mutating ? _tick : 0), 101) % 101);
        ctx.FillRectangle(new Rect(cx, cy + 8, Math.Max(1, cw * pct / 100), 1), _barFill); ops++;
        ctx.DrawText(cx + cw - 4, cy + 9, $"{pct,3}%", Value); ops++;

        return ops;
    }

    /// <summary>Deterministic pseudo-random value — keeps every raster reproducible.</summary>
    private static long Pseudo(long seed, long mod)
    {
        unchecked
        {
            ulong x = (ulong) (seed + 0x9E3779B9);
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            return (long) (x % (ulong) mod);
        }
    }
}

/// <summary>
/// Scenario B: a banded scroll viewport. The scene is a band of viewport+2K document rows; the
/// viewport scrolls one row per frame by composite offset alone until it reaches the band edge,
/// then the band re-anchors (full band re-raster at a new anchor row). The per-frame gap between
/// the two frame classes is the payoff banding claims.
/// </summary>
internal sealed class BandScrollScenario : RasterBenchScenario
{
    public const int BandMargin = 15;                                  // K
    public const int BandRows = ViewportRows + 2 * BandMargin;         // viewport + 2K

    private static readonly Color Gutter = Color.FromRgb(86, 95, 137);
    private static readonly Color Keyword = Color.FromRgb(122, 162, 247);
    private static readonly Color Ident = Color.FromRgb(192, 202, 245);
    private static readonly Color Literal = Color.FromRgb(158, 206, 106);
    private static readonly Color Comment = Color.FromRgb(96, 106, 134);

    private static readonly string[] Keywords = ["let", "match", "async", "record", "yield", "await"];
    private static readonly string[] Calls = ["Compose", "Measure", "Arrange", "RenderZone", "HitTest", "Anchor"];

    private int _scrollRow;     // document row at the top of the viewport
    private int _anchorRow;     // document row at the top of the band (scene row 0)

    public BandScrollScenario() : base(BandRows) { }

    /// <summary>Document row currently at the top of the viewport.</summary>
    public int ScrollRow => _scrollRow;

    protected override CompositeParameters LayerParameters => new(0, _anchorRow - _scrollRow);

    protected override bool Advance()
    {
        _scrollRow++;
        if (_scrollRow >= _anchorRow && _scrollRow + ViewportRows <= _anchorRow + BandRows)
            return false;                                  // still inside the band — offset-only frame

        _anchorRow = Math.Max(0, _scrollRow - BandMargin); // crossed the edge — re-anchor the band
        return true;
    }

    protected override void Paint(DrawingContext ctx)
    {
        int ops = 0;
        for (int r = 0; r < BandRows; r++)
        {
            int doc = _anchorRow + r;
            if (doc % 24 == 0)
            {
                ctx.FillRectangle(new Rect(0, r, ViewportColumns, 1), new SolidColorBrush(Color.FromRgb(35, 40, 60))); ops++;
                ctx.DrawText(2, r, $"── section {doc / 24:D4} ── offset {doc:D6}", Keyword); ops++;
                continue;
            }

            ctx.DrawText(0, r, $"{doc,6}", Gutter); ops++;
            ops += PaintCodeLine(ctx, 8, r, doc);
        }

        DrawOpsLastRaster = ops;
    }

    private static int PaintCodeLine(DrawingContext ctx, int column, int row, int doc)
    {
        unchecked
        {
            ulong x = (ulong) doc * 0x9E3779B97F4A7C15UL;
            x ^= x >> 29;

            int indent = (int) (x % 4) * 4;
            string keyword = Keywords[(int) (x % (ulong) Keywords.Length)];
            string call = Calls[(int) ((x >> 8) % (ulong) Calls.Length)];

            int c = column + indent;
            c += ctx.DrawText(c, row, keyword, Keyword).Columns + 1;
            c += ctx.DrawText(c, row, $"zone_{x % 997:D3} = ", Ident).Columns;
            c += ctx.DrawText(c, row, $"{call}(band: {x % 91}, k: {x % 17})", Literal).Columns;
            ctx.DrawText(c + 2, row, $"// doc row {doc}", Comment);
            return 4;
        }
    }
}

/// <summary>Mean / p50 / p95 over a sample set (nearest-rank percentiles).</summary>
internal sealed record BenchStats(double Mean, double P50, double P95)
{
    public static BenchStats Of(List<double> samples)
    {
        if (samples.Count == 0) return new(0, 0, 0);
        var sorted = samples.Order().ToArray();
        return new(samples.Average(), Rank(sorted, 0.50), Rank(sorted, 0.95));
    }

    private static double Rank(double[] sorted, double q) =>
        sorted[Math.Clamp((int) Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
}

/// <summary>Per-stage stats for one frame class of one scenario.</summary>
internal sealed record BenchScenarioResult(string Title, int Frames, int DrawOps,
                                           BenchStats Raster, BenchStats Composite, BenchStats Diff,
                                           BenchStats Total, BenchStats Bytes, BenchStats Alloc);

/// <summary>The runner: warms up, iterates each scenario, aggregates, and formats the table.</summary>
internal static class RasterBenchmark
{
    public sealed record Report(string Machine, int Iterations, int Warmup,
                                BenchScenarioResult Steady, BenchScenarioResult Mutated,
                                BenchScenarioResult OffsetOnly, BenchScenarioResult ReAnchor);

    public static Report Run(int iterations, int warmup, Action<string>? progress = null)
    {
        // One throwaway pass over both scenario types before anything is measured: tiered-JIT
        // promotion (~30+ calls) and lazy globalization/text caches otherwise land inside the
        // FIRST measured scenario and fatten its tail percentiles by multiples.
        using (var jitWarm = new DashboardScenario(mutate: true))
            for (int i = 0; i < 60; i++) jitWarm.Step();
        using (var jitWarmBand = new BandScrollScenario())
            for (int i = 0; i < 60; i++) jitWarmBand.Step();

        progress?.Invoke("scenario A1 — whole-zone re-raster, steady state…");
        BenchScenarioResult steady;
        using (var scenario = new DashboardScenario(mutate: false))
            steady = Collect("A1 · whole-zone re-raster — steady state (identical content each frame)",
                             scenario, iterations, warmup);

        progress?.Invoke("scenario A2 — whole-zone re-raster, small per-frame mutation…");
        BenchScenarioResult mutated;
        using (var scenario = new DashboardScenario(mutate: true))
            mutated = Collect("A2 · whole-zone re-raster — small mutation each frame (counter + one panel)",
                              scenario, iterations, warmup);

        progress?.Invoke("scenario B — banded scroll viewport (1 row/frame)…");
        using var band = new BandScrollScenario();
        var offsetOnly = new List<BenchFrameSample>();
        var reAnchor = new List<BenchFrameSample>();
        int bandOps = 0;
        for (int i = 0; i < warmup; i++) band.Step();
        DrainGarbage();
        for (int i = 0; i < iterations; i++)
        {
            var sample = band.Step();
            (sample.ReRastered ? reAnchor : offsetOnly).Add(sample);
            if (sample.ReRastered) bandOps = band.DrawOpsLastRaster;
        }

        return new Report(MachineSummary(), iterations, warmup, steady, mutated,
                          Aggregate($"B  · banded scroll — composite-offset-only frames (band {BandScrollScenario.BandRows} rows, K={BandScrollScenario.BandMargin})",
                                    offsetOnly, drawOps: 0),
                          Aggregate("B  · banded scroll — re-anchor frames (band edge crossed → full band re-raster)",
                                    reAnchor, bandOps));
    }

    private static BenchScenarioResult Collect(string title, RasterBenchScenario scenario,
                                               int iterations, int warmup)
    {
        for (int i = 0; i < warmup; i++) scenario.Step();
        DrainGarbage();

        var samples = new List<BenchFrameSample>(iterations);
        for (int i = 0; i < iterations; i++)
            samples.Add(scenario.Step());

        return Aggregate(title, samples, scenario.DrawOpsLastRaster);
    }

    /// <summary>
    /// Collect leftovers from warmup (incl. the LOH cell buffers of disposed warm scenarios) so a
    /// pending Gen2/LOH pause from a PREVIOUS phase doesn't land inside the next measured loop.
    /// Per-frame Gen0 churn during measurement is deliberately left in — that's real workload cost.
    /// </summary>
    private static void DrainGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static BenchScenarioResult Aggregate(string title, List<BenchFrameSample> samples, int drawOps) =>
        new(title, samples.Count, drawOps,
            BenchStats.Of(samples.Select(s => s.RasterMs).ToList()),
            BenchStats.Of(samples.Select(s => s.CompositeMs).ToList()),
            BenchStats.Of(samples.Select(s => s.DiffMs).ToList()),
            BenchStats.Of(samples.Select(s => s.TotalMs).ToList()),
            BenchStats.Of(samples.Select(s => (double) s.Bytes).ToList()),
            BenchStats.Of(samples.Select(s => (double) s.AllocBytes).ToList()));

    public static string MachineSummary()
    {
#if DEBUG
        const string config = "Debug (NOT representative — rerun with -c Release)";
#else
        const string config = "Release";
#endif
        return $"{RuntimeInformation.OSDescription.Trim()}, {RuntimeInformation.ProcessArchitecture}, " +
               $"{Environment.ProcessorCount} logical cores, {RuntimeInformation.FrameworkDescription}, {config}";
    }

    public static string Format(Report report)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Probe 1 — scene-raster benchmark (headless)");
        sb.AppendLine($"machine:  {report.Machine}");
        sb.AppendLine($"viewport: {RasterBenchScenario.ViewportColumns}×{RasterBenchScenario.ViewportRows}; " +
                      $"band {BandScrollScenario.BandRows} rows (K={BandScrollScenario.BandMargin}); " +
                      $"{report.Iterations} iterations (+{report.Warmup} warmup) per scenario");

        AppendScenario(sb, report.Steady);
        AppendScenario(sb, report.Mutated);
        AppendScenario(sb, report.OffsetOnly);
        AppendScenario(sb, report.ReAnchor);
        return sb.ToString();
    }

    private static void AppendScenario(StringBuilder sb, BenchScenarioResult r)
    {
        sb.AppendLine();
        sb.AppendLine(r.Title);
        if (r.Frames == 0)
        {
            sb.AppendLine("  no frames in this class — increase iterations (the band edge was never crossed)");
            return;
        }

        sb.AppendLine($"  frames: {r.Frames}{(r.DrawOps > 0 ? $", draw ops/raster: {r.DrawOps}" : "")}");
        sb.AppendLine("  stage          mean        p50         p95");
        AppendRow(sb, "raster", r.Raster, Ms);
        AppendRow(sb, "composite", r.Composite, Ms);
        AppendRow(sb, "diff+emit", r.Diff, Ms);
        AppendRow(sb, "frame total", r.Total, Ms);
        AppendRow(sb, "bytes/frame", r.Bytes, Size);
        AppendRow(sb, "alloc/frame", r.Alloc, Size);
    }

    private static void AppendRow(StringBuilder sb, string stage, BenchStats stats, Func<double, string> unit) =>
        sb.AppendLine($"  {stage,-13}{unit(stats.Mean),10}  {unit(stats.P50),10}  {unit(stats.P95),10}");

    private static string Ms(double ms) => ms.ToString("F3", CultureInfo.InvariantCulture) + " ms";

    private static string Size(double bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString("F0", CultureInfo.InvariantCulture) + " B",
    };
}
