using System.Diagnostics;
using Cursorial.Output;
using Cursorial.Tests.UI.PrecedenceMatrix;
using Cursorial.UI;
using Xunit.Abstractions;

namespace Cursorial.Tests.UI.Benchmarks;

/// <summary>
/// The §14 P0 store spike (design doc: "a throwaway store spike benchmarked at 300 elements ×
/// hover-flip churn") — measures the Fork A value store under the workloads the allocation
/// discipline bar (§2.3) is written against, and prints the numbers recorded in
/// <c>docs/ui-layer-design.md</c> ("P0 store spike results"). Deterministic
/// <c>GC.GetAllocatedBytesForCurrentThread</c> deltas after warm-up, single-threaded, fast enough
/// to run in every suite invocation — the assertions pin the allocation contract (exact zeros on
/// the steady-state hot paths, a generous drift bound on the churn path); the timing lines are
/// informational and only meaningful from a Release run. Timing is best-of-5 repetitions: on this
/// hardware single-shot numbers swing 2x+ with tiered-JIT promotion timing and P/E-core
/// scheduling, while allocation counts are exact and asserted on the <em>worst</em> repetition.
/// </summary>
[Trait("Category", "Benchmark")]
public class StoreSpikeBenchmark(ITestOutputHelper output)
{
    private const int Repetitions = 5;

    /// <summary>The hover frame's color setter — the styling vocabulary shape (record struct).</summary>
    private static readonly StyledProperty<Color> HoverBackground =
        UIProperty.Register<BenchHost, Color>("SpikeHoverBackground");

    /// <summary>The hover frame's second setter.</summary>
    private static readonly StyledProperty<int> HoverPadding =
        UIProperty.Register<BenchHost, int>("SpikeHoverPadding");

    /// <summary>The animated-lane property (the S5 storyboard shape: a double).</summary>
    private static readonly StyledProperty<double> SpikeOpacity =
        UIProperty.Register<BenchHost, double>("SpikeOpacity", defaultValue: 1.0);

    /// <summary>The cold/warm micro-number property.</summary>
    private static readonly StyledProperty<int> SpikeScratch =
        UIProperty.Register<BenchHost, int>("SpikeScratch");

    /// <summary>
    /// A host with the virtual-channel override present — the realistic dispatch shape (every
    /// UIElement overrides <c>OnPropertyChanged</c>); counts deliveries so each scenario can assert
    /// it actually exercised the notification path.
    /// </summary>
    private sealed class BenchHost : UIObject
    {
        public int Changes;

        protected override void OnPropertyChanged(in UIPropertyChangedEventArgs args)
        {
            base.OnPropertyChanged(in args);
            Changes++;
        }
    }

    /// <summary>
    /// Lets background tiered-JIT promotion land before measuring: in Release the first ~hundred
    /// milliseconds run TieredPGO-instrumented tier-0 code that is <em>slower than Debug MinOpts</em>
    /// (empirically ~8x on the storeless read path), and promotion is asynchronous, so warm-up call
    /// counts alone don't get the optimized bodies installed. A busy spin rather than
    /// <c>Thread.Sleep</c> — a sleeper can wake on an efficiency core and poison the timings.
    /// Callers re-warm briefly afterwards so the promoted bodies are faulted in.
    /// </summary>
    private static void SettleJit()
    {
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 250)
        {
        }
    }

    /// <summary>Wall time + thread-local allocated bytes for <paramref name="action"/> (the delegate is materialized by the caller, outside the window).</summary>
    private static (double Milliseconds, long Bytes) Measure(Action action)
    {
        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        action();
        var elapsed = Stopwatch.GetElapsedTime(start);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        return (elapsed.TotalMilliseconds, bytes);
    }

    /// <summary>Best (minimum) wall time and worst (maximum) allocation across <paramref name="repetitions"/> runs of <paramref name="action"/>.</summary>
    private static (double BestMilliseconds, long WorstBytes) MeasureBest(int repetitions, Action action, ITestOutputHelper? trace = null)
    {
        var bestMs = double.MaxValue;
        var worstBytes = long.MinValue;

        for (var r = 0; r < repetitions; r++)
        {
            var (ms, bytes) = Measure(action);
            trace?.WriteLine($"  rep {r}: {ms:F2} ms, {bytes} bytes");
            bestMs = Math.Min(bestMs, ms);
            worstBytes = Math.Max(worstBytes, bytes);
        }

        return (bestMs, worstBytes);
    }

    /// <summary>
    /// Spike 1 — hover-flip churn: 300 objects, each owning a 2-setter style frame; per frame the
    /// 30-object hover window moves (30 <c>RemoveFrame</c> + 30 <c>AddFrame</c>, 2 property changes
    /// each = 120 notifications/frame), 1000 frames per repetition after warm-up. Withdrawn entries
    /// are retained by the store (Reevaluate keeps the <c>EffectiveValue</c> at <c>Unset</c> base),
    /// so steady-state churn pays no entry re-creation; the only per-op allocation is
    /// <c>ReevaluateFrameProperties</c>' dedupe list for multi-entry frames (~72 B/op).
    /// </summary>
    [Fact]
    public void Spike1_HoverFlipChurn_300Objects_1000Frames()
    {
        const int ObjectCount = 300;
        const int HoverCount = 30;
        const int FrameCount = 1000;
        const int Windows = ObjectCount / HoverCount;

        var hosts = new BenchHost[ObjectCount];
        var frames = new TestValueFrame[ObjectCount];
        for (var i = 0; i < ObjectCount; i++)
        {
            hosts[i] = new BenchHost();
            frames[i] = new TestValueFrame(1)
                .With(HoverBackground, Color.FromRgb(40, 44, 52))
                .With(HoverPadding, 1);
        }

        var hovered = -1;
        var tick = 0;

        void Tick()
        {
            var window = tick % Windows;

            if (hovered >= 0)
            {
                for (var i = hovered * HoverCount; i < (hovered + 1) * HoverCount; i++)
                    hosts[i].RemoveFrame(frames[i]);
            }

            for (var i = window * HoverCount; i < (window + 1) * HoverCount; i++)
                hosts[i].AddFrame(frames[i]);

            hovered = window;
            tick++;
        }

        var frameLoop = () =>
        {
            for (var t = 0; t < FrameCount; t++)
                Tick();
        };

        // Warm-up runs the exact measured delegate: invocation 1 pays first-touch costs
        // (ValueStore + EffectiveValue entries on all 300 objects); tiered promotion of this path
        // empirically lands only after ~200 ms of executing it (per-rep traces show reps 0-2 slow,
        // rep 4 fast when measuring cold), so warm until that window has passed, settle, fault in.
        for (var w = 0; w < 4; w++)
            frameLoop();
        SettleJit();
        frameLoop();
        GC.Collect();

        var changesBefore = 0;
        foreach (var host in hosts)
            changesBefore += host.Changes;

        var (ms, bytes) = MeasureBest(Repetitions, frameLoop, output);

        var changes = -changesBefore;
        foreach (var host in hosts)
            changes += host.Changes;

        // 30 removes (2 promotions) + 30 adds (2 changes) per frame — the dispatch path ran.
        Assert.Equal(Repetitions * FrameCount * HoverCount * 2 * 2, changes);

        var bytesPerFrame = bytes / (double)FrameCount;
        output.WriteLine(
            $"hover-flip churn: {ObjectCount} objects, {FrameCount} frames x ({HoverCount} remove + {HoverCount} add, 2 setters), best of {Repetitions}: " +
            $"{ms:F2} ms ({ms * 1000 / FrameCount:F1} us/frame), {bytesPerFrame:F0} bytes/frame steady-state, {changes / Repetitions:N0} notifications/rep");

        // Drift bound, not a target: the known per-op dedupe-list allocation lands well under this.
        Assert.True(bytesPerFrame <= 8192, $"hover churn allocated {bytesPerFrame:F0} bytes/frame (bound 8192)");
    }

    /// <summary>
    /// Spike 2 — the animated write path: one <see cref="AnimatedValueHandle{T}"/> over a double
    /// property, 10,000 distinct-value writes per repetition after warm-up. The §2.3 bar is exact:
    /// ZERO steady-state allocation (in-place entry mutate + struct carrier args), asserted on the
    /// worst repetition.
    /// </summary>
    [Fact]
    public void Spike2_AnimatedWriteLoop_10kWrites_ZeroAllocation()
    {
        const int Writes = 10_000;

        var host = new BenchHost();
        var handle = host.BeginAnimation(SpikeOpacity);

        // Warm-up: the first push creates the entry + flips the lane; the rest warm the
        // changing-value dispatch (carrier pool + tiered JIT). Values sit outside the measured
        // range (the measured sequence is strictly positive).
        for (var i = 1; i <= 800; i++)
            handle.SetValue(-i * (1.0 / 1_000));

        SettleJit();
        for (var i = 801; i <= 1_000; i++) // re-warm: fault in the promoted bodies
            handle.SetValue(-i * (1.0 / 1_000));
        GC.Collect();

        var sequence = 0L;
        var (ms, bytes) = MeasureBest(Repetitions, () =>
        {
            for (var i = 0; i < Writes; i++)
                handle.SetValue(++sequence * 1e-9); // distinct every write — full dispatch each time
        });

        Assert.Equal(0, bytes);
        Assert.Equal(1_000 + Repetitions * Writes, host.Changes);

        output.WriteLine(
            $"animated write: {Writes:N0} writes through one AnimatedValueHandle<double>, best of {Repetitions}: " +
            $"{ms:F2} ms ({ms * 1_000_000 / Writes:F0} ns/write), {bytes} bytes steady-state");
    }

    /// <summary>
    /// Spike 3 — cold and warm micro-numbers: first-write cost on a fresh object (store + entry
    /// materialization — bounded, never zero), first-read on a storeless object (default path),
    /// and the warm per-op costs. Warm reads and writes must allocate exactly zero. Cold
    /// measurements run as best-of-5 batches of 2,000 fresh instances each.
    /// </summary>
    [Fact]
    public void Spike3_ColdAndWarmMicroNumbers()
    {
        const int BatchSize = 2_000;
        const int WarmOps = 100_000;

        // Warm the code paths (metadata caches + tiered JIT) on throwaway instances so "cold"
        // means per-instance materialization cost under steady-state code, not first-JIT cost.
        long sink = 0;
        for (var i = 0; i < 2_000; i++)
        {
            var warmUp = new BenchHost();
            sink += warmUp.GetValue(SpikeScratch);
            warmUp.SetValue(SpikeScratch, 1);
        }

        SettleJit();
        for (var i = 0; i < 500; i++) // re-warm: fault in the promoted bodies
        {
            var warmUp = new BenchHost();
            sink += warmUp.GetValue(SpikeScratch);
            warmUp.SetValue(SpikeScratch, 1);
        }

        var writeHosts = new BenchHost[Repetitions * BatchSize];
        for (var i = 0; i < writeHosts.Length; i++)
            writeHosts[i] = new BenchHost();
        GC.Collect();

        var writeBatch = 0;
        var (coldSetMs, coldSetBytes) = MeasureBest(Repetitions, () =>
        {
            var start = writeBatch++ * BatchSize;
            for (var i = start; i < start + BatchSize; i++)
                writeHosts[i].SetValue(SpikeScratch, 42); // first write: ValueStore + entry table + EffectiveValue<int>
        });

        var readHosts = new BenchHost[Repetitions * BatchSize];
        for (var i = 0; i < readHosts.Length; i++)
            readHosts[i] = new BenchHost();
        GC.Collect();

        var readBatch = 0;
        var (coldGetMs, coldGetBytes) = MeasureBest(Repetitions, () =>
        {
            var start = readBatch++ * BatchSize;
            for (var i = start; i < start + BatchSize; i++)
                sink += readHosts[i].GetValue(SpikeScratch); // storeless read — the metadata-default path
        });

        var warm = new BenchHost();
        for (var i = 0; i < 10_000; i++)
        {
            warm.SetValue(SpikeScratch, -1 - (i & 1)); // changing values — warm the full dispatch
            sink += warm.GetValue(SpikeScratch);
        }
        GC.Collect();

        var sequence = 0;
        var (warmSetMs, warmSetBytes) = MeasureBest(Repetitions, () =>
        {
            for (var i = 0; i < WarmOps; i++)
                warm.SetValue(SpikeScratch, ++sequence); // changing value — full dispatch each write
        });

        var (warmGetMs, warmGetBytes) = MeasureBest(Repetitions, () =>
        {
            for (var i = 0; i < WarmOps; i++)
                sink += warm.GetValue(SpikeScratch);
        });

        Assert.Equal(0, coldGetBytes);  // a default read materializes nothing
        Assert.Equal(0, warmSetBytes);  // M269's pin, re-asserted at micro scale
        Assert.Equal(0, warmGetBytes);  // M266's pin
        Assert.True(sink != 0 || warm.Changes >= 0); // keep the read loops un-elidable

        output.WriteLine(
            $"cold SetValue (first write on a fresh object): {coldSetMs * 1_000_000 / BatchSize:F0} ns/op, " +
            $"{coldSetBytes / (double)BatchSize:F0} bytes/op (store + entry materialization)");
        output.WriteLine(
            $"cold GetValue (default read on a storeless object): {coldGetMs * 1_000_000 / BatchSize:F0} ns/op, 0 bytes/op");
        output.WriteLine(
            $"warm SetValue (changing value): {warmSetMs * 1_000_000 / WarmOps:F0} ns/op, 0 bytes");
        output.WriteLine(
            $"warm GetValue: {warmGetMs * 1_000_000 / WarmOps:F0} ns/op, 0 bytes");
    }
}
