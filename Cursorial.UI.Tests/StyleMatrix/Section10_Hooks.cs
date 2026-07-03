using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Testing;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>Style matrix §10 — frame-loop hooks (S147–S152; ledger B1, SD12).</summary>
public class Section10_Hooks
{
    /// <summary>A pass-through <see cref="IStyleFrameHooks"/> spy counting the loop's flush calls (S151).</summary>
    private sealed class SpyHooks(IStyleFrameHooks inner) : IStyleFrameHooks
    {
        public int FlushCalls;

        public void FlushPendingActivations()
        {
            FlushCalls++;
            inner.FlushPendingActivations();
        }

        public bool HasPendingActivations => inner.HasPendingActivations;

        public void OnCapabilitiesChanged(TerminalCapabilities capabilities) => inner.OnCapabilitiesChanged(capabilities);
    }

    [Fact]
    public void S147_TheEngineOccupiesThePhase3Slot()
    {
        using var tree = ShowTree();

        Assert.NotNull(tree.App.StyleHooks);
        Assert.Same(tree.App.StyleEngineInternal, tree.App.StyleHooks); // the engine IS the hooks implementation (B1)
    }

    [Fact]
    public void S148_IdleEngine_NoPendingActivations()
    {
        using var tree = ShowTree();
        Assert.True(tree.Host.RunUntilIdle());

        Assert.False(tree.App.StyleHooks!.HasPendingActivations); // O(1) — one list-count read
    }

    [Fact]
    public void S149_LayoutRaisedFlip_Queues_SurfacesViaHasPendingActivations_RunUntilIdleConverges()
    {
        var mapped = UIProperty.Register<Widget, bool>(UniqueName("PbS149"));
        PseudoClassMapping.Register<Widget>(mapped, ":s149-measured");

        using var tree = ShowTree(show: false);
        tree.App.Styles.Add(R("Widget:s149-measured", (Widget.P, 5)));

        var fired = false;
        tree.A.OnMeasuring = widget =>
        {
            if (fired)
                return;

            fired = true;
            widget.SetValue(mapped, true); // the mapping flips :s149-measured DURING Phase 5 (SD12 deferred window)
        };

        tree.Host.ShowRoot(tree.Root);
        tree.Host.RunFrame();

        Assert.True(fired);
        Assert.True(tree.App.StyleHooks!.HasPendingActivations); // queued, not applied mid-layout
        Assert.Equal(0, tree.A.GetValue(Widget.P));
        Assert.False(tree.App.IsIdle); // the loop schedules another frame (the Phase-7 guard input)

        Assert.True(tree.Host.RunUntilIdle());
        Assert.Equal(5, tree.A.GetValue(Widget.P)); // the rule applied on the follow-up frame's Phase 3
        Assert.False(tree.App.StyleHooks.HasPendingActivations);
    }

    [Fact]
    public void S150_InputDrivenFlip_AppliedWithinTheSameFrame_VisibleToLayoutAndRender()
    {
        using var host = UITestHost.Create();
        var root = new Widget(80, 24) { PaintP = true };
        host.Application.Styles.Add(R("Widget:pointerover", (Widget.P, 9)));
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());
        Assert.Equal("0", host.GetCell(5, 5).Grapheme);

        host.SendMouseMove(5, 5);
        host.RunFrame(); // ONE frame: the Phase-1 hover flip reaches fixpoint before layout/render (invariant 1)

        Assert.Equal(9, root.GetValue(Widget.P));
        Assert.Equal("9", host.GetCell(5, 5).Grapheme); // the styled value rendered by the SAME frame
    }

    [Fact]
    public void S151_LoopFlushes_AtPhase3_AndAfterTheAnimationTick_EveryFrame()
    {
        using var tree = ShowTree();
        var spy = new SpyHooks(tree.App.StyleEngineInternal);
        tree.App.StyleHooks = spy; // the seam stays assignable — probe implementations install here

        tree.Host.RunFrame();
        Assert.Equal(2, spy.FlushCalls); // Phase 3 + the post-animation-tick flush (no-op driver at P3)

        tree.Host.RunFrame();
        Assert.Equal(4, spy.FlushCalls); // every frame
    }

    [Fact]
    public void S152_IdleGuard_NotIdleWhilePending_IdleAfterTheFlush()
    {
        var mapped = UIProperty.Register<Widget, bool>(UniqueName("PbS152"));
        PseudoClassMapping.Register<Widget>(mapped, ":s152-quiet");

        using var tree = ShowTree();
        tree.App.Styles.Add(R("Widget:s152-quiet", (Widget.Pd, 1.5))); // Pd carries no effects — no other work
        Assert.True(tree.Host.RunUntilIdle());

        var fired = false;
        tree.A.OnMeasuring = widget =>
        {
            if (fired)
                return;

            fired = true;
            widget.SetValue(mapped, true);
        };
        tree.A.InvalidateMeasure();
        tree.Host.RunFrame(); // the flip queues during Phase 5; everything else settles this frame

        Assert.True(tree.App.StyleHooks!.HasPendingActivations);
        Assert.False(tree.App.IsIdle); // the idle guard holds while a flush is pending

        tree.App.StyleHooks.FlushPendingActivations(); // callable directly
        Assert.False(tree.App.StyleHooks.HasPendingActivations);
        Assert.Equal(1.5, tree.A.GetValue(Widget.Pd));
        Assert.True(tree.App.IsIdle); // idle returns after the flush
    }
}
