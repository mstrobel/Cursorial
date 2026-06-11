using System.Diagnostics;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

using Xunit.Abstractions;

namespace Cursorial.Tests.UI.Benchmarks;

/// <summary>
/// Probe 4 — the §14 P2 motion-storm CI gate, measured at full pipeline weight: a 200-position
/// pointer sweep across a ~300-element tree whose leaves are <em>hover-reactive</em> (they arm
/// <see cref="InteractionState.Pressed"/> on enter and clear it on leave through the sanctioned
/// protected setter — the ButtonBase-at-P5 shape), with an installed
/// <see cref="IInteractionStateObserver"/> (the P3 styling-engine slot) and a
/// <see cref="InputDispatcher.HoverChanged"/> subscriber riding every flip. Two legs:
/// the per-<c>Move</c> dispatch path asserts <b>exactly zero</b> steady-state allocation
/// (worst repetition), and the frame-loop leg drains the whole 200-event storm plus render plus
/// the Phase-6 <see cref="InputDispatcher.UpdateHover"/> re-diff inside the 33 ms budget
/// (best-of-5). Methodology follows <see cref="StoreSpikeBenchmark"/> (ND25): warm the exact
/// measured delegate, busy-spin for tiered-JIT promotion, re-warm, then best-of-5 timings with
/// allocation asserted on the worst repetition. Numbers are recorded in
/// <c>docs/ui-layer-design.md</c> ("Probe 4 / motion-storm results"); the matrix's
/// <c>Section14_Perf.N200</c> is the lean always-on gate, this benchmark is the loaded one.
/// </summary>
[Trait("Category", "Benchmark")]
public class MotionStormBenchmark(ITestOutputHelper output)
{
    private const int Repetitions = 5;
    private const int SweepLength = 200;
    private const int SweepsPerRepetition = 50; // 10,000 Move dispatches per measured repetition

    /// <summary>
    /// A 2×2 leaf that writes interaction state in reaction to hover — the heaviest legitimate
    /// per-flip work a P2 control can hang off the hover chain (state commit + service routing +
    /// pressed-holder fan-in + observer notification).
    /// </summary>
    private sealed class HoverReactiveLeaf : UIElement
    {
        public int Flips;

        protected override Size MeasureOverride(Size availableSize) => new(2, 2);

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            Flips++;
            SetInteractionState(InteractionState.Pressed, true);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            Flips++;
            SetInteractionState(InteractionState.Pressed, false);
        }
    }

    private sealed class CountingObserver : IInteractionStateObserver
    {
        public int Notifications;

        public void OnInteractionStateChanged(UIElement element, InteractionState oldState, InteractionState newState)
            => Notifications++;
    }

    /// <summary>Busy-spin settle for asynchronous tiered-JIT promotion (see <see cref="StoreSpikeBenchmark"/>).</summary>
    private static void SettleJit()
    {
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 250)
        {
        }
    }

    /// <summary>Wall time + thread-local allocated bytes for one invocation of <paramref name="action"/>.</summary>
    private static (double Milliseconds, long Bytes) Measure(Action action)
    {
        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        action();
        var elapsed = Stopwatch.GetElapsedTime(start);
        return (elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - bytesBefore);
    }

    /// <summary>
    /// The probe-1-shaped dashboard tree: 300 hover-reactive 2×2 leaves (30 × 10) on an 80×24
    /// canvas, plus the 200-position row-3 sweep crossing a leaf boundary every other cell.
    /// </summary>
    private static (UITestHost Host, HoverReactiveLeaf[] Leaves, MouseEvent[] Sweep) CreateStorm()
    {
        var host = UITestHost.Create();
        var root = new Canvas();
        var leaves = new HoverReactiveLeaf[300];
        for (var row = 0; row < 10; row++)
        {
            for (var column = 0; column < 30; column++)
            {
                var leaf = new HoverReactiveLeaf();
                leaves[row * 30 + column] = leaf;
                Canvas.SetLeft(leaf, column * 2 + 10);
                Canvas.SetTop(leaf, row * 2 + 2);
                root.Children.Add(leaf);
            }
        }

        host.ShowRoot(root);
        host.RunFrame(); // settle layout + render — RenderTree.HitTest valid

        var sweep = new MouseEvent[SweepLength];
        for (var i = 0; i < sweep.Length; i++)
        {
            sweep[i] = new MouseEvent
            {
                Kind = MouseEventKind.Move,
                Position = new CellPosition(i % 80, 3), // the top leaf band (rows 2–3)
                Button = MouseButton.None,
                ButtonsHeld = MouseButtons.None,
                Modifiers = KeyModifiers.None,
                Timestamp = DateTimeOffset.UnixEpoch,
            };
        }

        return (host, leaves, sweep);
    }

    [Fact]
    public void Probe4_MotionStorm_ZeroMoveAllocation_FrameWithinBudget()
    {
        var (host, leaves, sweep) = CreateStorm();
        using var _ = host;

        var dispatcher = host.Application.InputDispatcher;
        var observer = new CountingObserver();
        host.Application.InteractionStateObserver = observer;
        var hoverChanges = 0;
        dispatcher.HoverChanged += (removed, added) => hoverChanges += removed.Count + added.Count;

        // ───────────── leg 1: the per-Move dispatch path (allocation contract — exact zero) ─────────────

        var storm = () =>
                    {
                        for (var n = 0; n < SweepsPerRepetition; n++)
                        {
                            foreach (var move in sweep)
                                dispatcher.ProcessEvent(move);
                        }
                    };

        // Warm the exact measured delegate (first-touch pool/scratch/store costs + tier-0), settle
        // promotion, fault the promoted bodies in, then clean the heap before sampling.
        storm();
        storm();
        SettleJit();
        storm();
        GC.Collect();

        var bestMs = double.MaxValue;
        var worstBytes = long.MinValue;
        for (var rep = 0; rep < Repetitions; rep++)
        {
            var (ms, bytes) = Measure(storm);
            output.WriteLine($"  move-path rep {rep}: {ms:F2} ms, {bytes} bytes");
            bestMs = Math.Min(bestMs, ms);
            worstBytes = Math.Max(worstBytes, bytes);
        }

        const int movesPerRepetition = SweepsPerRepetition * SweepLength;
        output.WriteLine(
            $"move path: {movesPerRepetition:N0} Move dispatches over 300 hover-reactive leaves, best of {Repetitions}: " +
            $"{bestMs:F2} ms ({bestMs * 1_000_000 / movesPerRepetition:F0} ns/move, " +
            $"{bestMs * 1000 / SweepsPerRepetition:F1} us per 200-move sweep), {worstBytes} bytes steady-state (worst rep)");

        Assert.Equal(0, worstBytes); // the §2.3 bar: the Move/hover/state path allocates NOTHING

        // The flips actually flowed: enter/leave virtuals, the observer, and HoverChanged all ran.
        Assert.True(leaves.Sum(static leaf => leaf.Flips) > 0, "hover-reactive flips never ran");
        Assert.True(observer.Notifications > 0, "the interaction-state observer never ran");
        Assert.True(hoverChanges > 0, "HoverChanged never raised");

        // ───────────── leg 2: the frame-loop leg (timing budget — one 30 fps frame period) ─────────────

        // Each repetition enqueues the full 200-event storm and drains it in ONE frame: Phase 1
        // dispatch (hover flips + state writes riding), render, and the Phase-6 UpdateHover
        // re-diff (RequestRender keeps the frame a rendered one so Phase 6 runs — ND21).
        var bestFrameMs = double.MaxValue;
        var worstFrameMs = 0d;
        for (var rep = 0; rep < Repetitions + 2; rep++) // 2 warm-up frames, 5 measured
        {
            foreach (var move in sweep)
                host.SendInput(move);
            host.Application.RequestRender();

            var (ms, _) = Measure(host.RunFrame);
            if (rep < 2)
                continue;

            output.WriteLine($"  frame rep {rep - 2}: {ms:F2} ms");
            bestFrameMs = Math.Min(bestFrameMs, ms);
            worstFrameMs = Math.Max(worstFrameMs, ms);
        }

        output.WriteLine(
            $"frame loop: 200-event storm drained in one frame (dispatch + render + UpdateHover), best of {Repetitions}: " +
            $"{bestFrameMs:F2} ms (worst {worstFrameMs:F2} ms, budget 33 ms)");

        Assert.True(
            bestFrameMs <= 33,
            $"Motion-storm frame budget exceeded: best-of-{Repetitions} was {bestFrameMs:F2} ms (budget 33 ms).");
    }
}
