using System.Diagnostics;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

using Xunit.Abstractions;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>
/// Style matrix §13 — performance and allocation (S173–S180). Methodology per the repo norm:
/// <c>GC.GetAllocatedBytesForCurrentThread()</c> deltas after warm-up, single-threaded. S177 is the
/// probe-4 CI gate re-assert mandated by the §14 P3 row, measured at full pipeline weight with a
/// real <c>:pointerover</c> rule set armed on every leaf.
/// </summary>
public class Section13_Perf(ITestOutputHelper output)
{
    [Fact]
    public void S173_WarmedPseudoFlip_FullActivateRetract_ZeroBytesPerFlip()
    {
        using var tree = ShowTree();
        tree.App.Styles.Add(R("Widget:pointerover", (Widget.P, 5), (Widget.Q, 6))); // one armed 2-setter rule
        Assert.True(tree.Host.RunUntilIdle());

        var cycle = () =>
        {
            tree.A.Flip(InteractionState.PointerOver, true);
            tree.A.Flip(InteractionState.PointerOver, false);
        };

        for (var i = 0; i < 64; i++)
            cycle(); // warm: first-touch store entries, queue capacity, tiering

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++)
            cycle();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before); // the §3.9-④ contract

        // The measured path really toggled the full activate/retract through the store.
        tree.A.Flip(InteractionState.PointerOver, true);
        Assert.Equal(5, tree.A.GetValue(Widget.P));
        Assert.Equal(6, tree.A.GetValue(Widget.Q));
        tree.A.Flip(InteractionState.PointerOver, false);
        Assert.Equal(0, tree.A.GetValue(Widget.P));
    }

    [Fact]
    public void S174_InterestMaskMiss_OnAnElementWithStyleState_ZeroBytes()
    {
        using var tree = ShowTree();
        tree.App.Styles.Add(R("Widget:pointerover", (Widget.P, 5), (Widget.Q, 6)));
        Assert.True(tree.Host.RunUntilIdle());
        Assert.NotEmpty(StyleDiagnostics.MatchedRules(tree.A)); // the element HAS style state

        var cycle = () =>
        {
            tree.A.Flip(InteractionState.ModalAttention, true); // a bit no armed rule wants
            tree.A.Flip(InteractionState.ModalAttention, false);
        };

        for (var i = 0; i < 64; i++)
            cycle();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++)
            cycle();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before); // the one-AND early-out

        Assert.Equal(0, tree.A.GetValue(Widget.P)); // nothing activated
    }

    [Fact]
    public void S175_FlipOnAnElementWithNoStyleState_ZeroBytes()
    {
        using var tree = ShowTree(); // no styles anywhere
        Assert.True(tree.Host.RunUntilIdle());
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.A));

        var cycle = () =>
        {
            tree.A.Flip(InteractionState.PointerOver, true);
            tree.A.Flip(InteractionState.PointerOver, false);
        };

        for (var i = 0; i < 64; i++)
            cycle();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++)
            cycle();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before); // O(1) — the null-state check
    }

    [Fact]
    public void S176_PseudoFlipRestyle_ReRastersOnlyTheAffectedZone()
    {
        using var host = UITestHost.Create();
        var root = new StackPanel();
        var zoneA = new Widget(10, 2) { PaintP = true, IsRenderBoundary = true };
        var zoneB = new Widget(10, 2) { PaintP = true, IsRenderBoundary = true };
        root.Children.Add(zoneA);
        root.Children.Add(zoneB);
        host.Application.Styles.Add(R("Widget:pointerover", (Widget.P, 5))); // P is AffectsRender
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        var tree = host.Application.WindowManager!.Tree!;
        var versionA = tree.GetScene(zoneA)!.RasterVersion;
        var versionB = tree.GetScene(zoneB)!.RasterVersion;

        zoneA.Flip(InteractionState.PointerOver, true);
        host.RunFrame();

        Assert.Equal(versionA + 1, tree.GetScene(zoneA)!.RasterVersion); // zone A re-rastered…
        Assert.Equal(versionB, tree.GetScene(zoneB)!.RasterVersion);     // …zone B untouched (invariants 2/3 routing)
        Assert.Equal("5", host.GetCell(0, 0).Grapheme);
        Assert.Equal("0", host.GetCell(0, 2).Grapheme);
    }

    // ───────────────────────────── S177 — the probe-4 motion-storm re-assert ─────────────────────────────

    /// <summary>The P2 storm leaf (2×2, hover-reactive through the sanctioned protected setter) — the §14 P3 re-assert tree.</summary>
    public sealed class StormLeaf : Widget
    {
        public StormLeaf()
            : base(2, 2)
        {
        }

        protected override void OnMouseEnter(MouseEventArgs e) => SetInteractionState(InteractionState.Pressed, true);

        protected override void OnMouseLeave(MouseEventArgs e) => SetInteractionState(InteractionState.Pressed, false);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void S177_MotionStormReAssert_HoverRestyles_ZeroAllocation_FrameWithinBudget()
    {
        const int sweepLength = 200;
        const int sweepsPerRepetition = 25; // 5,000 Move dispatches per measured repetition
        const int repetitions = 5;

        using var host = UITestHost.Create();
        var root = new Canvas();
        var leaves = new StormLeaf[300];
        for (var row = 0; row < 10; row++)
        {
            for (var column = 0; column < 30; column++)
            {
                var leaf = new StormLeaf();
                leaves[row * 30 + column] = leaf;
                Canvas.SetLeft(leaf, column * 2 + 10);
                Canvas.SetTop(leaf, row * 2 + 2);
                root.Children.Add(leaf);
            }
        }

        // The REAL rule set: a :pointerover rule with 2 setters armed on every leaf (and a state
        // rule for the Pressed writes the leaves make — full P2 parity plus the restyle load).
        host.Application.Styles.Add(R(":is(Widget):pointerover", (Widget.P, 5), (Widget.Q, 6)));
        host.ShowRoot(root);
        host.RunFrame(); // settle layout + render — RenderTree.HitTest valid

        Assert.All(leaves, static leaf => Assert.NotEmpty(StyleDiagnostics.MatchedRules(leaf)));

        var sweep = new MouseEvent[sweepLength];
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

        var dispatcher = host.Application.InputDispatcher;

        // ── leg 1: the per-Move dispatch path INCLUDING the restyles — exact zero, asserted every run ──
        var storm = () =>
                    {
                        for (var n = 0; n < sweepsPerRepetition; n++)
                        {
                            foreach (var move in sweep)
                                dispatcher.ProcessEvent(move);
                        }
                    };

        storm();
        storm();
        var settle = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(settle).TotalMilliseconds < 250)
        {
            // busy-spin for tiered-JIT promotion (the StoreSpikeBenchmark methodology)
        }

        storm();
        GC.Collect();

        var worstBytes = long.MinValue;
        var bestMs = double.MaxValue;
        for (var rep = 0; rep < repetitions; rep++)
        {
            var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            storm();
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
            output.WriteLine($"  move-path rep {rep}: {ms:F2} ms, {bytes} bytes");
            worstBytes = Math.Max(worstBytes, bytes);
            bestMs = Math.Min(bestMs, ms);
        }

        output.WriteLine(
            $"move path: {sweepsPerRepetition * sweepLength:N0} Move dispatches with hover restyles, " +
            $"best of {repetitions}: {bestMs:F2} ms, {worstBytes} bytes steady-state (worst rep)");
        Assert.Equal(0, worstBytes); // zero per Move INCLUDING the restyles

        // The restyles actually flowed: hover sits on a leaf with the rule active right now.
        host.Application.InputDispatcher.ProcessEvent(sweep[10]); // column 10 — inside the leaf band
        Assert.Contains(leaves, static leaf => leaf.GetValue(Widget.P) == 5);

        // ── leg 2: the frame-loop leg — the 200-event storm drained in ONE frame, ≤ 33 ms best-of-5 warm ──
        var bestFrameMs = double.MaxValue;
        var worstFrameMs = 0d;
        for (var rep = 0; rep < repetitions + 2; rep++) // 2 warm-up frames, 5 measured
        {
            foreach (var move in sweep)
                host.SendInput(move);
            host.Application.RequestRender();

            var start = Stopwatch.GetTimestamp();
            host.RunFrame();
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (rep < 2)
                continue;

            output.WriteLine($"  frame rep {rep - 2}: {ms:F2} ms");
            bestFrameMs = Math.Min(bestFrameMs, ms);
            worstFrameMs = Math.Max(worstFrameMs, ms);
        }

        output.WriteLine(
            $"frame loop: 200-event storm + restyles + render in one frame, best of {repetitions}: " +
            $"{bestFrameMs:F2} ms (worst {worstFrameMs:F2} ms, budget 33 ms)");
        Assert.True(
            bestFrameMs <= 33,
            $"Motion-storm frame budget exceeded: best-of-{repetitions} was {bestFrameMs:F2} ms (budget 33 ms).");
    }

    [Fact]
    public void S178_ColdAttach_300Elements_Under20RuleAppStyles_BoundedAndInsideTheStartupTier()
    {
        using var host = UITestHost.Create();
        var app = host.Application;
        for (var i = 0; i < 10; i++)
        {
            app.Styles.Add(R($"Widget.band{i}", (Widget.P, i))); // 10 type+class rules
            app.Styles.Add(R($".band{i}", (Widget.Q, i)));       // 10 class rules — 20 total
        }

        var root = new StackPanel();
        var leaves = new List<Widget>(300);
        for (var pane = 0; pane < 10; pane++)
        {
            var stack = new StackPanel();
            for (var i = 0; i < 30; i++)
            {
                var leaf = new Widget();
                leaf.Classes.Add($"band{(pane * 30 + i) % 10}");
                leaves.Add(leaf);
                stack.Children.Add(leaf);
            }

            root.Children.Add(stack);
        }

        var start = Stopwatch.GetTimestamp();
        host.ShowRoot(root); // the attach walk arms synchronously (B19)
        var attachMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        // One-time costs are bounded, NOT zero: every leaf armed its two matching rules with values applied.
        Assert.All(leaves, static leaf => Assert.Equal(2, StyleDiagnostics.MatchedRules(leaf).Count));
        Assert.Equal(3, leaves[3].GetValue(Widget.P));
        Assert.Equal(3, leaves[3].GetValue(Widget.Q));

        // Inside the startup tier (generous CI headroom); the measured number is recorded
        // informationally in the design doc's §14 P3 row.
        output.WriteLine($"cold attach: 300 elements under a 20-rule S_app armed in {attachMs:F1} ms");
        Assert.True(attachMs < 250, $"cold attach took {attachMs:F1} ms (startup tier 250 ms)");
    }

    [Fact]
    public void S179_ClassAddRemoveCycle_BoundedNotZero_FlipPathOfOtherElementsStaysZero()
    {
        using var tree = ShowTree();
        tree.App.Styles.Add(R("Widget.hot", (Widget.P, 5)));
        tree.App.Styles.Add(R("Widget:pointerover", (Widget.Q, 6)));
        Assert.True(tree.Host.RunUntilIdle());

        var classCycle = () =>
        {
            tree.A.Classes.Add("hot");
            tree.A.Classes.Remove("hot");
        };
        var flipCycle = () =>
        {
            tree.B.Flip(InteractionState.PointerOver, true);
            tree.B.Flip(InteractionState.PointerOver, false);
        };

        for (var i = 0; i < 32; i++)
        {
            classCycle();
            flipCycle();
        }

        // The flip path of OTHER elements stays 0 B throughout — interleaved with re-matches.
        for (var i = 0; i < 8; i++)
        {
            classCycle();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var j = 0; j < 32; j++)
                flipCycle();
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // The re-match tier is bounded, NOT zero — the frames array rebuild is honest Phase-1 work.
        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 32; i++)
            classCycle();
        var perCycle = (GC.GetAllocatedBytesForCurrentThread() - bytesBefore) / 32.0;
        output.WriteLine($"class add/remove re-match: {perCycle:F0} B per cycle");
        Assert.True(perCycle > 0, "a re-match cycle reported zero allocation — the bounded-not-zero pin is stale");
        Assert.True(perCycle < 8 * 1024, $"a re-match cycle allocated {perCycle:F0} B (bound 8 KiB)");
    }

    [Fact]
    public void S180_FlushPendingActivations_EmptyQueue_ZeroBytes_Idempotent()
    {
        using var tree = ShowTree();
        Assert.True(tree.Host.RunUntilIdle());
        var hooks = tree.App.StyleHooks!;

        hooks.FlushPendingActivations(); // callable directly…
        hooks.FlushPendingActivations(); // …and idempotent
        Assert.False(hooks.HasPendingActivations);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1024; i++)
            hooks.FlushPendingActivations();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before); // cheap when empty (B1)

        Assert.False(hooks.HasPendingActivations);
    }
}
