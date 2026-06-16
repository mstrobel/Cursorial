using Cursorial.Input;
using Cursorial.UI;
using Cursorial.UI.Input;

namespace Cursorial.Tests.UI.InputMatrix;

// ReSharper disable InconsistentNaming

/// <summary>Input matrix §7 — mouse capture (N85–N94). Same tree as §5.</summary>
public class Section07_MouseCapture
{
    private static bool Over(UIElement element)
        => (element.InteractionStateInternal & InteractionState.PointerOver) != 0;

    [Fact]
    public void N085_Capture_RoutesAllMouseEventsToHolder_PositionsTranslated()
    {
        using var fixture = MouseHost.Create();
        CellPosition? downAtA = null;

        // ReSharper disable AccessToDisposedClosure
        fixture.A.AddHandler(UIElement.MouseDownEvent, (_, e) => downAtA = e.GetPosition(fixture.A));
        // ReSharper restore AccessToDisposedClosure

        Assert.True(fixture.A.CaptureMouse());
        Assert.Same(fixture.A, fixture.Dispatcher.MouseCaptureTarget);
        fixture.Log.Clear();

        fixture.Move(31, 6); // over B — still routes to A (tunnel root → A as usual)
        Assert.Contains("Root.OnPreviewMouseMove", fixture.Log);
        Assert.Contains("A.OnMouseMove", fixture.Log);
        Assert.DoesNotContain("B.OnMouseMove", fixture.Log);

        fixture.Down(0, 0);
        Assert.Equal((-10, -5), (downAtA!.Value.Column, downAtA.Value.Row)); // negative is legal

        fixture.Log.Clear();
        fixture.Up(0, 0);
        Assert.Contains("A.OnMouseUp", fixture.Log);
    }

    [Fact]
    public void N086_DetachedOrCollapsed_CaptureRefused()
    {
        using var fixture = MouseHost.Create();

        var detached = new Probe("Z", fixture.Log);
        Assert.False(detached.CaptureMouse());

        fixture.B.Visibility = Visibility.Collapsed;
        Assert.False(fixture.B.CaptureMouse());

        Assert.Null(fixture.Dispatcher.MouseCaptureTarget);
    }

    [Fact]
    public void N087_Release_DirectLostMouseCaptureOnce_NextEventRoutesByHit()
    {
        using var fixture = MouseHost.Create();
        Assert.True(fixture.A.CaptureMouse());
        fixture.Log.Clear();

        fixture.A.ReleaseMouseCapture();

        Assert.Equal(1, fixture.Log.Count(static entry => entry == "A.OnLostMouseCapture"));
        Assert.DoesNotContain("Root.OnLostMouseCapture", fixture.Log); // Direct, no bubbling
        Assert.Null(fixture.Dispatcher.MouseCaptureTarget);

        fixture.Log.Clear();
        fixture.Down(13, 7);
        Assert.Contains("D.OnMouseDown", fixture.Log); // by hit again
    }

    [Fact]
    public void N088_NonHolderRelease_NoOp()
    {
        using var fixture = MouseHost.Create();
        Assert.True(fixture.A.CaptureMouse());
        fixture.Log.Clear();

        fixture.B.ReleaseMouseCapture();

        Assert.Same(fixture.A, fixture.Dispatcher.MouseCaptureTarget); // A still captured
        Assert.Empty(fixture.Log);
    }

    [Fact]
    public void N089_CaptureTransfer_OldHolderNotified()
    {
        using var fixture = MouseHost.Create();
        Assert.True(fixture.A.CaptureMouse());
        fixture.Log.Clear();

        Assert.True(fixture.B.CaptureMouse());

        Assert.Equal(["A.OnLostMouseCapture"], fixture.Log);
        Assert.Same(fixture.B, fixture.Dispatcher.MouseCaptureTarget);
    }

    [Fact]
    public void N090_HolderDetach_ForceReleases()
    {
        using var fixture = MouseHost.Create();
        Assert.True(fixture.A.CaptureMouse());
        fixture.Log.Clear();

        fixture.Root.RemoveChild(fixture.A);

        Assert.Contains("A.OnLostMouseCapture", fixture.Log);
        Assert.Null(fixture.Dispatcher.MouseCaptureTarget);
    }

    [Fact]
    public void N091_HitTestingRunsUnderCapture_HoverTracksHitNotHolder()
    {
        using var fixture = MouseHost.Create();
        Assert.True(fixture.A.CaptureMouse());
        fixture.Log.Clear();

        fixture.Move(31, 6); // pointer over B

        Assert.Contains("B.OnMouseEnter", fixture.Log); // :pointerover stays honest
        Assert.True(Over(fixture.B));
        Assert.False(Over(fixture.A));
        Assert.Contains("A.OnMouseMove", fixture.Log);      // events route to the holder
        Assert.DoesNotContain("B.OnMouseMove", fixture.Log);
    }

    [Fact]
    public void N092_Wheel_TargetsHitElement_EvenUnderCapture()
    {
        using var fixture = MouseHost.Create();
        Assert.True(fixture.A.CaptureMouse());
        fixture.Log.Clear();

        fixture.Wheel(31, 6, 120);

        Assert.Contains("B.OnMouseWheel", fixture.Log);
        Assert.DoesNotContain("A.OnMouseWheel", fixture.Log);
    }

    [Fact]
    public void N093_TerminalFocusOut_ForceReleasesCapture()
    {
        using var fixture = MouseHost.Create();
        Assert.True(fixture.A.CaptureMouse());
        fixture.Log.Clear();

        fixture.Dispatcher.ProcessEvent(new Cursorial.Input.Events.FocusEvent
        {
            HasFocus = false,
            Timestamp = fixture.Host.Time.GetUtcNow()
        });

        Assert.Contains("A.OnLostMouseCapture", fixture.Log);
        Assert.Null(fixture.Dispatcher.MouseCaptureTarget);
    }

    [Fact]
    public void N094_CaptureSurvivesMouseUp()
    {
        using var fixture = MouseHost.Create();
        Assert.True(fixture.A.CaptureMouse());

        fixture.Down(13, 7);
        fixture.Up(13, 7);

        Assert.Same(fixture.A, fixture.Dispatcher.MouseCaptureTarget); // release is control logic
        Assert.DoesNotContain(fixture.Log, static entry => entry.Contains("LostMouseCapture"));
    }

    [Fact] // SubTree capture: a press over a descendant routes to the HIT (D stays interactive), not the holder
    public void N095_SubTreeCapture_InsideSubtree_RoutesToHit()
    {
        using var fixture = MouseHost.Create();
        Assert.True(fixture.A.CaptureMouse(CaptureMode.SubTree));
        Assert.Same(fixture.A, fixture.Dispatcher.MouseCaptureTarget);
        Assert.Equal(CaptureMode.SubTree, fixture.Dispatcher.CaptureMode);
        fixture.Log.Clear();

        fixture.Down(13, 7); // over D, a descendant of A — routes to D (D → A → Root bubble)

        Assert.Contains("D.OnMouseDown", fixture.Log);   // the hit target, not redirected to A
        Assert.Contains("A.OnMouseDown", fixture.Log);   // A still sees it as a bubble node
    }

    [Fact] // SubTree capture: a press outside the holder's subtree redirects to the holder; hover stays honest
    public void N096_SubTreeCapture_OutsideSubtree_RedirectsToHolder_HoverHonest()
    {
        using var fixture = MouseHost.Create();
        Assert.True(fixture.A.CaptureMouse(CaptureMode.SubTree));
        fixture.Log.Clear();

        fixture.Move(31, 6); // hover over B (outside A's subtree)
        Assert.True(Over(fixture.B));                        // :pointerover still tracks the hit (§7.6)
        Assert.False(Over(fixture.A));
        Assert.Contains("A.OnMouseMove", fixture.Log);       // move redirected to the holder
        Assert.DoesNotContain("B.OnMouseMove", fixture.Log);

        fixture.Log.Clear();
        fixture.Down(31, 6); // press over B — outside A's subtree → redirects to A

        Assert.Contains("A.OnMouseDown", fixture.Log);
        Assert.DoesNotContain("B.OnMouseDown", fixture.Log); // B is not on A's route
    }

    [Fact] // re-capturing by the current holder just swaps the mode — no transfer, no LostMouseCapture
    public void N097_Recapture_BySameHolder_SwitchesModeWithoutNotify()
    {
        using var fixture = MouseHost.Create();
        Assert.True(fixture.A.CaptureMouse()); // Element by default
        Assert.Equal(CaptureMode.Element, fixture.Dispatcher.CaptureMode);
        fixture.Log.Clear();

        Assert.True(fixture.A.CaptureMouse(CaptureMode.SubTree));
        Assert.Equal(CaptureMode.SubTree, fixture.Dispatcher.CaptureMode);
        Assert.Empty(fixture.Log); // no LostMouseCapture on a same-holder mode change

        // a press over D now routes to the hit (the SubTree policy is live)
        fixture.Down(13, 7);
        Assert.Contains("D.OnMouseDown", fixture.Log);
    }

    [Fact]
    public void N207_OnSurfacesChanged_RevalidatesCapture_P2Semantics()
    {
        using var fixture = MouseHost.Create();
        Assert.True(fixture.A.CaptureMouse());
        fixture.Log.Clear();

        // Holder still attached + visible: the seam is a no-op, capture retained.
        fixture.Dispatcher.OnSurfacesChanged();
        Assert.Same(fixture.A, fixture.Dispatcher.MouseCaptureTarget);
        Assert.Empty(fixture.Log);

        // Holder invalidated: force-release + Direct LostMouseCapture at A (the P2 semantics of
        // the S4 seam — surface-stack/modal validation joins at P7, ND5); the hover refresh rides
        // the per-frame UpdateHover.
        fixture.A.Visibility = Visibility.Collapsed;
        fixture.Dispatcher.OnSurfacesChanged();

        Assert.Null(fixture.Dispatcher.MouseCaptureTarget);
        Assert.Equal(["A.OnLostMouseCapture"], fixture.Log);
    }
}
