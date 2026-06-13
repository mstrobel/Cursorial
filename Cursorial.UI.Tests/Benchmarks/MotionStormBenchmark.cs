using System.Diagnostics;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
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
    private class HoverReactiveLeaf : UIElement
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

    /// <summary>
    /// The P3 leaf: hover-reactive AND style-targetable — two registered <c>AffectsRender</c>
    /// properties give a <c>:pointerover</c> rule real background-setter weight (store arbitration
    /// + render invalidation per flip), the §14 P3 re-assert load.
    /// </summary>
    private sealed class RestyledLeaf : HoverReactiveLeaf
    {
        public static readonly StyledProperty<int> HotProperty = UIProperty.Register<RestyledLeaf, int>("Hot");
        public static readonly StyledProperty<int> GlowProperty = UIProperty.Register<RestyledLeaf, int>("Glow");

        static RestyledLeaf() => AffectsRender<RestyledLeaf>(HotProperty, GlowProperty);
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
    private static (UITestHost Host, HoverReactiveLeaf[] Leaves, MouseEvent[] Sweep) CreateStorm(
        Func<HoverReactiveLeaf>? leafFactory = null, Action<UIApplication>? beforeShow = null)
    {
        var host = UITestHost.Create();
        var root = new Canvas();
        var leaves = new HoverReactiveLeaf[300];
        for (var row = 0; row < 10; row++)
        {
            for (var column = 0; column < 30; column++)
            {
                var leaf = leafFactory?.Invoke() ?? new HoverReactiveLeaf();
                leaves[row * 30 + column] = leaf;
                Canvas.SetLeft(leaf, column * 2 + 10);
                Canvas.SetTop(leaf, row * 2 + 2);
                root.Children.Add(leaf);
            }
        }

        beforeShow?.Invoke(host.Application);
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

    /// <summary>
    /// The §14 P3 row's re-assert of this gate at full styling weight: the identical storm, but
    /// every leaf is armed with a real 2-setter <c>:is(RestyledLeaf):pointerover</c> rule
    /// (background-setter shape — both properties <c>AffectsRender</c>), so every hover flip rides
    /// the engine's pseudo-class fast path (armed frame + interest-mask hit + in-place
    /// <c>SetActive</c>) through store arbitration and render invalidation. The styling engine
    /// stays in its production <c>InteractionStateObserver</c> slot (SD22) — installing a counting
    /// observer would EVICT it and silently un-style the storm. Same contracts: exactly zero
    /// steady-state bytes per <c>Move</c> including the restyles (worst repetition), frame leg
    /// ≤ 33 ms (best-of-5). The matrix's <c>Section13_Perf.S177</c> is the lean always-on twin;
    /// numbers are recorded in <c>docs/ui-layer-design.md</c> ("P3 motion-storm re-assert").
    /// </summary>
    [Fact]
    public void Probe4_MotionStormWithHoverRestyles_ZeroMoveAllocation_FrameWithinBudget()
    {
        var (host, leaves, sweep) = CreateStorm(
            static () => new RestyledLeaf(),
            static app => app.Styles.Add(CreateHoverRule()));
        using var _ = host;

        var dispatcher = host.Application.InputDispatcher;

        // Every leaf armed the rule (inactive until its first hover) — the restyle load is real.
        Assert.All(leaves, static leaf => Assert.NotEmpty(StyleDiagnostics.MatchedRules(leaf)));

        // ───────────── leg 1: the per-Move dispatch path INCLUDING restyles (exact zero) ─────────────

        var storm = () =>
                    {
                        for (var n = 0; n < SweepsPerRepetition; n++)
                        {
                            foreach (var move in sweep)
                                dispatcher.ProcessEvent(move);
                        }
                    };

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
            $"move path (with hover restyles): {movesPerRepetition:N0} Move dispatches over 300 rule-armed leaves, " +
            $"best of {Repetitions}: {bestMs:F2} ms ({bestMs * 1_000_000 / movesPerRepetition:F0} ns/move, " +
            $"{bestMs * 1000 / SweepsPerRepetition:F1} us per 200-move sweep), {worstBytes} bytes steady-state (worst rep)");

        Assert.Equal(0, worstBytes); // zero per Move INCLUDING activation/retraction through the store

        // The restyles actually flowed: the leaf under the pointer holds the rule's values NOW.
        dispatcher.ProcessEvent(sweep[10]); // column 10 — inside the leaf band
        Assert.Contains(leaves, static leaf => leaf.GetValue(RestyledLeaf.HotProperty) == 5
                                               && leaf.GetValue(RestyledLeaf.GlowProperty) == 6);
        Assert.True(leaves.Sum(static leaf => leaf.Flips) > 0, "hover-reactive flips never ran");

        // ───────────── leg 2: the frame-loop leg (storm + restyles + render in ONE frame) ─────────────

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
            $"frame loop (with hover restyles): 200-event storm + restyles + render in one frame, best of {Repetitions}: " +
            $"{bestFrameMs:F2} ms (worst {worstFrameMs:F2} ms, budget 33 ms)");

        Assert.True(
            bestFrameMs <= 33,
            $"Motion-storm (restyle) frame budget exceeded: best-of-{Repetitions} was {bestFrameMs:F2} ms (budget 33 ms).");
    }

    /// <summary>The armed rule: <c>:is(RestyledLeaf):pointerover { Hot = 5; Glow = 6 }</c> (fluent — no resolver).</summary>
    private static Style CreateHoverRule()
    {
        var style = new Style(Selectors.Is<RestyledLeaf>().PseudoClass("pointerover"));
        style.Setters.Add(new Setter(RestyledLeaf.HotProperty, 5));
        style.Setters.Add(new Setter(RestyledLeaf.GlowProperty, 6));
        return style;
    }

    // ───────────────────────────── the §14 P5 re-assert (templated controls) ─────────────────────────────

    private const int ButtonGridColumns = 20;
    private const int ButtonGridRows = 8; // 160 templated buttons (each a Border/ContentPresenter subtree)

    /// <summary>
    /// The §14 P5 row's re-assert of the motion-storm gate at full <em>templated-control</em> weight:
    /// the identical 200-position sweep, but the hovered tree is 160 real <see cref="Button"/>s — each
    /// a templated <see cref="Control"/> (a <see cref="ContentPresenter"/> over presented content),
    /// natively <c>:pointerover</c>-aware
    /// (S3 hover → <see cref="InteractionState"/>) — armed with a real <c>Button:pointerover</c>
    /// background-setter rule. Every hover flip therefore drives the full P5 hot path: the hit-test
    /// descends THROUGH each button's template subtree, the <c>:pointerover</c> pseudo-class flips, the
    /// styling engine reconciles the armed rule on the templated control, and the <c>AffectsRender</c>
    /// setter re-rasters the button's zone. The styling engine stays in its production
    /// <c>InteractionStateObserver</c> slot (SD22). Contracts (the §14 P5 exit row): exactly zero
    /// steady-state bytes per <c>Move</c> including the templated restyles (worst repetition), and the
    /// frame leg ≤ 33 ms (best-of-5). Numbers recorded in <c>docs/ui-layer-design.md</c>
    /// ("P5 motion-storm re-assert").
    /// </summary>
    [Fact]
    public void Probe4_MotionStormOverTemplatedButtons_ZeroMoveAllocation_FrameWithinBudget()
    {
        // A wide host so 300 default-themed buttons (each ~4×3 cells with the border) fit in a grid and
        // the top button band lines up under the sweep row.
        var host = UITestHost.Create(new UITestHostOptions { Capabilities = TestCapabilities.KittyTruecolor, InitialSize = new Size(160, 40) });
        using var _ = host;

        // 300 contiguous 2×2 buttons (30 × 10) on the canvas — the sweep over the top band crosses a
        // button boundary every other cell, mirroring the probe-1 leaf layout but with real templates.
        var root = new Canvas();
        var buttons = new Button[ButtonGridColumns * ButtonGridRows];
        for (var row = 0; row < ButtonGridRows; row++)
        {
            for (var column = 0; column < ButtonGridColumns; column++)
            {
                var button = new Button { Content = "xx", Template = CompactButtonTemplate(), Width = 2, Height = 2 };
                buttons[row * ButtonGridColumns + column] = button;
                Canvas.SetLeft(button, column * 2);
                Canvas.SetTop(button, row * 2);
                root.Children.Add(button);
            }
        }

        // The armed restyle: a Button:pointerover background-setter rule (AffectsRender weight).
        host.Application.Styles.Add(CreateButtonHoverRule());

        host.ShowRoot(root);
        host.RunFrame(); // settle layout + template expansion + render — HitTest valid

        // Every button armed the rule (inactive until its first hover) — the restyle load is real.
        Assert.All(buttons, static b => Assert.NotEmpty(StyleDiagnostics.MatchedRules(b)));

        var sweep = new MouseEvent[SweepLength];
        for (var i = 0; i < sweep.Length; i++)
        {
            sweep[i] = new MouseEvent
            {
                Kind = MouseEventKind.Move,
                Position = new CellPosition(i % (ButtonGridColumns * 2), 0), // the top button band (rows 0–1)
                Button = MouseButton.None,
                ButtonsHeld = MouseButtons.None,
                Modifiers = KeyModifiers.None,
                Timestamp = DateTimeOffset.UnixEpoch,
            };
        }

        var dispatcher = host.Application.InputDispatcher;

        // ───────────── leg 1: the per-Move dispatch path INCLUDING the templated restyles (exact zero) ─────────────

        var storm = () =>
                    {
                        for (var n = 0; n < SweepsPerRepetition; n++)
                        {
                            foreach (var move in sweep)
                                dispatcher.ProcessEvent(move);
                        }
                    };

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
            $"move path (templated buttons): {movesPerRepetition:N0} Move dispatches over {buttons.Length} rule-armed Buttons, " +
            $"best of {Repetitions}: {bestMs:F2} ms ({bestMs * 1_000_000 / movesPerRepetition:F0} ns/move, " +
            $"{bestMs * 1000 / SweepsPerRepetition:F1} us per 200-move sweep), {worstBytes} bytes steady-state (worst rep)");

        Assert.Equal(0, worstBytes); // zero per Move INCLUDING the templated restyle through the store

        // The restyles actually flowed: a button under the pointer is :pointerover NOW.
        dispatcher.ProcessEvent(sweep[1]); // column 1 — inside the first button (cols 0–1)
        Assert.Contains(buttons, static b => b.IsPointerOver);

        // ───────────── leg 2: the frame-loop leg (storm + templated restyles + render in ONE frame) ─────────────

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
            $"frame loop (templated buttons): 200-event storm + restyles + render in one frame, best of {Repetitions}: " +
            $"{bestFrameMs:F2} ms (worst {worstFrameMs:F2} ms, budget 33 ms)");

        Assert.True(
            bestFrameMs <= 33,
            $"Motion-storm (templated button) frame budget exceeded: best-of-{Repetitions} was {bestFrameMs:F2} ms (budget 33 ms).");
    }

    /// <summary>A compact 1×1 button template (a bare ContentPresenter — no border) so 300 fit the grid.</summary>
    private static ControlTemplate CompactButtonTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter();
        ctx.RegisterName("PART_ContentPresenter", presenter);
        return presenter;
    });

    /// <summary>The armed rule: <c>Button:pointerover { Background = … }</c> (AffectsRender, the restyle weight).</summary>
    private static Style CreateButtonHoverRule()
    {
        var style = new Style(Selectors.OfType<Button>().PseudoClass("pointerover"));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new Cursorial.Drawing.Media.SolidColorBrush(Cursorial.Output.Color.FromRgb(80, 120, 200))));
        return style;
    }
}
