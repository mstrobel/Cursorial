using Cursorial.Input;
using Cursorial.UI;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.InputMatrix;

/// <summary>
/// Input matrix §6 — the hover chain and <c>UpdateHover</c> (N72–N84). Same tree as §5; the
/// matrix's <c>state(e)</c> reads <see cref="UIElement"/>'s internal interaction mask.
/// </summary>
public class Section06_HoverChain
{
    private static bool Over(UIElement element)
        => (element.InteractionStateInternal & InteractionState.PointerOver) != 0;

    [Fact]
    public void N072_FirstMove_BuildsChain_EnterOutermostFirst_ThenMove()
    {
        using var fixture = MouseHost.Create();

        fixture.Move(13, 7);

        Assert.Equal(
            [
                "Root.OnMouseEnter", "A.OnMouseEnter", "D.OnMouseEnter",
                "Root.OnPreviewMouseMove", "A.OnPreviewMouseMove", "D.OnPreviewMouseMove",
                "D.OnMouseMove", "A.OnMouseMove", "Root.OnMouseMove",
            ],
            fixture.Log);
        Assert.True(Over(fixture.Root));
        Assert.True(Over(fixture.A));
        Assert.True(Over(fixture.D));
        Assert.True(fixture.D.IsPointerOver);
    }

    [Fact]
    public void N073_SameLeafMove_NoEnterLeave_EqualityGated()
    {
        using var fixture = MouseHost.Create();
        fixture.Move(13, 7);
        fixture.Log.Clear();

        fixture.Move(14, 7); // same leaf (D spans 12..15 × 6..7)

        Assert.DoesNotContain(fixture.Log, static entry => entry.Contains("MouseEnter") || entry.Contains("MouseLeave"));
        Assert.Contains("D.OnMouseMove", fixture.Log); // MouseMove still routed
        Assert.True(Over(fixture.D));
    }

    [Fact]
    public void N074_CommonAncestorPruned_LeaveDeepestFirst_ThenEnter_RootUntouched()
    {
        using var fixture = MouseHost.Create();
        fixture.Move(13, 7);
        fixture.Log.Clear();

        fixture.Move(31, 6); // into B

        Assert.Equal(
            [
                "D.OnMouseLeave", "A.OnMouseLeave", "B.OnMouseEnter",
                "Root.OnPreviewMouseMove", "B.OnPreviewMouseMove",
                "B.OnMouseMove", "Root.OnMouseMove",
            ],
            fixture.Log);
        Assert.False(Over(fixture.D));
        Assert.False(Over(fixture.A));
        Assert.True(Over(fixture.B));
        Assert.True(Over(fixture.Root)); // no Leave/Enter, no PointerOver flicker
    }

    [Fact]
    public void N075_TwoPhase_AllStateCommitsPrecedeFirstRaise()
    {
        using var fixture = MouseHost.Create();
        fixture.Move(13, 7);

        (bool BOver, bool DOver, bool AOver)? observed = null;

        // ReSharper disable AccessToDisposedClosure
        fixture.D.AddHandler(UIElement.MouseLeaveEvent, (_, _) =>
            observed = (Over(fixture.B), Over(fixture.D), Over(fixture.A))); 
        // ReSharper restore AccessToDisposedClosure

        fixture.Move(31, 6);

        // The first phase-2 raise (D's Leave) already observes the fully committed restyle.
        Assert.Equal((true, false, false), observed);
    }

    [Fact]
    public void N076_HoverChanged_OnceAfterEnterLeave_PooledSnapshots()
    {
        using var fixture = MouseHost.Create();
        fixture.Move(13, 7);

        var raises = 0;
        var removed = new List<UIElement>();
        var added = new List<UIElement>();
        HoverChainSnapshot? retained = null;
        
        // ReSharper disable AccessToDisposedClosure
        fixture.Dispatcher.HoverChanged += (removedChain, addedChain) =>
        {
            raises++;
            fixture.Log.Add("HoverChanged");
            for (var i = 0; i < removedChain.Count; i++)
                removed.Add(removedChain[i]);
            for (var i = 0; i < addedChain.Count; i++)
                added.Add(addedChain[i]);
            retained = removedChain;
        };
        // ReSharper restore AccessToDisposedClosure

        fixture.Log.Clear();

        fixture.Move(31, 6);

        Assert.Equal(1, raises);
        Assert.Equal([fixture.D, fixture.A], removed); // deepest-first
        Assert.Equal([fixture.B], added);              // outermost-first
        Assert.Equal(
            ["D.OnMouseLeave", "A.OnMouseLeave", "B.OnMouseEnter", "HoverChanged"],
            fixture.Log.Where(static entry => !entry.Contains("MouseMove")).ToList());
        Assert.True(
            fixture.Log.IndexOf("HoverChanged") < fixture.Log.IndexOf("Root.OnPreviewMouseMove"),
            "HoverChanged precedes the MouseMove route (hover phase 2 — doc §7.6).");

#if DEBUG
        Assert.Throws<InvalidOperationException>(() => retained!.Count); // retaining past the raise throws
#endif
    }

    [Fact]
    public void N077_OffViewportMove_FullChainLeaves_DeepestFirst()
    {
        using var fixture = MouseHost.Create();
        fixture.Move(13, 7);
        fixture.Log.Clear();

        var result = fixture.Move(200, 50); // ND6 miss

        Assert.Equal(["D.OnMouseLeave", "A.OnMouseLeave", "Root.OnMouseLeave"], fixture.Log);
        Assert.Equal(InputDispatchResult.DispatchedUnhandled, result);
        Assert.False(Over(fixture.Root));
        Assert.False(Over(fixture.A));
        Assert.False(Over(fixture.D));
    }

    [Fact]
    public void N078_LayoutMovesAwayUnderStationaryPointer_UpdateHoverReDiffs()
    {
        using var fixture = MouseHost.Create();
        fixture.Move(18, 5); // over A, outside D
        Assert.True(Over(fixture.A));
        fixture.Log.Clear();

        fixture.A.Width = 4; // A now spans columns 10..13 — the pointer cell is vacated
        fixture.Host.RunFrame(); // no mouse event — Phase 6's UpdateHover re-diffs after layout

        Assert.Contains("A.OnMouseLeave", fixture.Log);
        Assert.False(Over(fixture.A));
        Assert.True(Over(fixture.Root)); // the new hit (Root was already in the chain)
    }

    [Fact]
    public void N079_ScrollOffsetChange_UpdateHoverTracksCompositeSlide()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new CanvasProbe("Root", log, 80, 24);
        var presenter = new ScrollContentPresenter { Width = 20, Height = 5, CanScrollVertically = true };
        var stack = new StackPanel();
        var rows = new Probe[10];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = new Probe($"P{i}", log, 20, 1);
            stack.Children.Add(rows[i]);
        }

        presenter.Content = stack;
        root.Place(presenter, 0, 0);
        host.ShowRoot(root);
        host.RunFrame();

        host.Application.InputDispatcher.ProcessEvent(new Cursorial.Input.Events.MouseEvent
        {
            Kind = MouseEventKind.Move,
            Position = new CellPosition(5, 2),
            Button = MouseButton.None,
            ButtonsHeld = MouseButtons.None,
            Modifiers = KeyModifiers.None,
            Timestamp = host.Time.GetUtcNow(),
        });
        Assert.True(Over(rows[2])); // content row 2 under the pointer at offset 0
        log.Clear();

        presenter.ScrollOffsetRow = 2; // pure composite slide
        host.RunFrame();

        Assert.False(Over(rows[2]));
        Assert.True(Over(rows[4])); // content row 4 now under the stationary pointer
        Assert.Contains("P2.OnMouseLeave", log);
        Assert.Contains("P4.OnMouseEnter", log);
    }

    [Fact]
    public void N080_NoMouseEventEver_UpdateHoverNoOps_ZeroHitTests()
    {
        using var fixture = MouseHost.Create();

        fixture.Host.RunFrames(3);
        fixture.Host.Application.RequestRender();
        fixture.Host.RunFrame(); // a rendered frame still performs zero hover hit tests

        Assert.Null(fixture.Dispatcher.LastPointerPosition);
        Assert.Empty(fixture.Log);
        Assert.Equal(0, fixture.Root.HitTestCoreCalls);
        Assert.Equal(0, fixture.A.HitTestCoreCalls);
        Assert.Equal(0, fixture.B.HitTestCoreCalls);
        Assert.Equal(0, fixture.D.HitTestCoreCalls);
    }

    [Fact]
    public void N081_DetachMidFrame_ChainTruncated_DeferredRefreshAtUpdateHover()
    {
        using var fixture = MouseHost.Create();
        fixture.Move(13, 7);
        Assert.True(Over(fixture.D));

        // ReSharper disable AccessToDisposedClosure
        fixture.Host.Dispatcher.Post(() => fixture.A.RemoveChild(fixture.D)); // detach mid-frame (job)
        // ReSharper restore AccessToDisposedClosure

        fixture.Host.RunFrame();

        Assert.False(Over(fixture.D)); // no stale PointerOver on D
        Assert.True(Over(fixture.A));  // the re-diff against the live tree keeps the survivors
        Assert.True(Over(fixture.Root));
        Assert.DoesNotContain("D.OnMouseLeave", fixture.Log); // truncation is silent on the detached element
    }

    [Fact]
    public void N082_LeaveHandlerDetachesAddedElement_SnapshotIterationUnaffected()
    {
        using var fixture = MouseHost.Create();
        fixture.Move(13, 7);
        // ReSharper disable AccessToDisposedClosure
        fixture.A.AddHandler(UIElement.MouseLeaveEvent, (_, _) => fixture.Root.RemoveChild(fixture.B));
        // ReSharper restore AccessToDisposedClosure
        fixture.Log.Clear();

        fixture.Move(31, 6); // B is the added suffix; A's Leave detaches it mid-phase-2

        var crossings = fixture.Log.Where(static entry => entry.Contains("MouseLeave") || entry.Contains("MouseEnter")).ToList();
        Assert.Equal(["D.OnMouseLeave", "A.OnMouseLeave", "B.OnMouseEnter"], crossings); // from the snapshot
        Assert.False(Over(fixture.B)); // detach truncation already cleared the bit

        fixture.Host.RunFrame(); // the deferred refresh re-diffs against the live tree
        Assert.True(Over(fixture.Root));
        Assert.False(Over(fixture.B));
    }

    [Fact]
    public void N083_Drag_UpdatesHoverLikeMove_ThenRoutesMouseMove()
    {
        using var fixture = MouseHost.Create();
        fixture.Move(13, 7);
        fixture.Log.Clear();

        fixture.Drag(31, 6, MouseButtons.Left);

        Assert.Equal(
            [
                "D.OnMouseLeave", "A.OnMouseLeave", "B.OnMouseEnter",
                "Root.OnPreviewMouseMove", "B.OnPreviewMouseMove",
                "B.OnMouseMove", "Root.OnMouseMove",
            ],
            fixture.Log);
        Assert.True(Over(fixture.B));
    }

    [Fact]
    public void N084_DisabledElement_NeverInHoverChain_NoEnterRaised()
    {
        using var fixture = MouseHost.Create();
        fixture.A.IsEnabled = false;

        fixture.Move(13, 7); // over A's (and D's) cells

        Assert.False(Over(fixture.A)); // ND7: PointerOver on enabled ancestors only
        Assert.False(Over(fixture.D));
        Assert.True(Over(fixture.Root));
        Assert.DoesNotContain("A.OnMouseEnter", fixture.Log);
        Assert.DoesNotContain("D.OnMouseEnter", fixture.Log);
        Assert.Contains("Root.OnMouseEnter", fixture.Log);
    }

    [Fact]
    public void N205_RenegotiationLosesMotion_LiveChainClears_DriverForgotten()
    {
        using var fixture = MouseHost.Create();
        fixture.Move(13, 7); // chain Root → A → D under the motion-capable snapshot
        fixture.Log.Clear();

        var hoverChanges = 0;
        fixture.Dispatcher.HoverChanged += (_, _) => hoverChanges++;

        fixture.Dispatcher.OnCapabilitiesChanged(TestCapabilities.NoMotion);

        // The ordinary diff: MouseLeave deepest-first, PointerOver off everywhere, one HoverChanged.
        Assert.Equal(["D.OnMouseLeave", "A.OnMouseLeave", "Root.OnMouseLeave"], fixture.Log);
        Assert.Equal(1, hoverChanges);
        Assert.False(Over(fixture.Root));
        Assert.False(Over(fixture.A));
        Assert.False(Over(fixture.D));

        // The driver is forgotten: even a motion-capable RE-renegotiation re-hovers nothing until
        // fresh motion arrives (the stale position must not silently rebuild the chain).
        fixture.Log.Clear();
        fixture.Dispatcher.OnCapabilitiesChanged(TestCapabilities.KittyTruecolor);
        fixture.Dispatcher.UpdateHover();
        fixture.Host.RunFrame();
        Assert.DoesNotContain(fixture.Log, static entry => entry.Contains("MouseEnter"));
        Assert.False(Over(fixture.D));

        fixture.Move(13, 7); // fresh motion under the restored snapshot re-hovers
        Assert.True(Over(fixture.D));
    }

#if DEBUG
    [Fact]
    public void N206_ReentrantUpdateHoverFromEnterHandler_DebugThrow()
    {
        using var fixture = MouseHost.Create();

        // ReSharper disable AccessToDisposedClosure
        fixture.B.AddHandler(UIElement.MouseEnterEvent, (_, _) => fixture.Dispatcher.UpdateHover());
        // ReSharper restore AccessToDisposedClosure

        // Phase 2 is iterating the pooled snapshots — re-running the diff is a programming error
        // (mirrors N16); the guard is compiled out of release builds.
        Assert.Throws<InvalidOperationException>(() => fixture.Move(31, 6));
    }
#endif
}
