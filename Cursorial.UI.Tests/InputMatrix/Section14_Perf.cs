using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.InputMatrix;

// ReSharper disable InconsistentNaming

/// <summary>
/// Input matrix §14 — performance and allocation contracts (N196–N201), per the ND25 methodology:
/// allocation deltas via <see cref="GC.GetAllocatedBytesForCurrentThread"/> after warming the
/// exact measured delegate; timings best-of-5 in-process after tiered-JIT settle. N200 is the
/// probe-4 motion-storm CI gate (<c>[Trait("Category","Benchmark")]</c> — allocation asserted on
/// every run, timing budget-asserted).
/// </summary>
[SuppressMessage("ReSharper", "UnusedTupleComponentInReturnValue")]
public class Section14_Perf
{
    /// <summary>A non-logging rigid leaf — log-string allocation must not pollute measurements.</summary>
    private sealed class RigidElement(int columns, int rows) : UIElement
    {
        public void AddChild(UIElement child) => AddVisualChild(child);

        protected override Size MeasureOverride(Size availableSize) => new(columns, rows);
    }

    private static (UITestHost Host, RigidElement A, RigidElement B) CreateRigidTree()
    {
        var host = UITestHost.Create();
        var root = new Canvas();
        var a = new RigidElement(10, 4);
        var b = new RigidElement(10, 4);
        Canvas.SetLeft(a, 10);
        Canvas.SetTop(a, 5);
        Canvas.SetLeft(b, 30);
        Canvas.SetTop(b, 5);
        root.Children.Add(a);
        root.Children.Add(b);
        host.ShowRoot(root);
        host.RunFrame(); // settle layout + render — RenderTree.HitTest valid
        return (host, a, b);
    }

    private static MouseEvent Move(int column, int row)
        => new()
        {
            Kind = MouseEventKind.Move,
            Position = new CellPosition(column, row),
            Button = MouseButton.None,
            ButtonsHeld = MouseButtons.None,
            Modifiers = KeyModifiers.None,
            Timestamp = DateTimeOffset.UnixEpoch,
        };

    private static KeyEvent CharKey(string text)
        => new()
        {
            Key = Key.Character,
            Modifiers = KeyModifiers.None,
            Kind = KeyEventKind.Down,
            Text = text.AsMemory(),
            Timestamp = DateTimeOffset.UnixEpoch,
        };

    [Fact]
    public void N196_SameChainMove_ZeroBytes_OneHitTestDescent()
    {
        // Leg 1 — allocation: the any-event-motion steady state is 0 B per ProcessEvent.
        var (host, _, _) = CreateRigidTree();
        using (host)
        {
            var dispatcher = host.Application.InputDispatcher;
            var first = Move(12, 6);
            var second = Move(13, 6); // same leaf — unchanged hover chain

            for (var i = 0; i < 256; i++)
            {
                dispatcher.ProcessEvent(first);
                dispatcher.ProcessEvent(second);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1_000; i++)
            {
                dispatcher.ProcessEvent(first);
                dispatcher.ProcessEvent(second);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // Leg 2 — no hit-test re-derivation: one ProcessEvent performs exactly the HitTestCore
        // work one RenderTree.HitTest call produces (S3 re-derives nothing — doc §13.2).
        using var probes = MouseHost.Create();
        probes.Move(13, 7); // establish the chain so the measured move is steady-state

        ResetHitTestCounts(probes);
        probes.Tree.HitTest(13, 7);
        var oracle = SnapshotHitTestCounts(probes);

        ResetHitTestCounts(probes);
        probes.Move(13, 7);
        var actual = SnapshotHitTestCounts(probes);

        Assert.Equal(oracle, actual);

        static void ResetHitTestCounts(MouseHost fixture)
        {
            fixture.Root.HitTestCoreCalls = 0;
            fixture.A.HitTestCoreCalls = 0;
            fixture.B.HitTestCoreCalls = 0;
            fixture.D.HitTestCoreCalls = 0;
        }

        static (int, int, int, int) SnapshotHitTestCounts(MouseHost fixture)
            => (fixture.Root.HitTestCoreCalls, fixture.A.HitTestCoreCalls, fixture.B.HitTestCoreCalls, fixture.D.HitTestCoreCalls);
    }

    [Fact]
    public void N197_AlternatingLeafMove_EnterLeaveBatchAndHoverChanged_ZeroBytes()
    {
        var (host, _, _) = CreateRigidTree();
        using var _host = host;
        var dispatcher = host.Application.InputDispatcher;

        // The full N197 shape: enter/leave + one interaction batch + a HoverChanged subscriber +
        // an installed observer, all allocation-free per move.
        var hoverChanges = 0;
        dispatcher.HoverChanged += (removed, added) => hoverChanges += removed.Count + added.Count;
        var observer = new CountingObserver();
        host.Application.InteractionStateObserver = observer;

        var overA = Move(12, 6);
        var overB = Move(32, 6);

        for (var i = 0; i < 256; i++)
        {
            dispatcher.ProcessEvent(overA);
            dispatcher.ProcessEvent(overB);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            dispatcher.ProcessEvent(overA);
            dispatcher.ProcessEvent(overB);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(hoverChanges > 0);
        Assert.True(observer.Notifications > 0);
    }

    [Fact]
    public void N198_KeyDispatch_TenDeepRoute_WithTextInputSynthesis_ZeroBytes()
    {
        using var host = UITestHost.Create();
        var root = new RigidElement(80, 24);
        var node = root;
        for (var i = 0; i < 9; i++)
        {
            var child = new RigidElement(10, 4);
            node.AddChild(child);
            node = child;
        }

        node.Focusable = true;
        host.ShowRoot(root);
        host.RunFrame();
        Assert.True(node.Focus());

        // One subscribed handler per node (pre-registered; invocation must not allocate).
        var invocations = 0;
        EventHandler<Cursorial.UI.Input.KeyEventArgs> handler = (_, _) => invocations++;
        for (var walker = (UIElement?)node; walker is not null; walker = walker.VisualParent)
            walker.AddHandler(UIElement.KeyDownEvent, handler);

        var dispatcher = host.Application.InputDispatcher;
        var key = CharKey("x"); // unhandled ⇒ the full tail incl. TextInput synthesis runs

        for (var i = 0; i < 256; i++)
            dispatcher.ProcessEvent(key);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
            dispatcher.ProcessEvent(key);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(invocations >= 1_256 * 10);
    }

    [Fact]
    public void N199_StationaryPointer_UpdateHoverReDiff_ZeroBytes()
    {
        var (host, _, _) = CreateRigidTree();
        using var _host = host;
        var dispatcher = host.Application.InputDispatcher;

        dispatcher.ProcessEvent(Move(12, 6)); // the hover driver — UpdateHover re-diffs against it

        for (var i = 0; i < 256; i++)
            dispatcher.UpdateHover();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
            dispatcher.UpdateHover();

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void N200_Probe4_MotionStorm_CIGate()
    {
        // The probe-1-shaped dashboard tree: ~300 rigid leaves on an 80×24 canvas.
        using var host = UITestHost.Create();
        var root = new Canvas();
        for (var row = 0; row < 10; row++)
        {
            for (var column = 0; column < 30; column++)
            {
                var leaf = new RigidElement(2, 2);
                Canvas.SetLeft(leaf, column * 2 + 10);
                Canvas.SetTop(leaf, row * 2 + 2);
                root.Children.Add(leaf);
            }
        }

        host.ShowRoot(root);
        host.RunFrame();

        var dispatcher = host.Application.InputDispatcher;
        var sweep = new MouseEvent[200];
        for (var i = 0; i < sweep.Length; i++)
            sweep[i] = Move(i % 80, 3); // a row crossing leaf boundaries every other cell

        // Allocation contract (asserted every suite run): the per-Move path is allocation-free.
        for (var i = 0; i < 5; i++)
        {
            foreach (var move in sweep)
                dispatcher.ProcessEvent(move);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var move in sweep)
            dispatcher.ProcessEvent(move);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        // Timing (informational + budget-asserted): 200 Move events drained in ONE frame,
        // best-of-5 after warm-up, budget ≤ 33 ms/frame (one 30 fps frame period).
        var best = TimeSpan.MaxValue;
        for (var rep = 0; rep < 5; rep++)
        {
            foreach (var move in sweep)
                host.SendInput(move);

            var stopwatch = Stopwatch.StartNew();
            host.RunFrame();
            stopwatch.Stop();
            if (stopwatch.Elapsed < best)
                best = stopwatch.Elapsed;
        }

        Assert.True(
            best <= TimeSpan.FromMilliseconds(33),
            $"Motion-storm frame budget exceeded: best-of-5 was {best.TotalMilliseconds:F2} ms (budget 33 ms).");
    }

    [Fact]
    public void N201_OneTimeCosts_BoundedNotZero_SecondDispatchAllocatesNothing()
    {
        using var host = UITestHost.Create();
        var root = new RigidElement(80, 24);
        var leaf = new RigidElement(10, 4) { Focusable = true };
        root.AddChild(leaf);
        host.ShowRoot(root);
        host.RunFrame();
        Assert.True(leaf.Focus());

        var handled = 0;
        leaf.AddHandler(UIElement.KeyDownEvent, (_, _) => handled++);

        var dispatcher = host.Application.InputDispatcher;
        var key = CharKey("x");

        var before = GC.GetAllocatedBytesForCurrentThread();
        dispatcher.ProcessEvent(key);
        var firstDispatch = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        dispatcher.ProcessEvent(key);
        var secondDispatch = GC.GetAllocatedBytesForCurrentThread() - before;

        // The pin is "bounded, not zero": the first dispatch MAY pay one-time pool/scratch/store
        // costs (pooled args, route scratch, handler-store arrays) up to a small bound — it can
        // legitimately be 0 in-suite when the thread-static free-lists are already warm from an
        // earlier test on this worker thread. The second identical dispatch allocates exactly 0
        // (the warm-up contract N196–N199 rely on).
        Assert.InRange(firstDispatch, 0, 64 * 1024);
        Assert.Equal(0, secondDispatch);
        Assert.Equal(2, handled);
    }

    private sealed class CountingObserver : IInteractionStateObserver
    {
        public int Notifications;

        public void OnInteractionStateChanged(UIElement element, InteractionState oldState, InteractionState newState)
            => Notifications++;
    }
}
