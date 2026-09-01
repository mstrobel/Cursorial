using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;

// ReSharper disable CheckNamespace

// The 'rasterbench' command — Probe 1 of the UI-layer phase plan (docs/ui-layer-design.md §14 P0).
// The measurement itself is fully headless (see RasterBenchmark.cs) so agents/CI can run it with
// redirected stdio and still get the table. On a real terminal it first plays a live preview of
// the two scenarios (so a human can see what is being measured), then runs the headless pass and
// prints per-stage mean/p50/p95.
internal sealed class RasterBenchDemo : IDemo
{
    public string Name => "rasterbench";
    public IReadOnlyList<string> Aliases => ["rbench"];
    public string Description =>
        "Probe 1 — headless scene-raster benchmark: whole-zone re-raster vs banded scroll re-anchor (200×60). " +
        "Args: [iterations] [warmup] [nolive]";

    public async Task RunAsync(string argument)
    {
        int iterations = 300, warmup = 30;
        bool live = !Console.IsOutputRedirected && !Console.IsInputRedirected;

        int intArg = 0;
        foreach (var token in argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Equals("nolive", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("headless", StringComparison.OrdinalIgnoreCase))
            {
                live = false;
            }
            else if (int.TryParse(token, out int n) && n > 0)
            {
                if (intArg++ == 0) iterations = n;
                else warmup = n;
            }
            else
            {
                Console.WriteLine($"Unrecognized argument '{token}' — usage: rasterbench [iterations] [warmup] [nolive]");
                return;
            }
        }

        if (live)
            await new LivePreviewDemo().RunAsync("");

        Console.WriteLine($"Measuring headless: {iterations} iterations (+{warmup} warmup) per scenario…");
        var report = RasterBenchmark.Run(iterations, warmup, progress: Console.WriteLine);
        Console.WriteLine(RasterBenchmark.Format(report));
    }

    // Live view of the exact pipeline being measured: alternates between the mutating dashboard
    // (scenario A2) and the banded scroll (scenario B), blitting each frame's 200×60 composite
    // target into the session buffer (clipped to the real terminal) with a one-line HUD of that
    // frame's stage timings. Display cost is NOT part of any measured number — the headless pass
    // that follows produces the report.
    private sealed class LivePreviewDemo : InteractiveDemo
    {
        public override string Name => "rasterbench";
        public override string Description => "";

        protected override bool Animated => true;
        protected override TimeSpan FrameInterval => TimeSpan.FromMilliseconds(16);
        protected override string IntroMessage =>
            "Live preview — alternates scenario A (whole-zone re-raster dashboard) and scenario B (banded scroll).\n" +
            "Press q or Esc to continue to the measured headless run.";

        private const int PhaseFrames = 300;   // ~5 s per scenario at 33 ms/frame

        private static readonly CellStyle HudStyle = CellStyle.Default
            .WithForeground(Color.FromRgb(13, 15, 24))
            .WithBackground(Color.FromRgb(224, 175, 104));

        private readonly char[] _buffer = new char[1024];
        private DashboardScenario _dashboard = null!;
        private BandScrollScenario _band = null!;

        protected override void Initialize()
        {
            _dashboard = new DashboardScenario(mutate: true);
            _band = new BandScrollScenario();
        }

        protected override void RenderFrame(long frame)
        {
            bool dashboardPhase = frame % (2 * PhaseFrames) < PhaseFrames;
            RasterBenchScenario scenario = dashboardPhase ? _dashboard : _band;

            var sample = scenario.Step();
            Buffer.Blit(scenario.Target, Buffer.Bounds);

            Span<char> buffer = _buffer;

            buffer.TryWrite($" {(dashboardPhase ? "A · whole-zone re-raster" : $"B · banded scroll (row {_band.ScrollRow})")}" +
                            $"  raster {sample.RasterMs,7:F3} ms  composite {sample.CompositeMs,7:F3} ms" +
                            $"  diff+emit {sample.DiffMs,7:F3} ms  {sample.Bytes,7} B/frame  ·  q = measure",
                            out int cc);
            
            string hud = $" {(dashboardPhase ? "A · whole-zone re-raster" : $"B · banded scroll (row {_band.ScrollRow})")}" +
                         $"  raster {sample.RasterMs,7:F3} ms  composite {sample.CompositeMs,7:F3} ms" +
                         $"  diff+emit {sample.DiffMs,7:F3} ms  {sample.Bytes,7} B/frame  ·  q = measure";

            if (cc < Buffer.Columns)
                buffer.Slice(cc, Buffer.Columns - cc).Fill(' ');

            DemoSupport.PaintTextRow(Buffer.AsView(), 0, Buffer.Rows - 1,
                                     buffer[..Buffer.Columns],
                                     HudStyle);
        }
    }
}
