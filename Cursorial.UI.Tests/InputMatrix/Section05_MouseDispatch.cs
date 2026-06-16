using Cursorial.Input;
using Cursorial.UI;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.InputMatrix;

/// <summary>
/// Input matrix §5 — mouse dispatch and hit-test delegation (N58–N71). Tree per the matrix:
/// <c>Root</c> (Canvas 80×24), <c>A</c> at (10,5,10×4), <c>B</c> at (30,5,10×4), <c>D</c> child
/// of <c>A</c> at terminal (12,6,4×2).
/// </summary>
public class Section05_MouseDispatch
{
    [Fact]
    public void N058_Down_TargetIsRenderTreeHitTestAnswer_FullRoutePair()
    {
        using var fixture = MouseHost.Create();
        UIElement? source = null;
        fixture.D.AddHandler(UIElement.MouseDownEvent, (_, e) => source = e.Source);

        var oracle = fixture.Tree.HitTest(13, 7); // S3 delegates — the answers must coincide
        fixture.Log.Clear();

        fixture.Down(13, 7);

        Assert.Same(fixture.D, oracle);
        Assert.Same(oracle, source); // exactly RenderTree.HitTest's answer
        Assert.Equal(
            [
                "Root.OnPreviewMouseDown", "A.OnPreviewMouseDown", "D.OnPreviewMouseDown",
                "D.OnMouseDown", "A.OnMouseDown", "Root.OnMouseDown"
            ],
            fixture.Log);
    }

    [Fact]
    public void N059_HitTestCoreReject_NoSecondDescent_TargetFallsToA()
    {
        using var fixture = MouseHost.Create();
        fixture.D.HitTestCoreOverride = static (_, _) => false;

        fixture.D.HitTestCoreCalls = 0;
        var oracle = fixture.Tree.HitTest(13, 7);
        var oneDescentCalls = fixture.D.HitTestCoreCalls;
        Assert.Same(fixture.A, oracle);

        fixture.D.HitTestCoreCalls = 0;
        fixture.Log.Clear();
        fixture.Down(13, 7);

        Assert.Equal(oneDescentCalls, fixture.D.HitTestCoreCalls); // the dispatcher re-derives nothing
        Assert.Contains("A.OnMouseDown", fixture.Log);
        Assert.DoesNotContain("D.OnMouseDown", fixture.Log);
    }

    [Fact]
    public void N060_ZIndex_CompositeOrderWinner_ByDelegation()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new CanvasProbe("Root", log, 80, 24);
        var x = new Probe("X", log, 10, 4) { ZIndex = 1 };
        var y = new Probe("Y", log, 10, 4); // later document order, lower ZIndex
        root.Place(x, 30, 5);
        root.Place(y, 30, 5);
        host.ShowRoot(root);
        host.RunFrame();
        log.Clear();

        var tree = host.Application.WindowManager!.Tree!;
        var oracle = tree.HitTest(31, 6);
        Assert.Same(x, oracle); // ZIndex beats document order in composite order

        host.Application.InputDispatcher.ProcessEvent(new Cursorial.Input.Events.MouseEvent
        {
            Kind = MouseEventKind.ButtonDown,
            Position = new CellPosition(31, 6),
            Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.None,
            Modifiers = KeyModifiers.None,
            Timestamp = host.Time.GetUtcNow()
        });

        Assert.Contains("X.OnMouseDown", log); // the same composite-order winner, by delegation
        Assert.DoesNotContain("Y.OnMouseDown", log);
    }

    [Fact]
    public void N061_Positions_ElementLocal_NegativeLegal_ScreenTerminal()
    {
        using var fixture = MouseHost.Create();
        CellPosition? atD = null, atA = null, atB = null, screen = null;

        // ReSharper disable AccessToDisposedClosure
        fixture.D.AddHandler(UIElement.MouseDownEvent, (_, e) =>
        {
            atD = e.GetPosition(fixture.D);
            atA = e.GetPosition(fixture.A);
            atB = e.GetPosition(fixture.B);
            screen = e.ScreenPosition;
        });
        // ReSharper restore AccessToDisposedClosure

        fixture.Down(13, 7);

        Assert.Equal((1, 1), (atD!.Value.Column, atD.Value.Row));
        Assert.Equal((3, 2), (atA!.Value.Column, atA.Value.Row));
        Assert.Equal((-17, 2), (atB!.Value.Column, atB.Value.Row)); // negative is legal, no throw
        Assert.Equal((13, 7), (screen!.Value.Column, screen.Value.Row));
    }

    [Fact]
    public void N062_ClickCount_BakedIn_RidesMouseDownOnly()
    {
        using var fixture = MouseHost.Create();
        var downCount = 0;
        var upCount = 0;
        fixture.D.AddHandler(UIElement.MouseDownEvent, (_, e) => downCount = e.ClickCount);
        fixture.D.AddHandler(UIElement.MouseUpEvent, (_, e) => upCount = e.ClickCount);

        fixture.Down(13, 7, clicks: 2);
        fixture.Up(13, 7);

        Assert.Equal(2, downCount); // arrives baked in (ClickCountTarget.ButtonDown)
        Assert.Equal(1, upCount);   // the dispatcher adds no timing
    }

    [Fact]
    public void N063_Wheel_TargetsHitElement_NotFocus()
    {
        using var fixture = MouseHost.Create();
        fixture.A.Focusable = true;
        Assert.True(fixture.A.Focus());
        var delta = 0;
        fixture.B.AddHandler(UIElement.MouseWheelEvent, (_, e) => delta = e.WheelDeltaY);
        fixture.Log.Clear();

        fixture.Wheel(31, 6, 120);

        Assert.Equal(120, delta);
        Assert.Equal(
            ["Root.OnPreviewMouseWheel", "B.OnPreviewMouseWheel", "B.OnMouseWheel", "Root.OnMouseWheel"],
            fixture.Log);
    }

    [Fact]
    public void N064_Move_RoutedAtHitElement_AfterHoverPhases()
    {
        using var fixture = MouseHost.Create();

        fixture.Move(13, 7);

        Assert.Equal(
            [
                "Root.OnMouseEnter", "A.OnMouseEnter", "D.OnMouseEnter",
                "Root.OnPreviewMouseMove", "A.OnPreviewMouseMove", "D.OnPreviewMouseMove",
                "D.OnMouseMove", "A.OnMouseMove", "Root.OnMouseMove"
            ],
            fixture.Log);
    }

    [Fact]
    public void N065_DownOnEmptyRootArea_HitsSurfaceRoot()
    {
        using var fixture = MouseHost.Create();

        fixture.Down(0, 0);

        // Surfaces are hit-opaque: a point no descendant claims hits the root (doc §7.6).
        Assert.Equal(["Root.OnPreviewMouseDown", "Root.OnMouseDown"], fixture.Log);
    }

    [Fact]
    public void N066_OutOfViewport_Miss_NoRouteNoThrow_PositionStillRecorded()
    {
        using var fixture = MouseHost.Create();

        var result = fixture.Down(200, 50);

        Assert.Equal(InputDispatchResult.DispatchedUnhandled, result);
        Assert.Empty(fixture.Log); // no route, no hover chain (ND6)
        Assert.Equal(new CellPosition(200, 50), fixture.Dispatcher.LastPointerPosition);
    }

    [Fact]
    public void N067_IsHitTestVisibleFalse_LeafOnlyGate_TargetFallsToA()
    {
        using var fixture = MouseHost.Create();
        fixture.D.IsHitTestVisible = false;

        var oracle = fixture.Tree.HitTest(13, 7);
        fixture.Log.Clear();
        fixture.Down(13, 7);

        Assert.Same(fixture.A, oracle);
        Assert.Contains("A.OnMouseDown", fixture.Log);
        Assert.DoesNotContain("D.OnMouseDown", fixture.Log);
    }

    [Fact]
    public void N068_HiddenSubtree_NotHit_TargetFallsToA()
    {
        using var fixture = MouseHost.Create();
        fixture.D.Visibility = Visibility.Hidden;
        fixture.Log.Clear();

        fixture.Down(13, 7);

        Assert.Contains("A.OnMouseDown", fixture.Log);
        Assert.DoesNotContain("D.OnMouseDown", fixture.Log);
    }

    [Fact]
    public void N069_DisabledAncestor_HitOpaqueEventTransparent_TargetIsRoot()
    {
        using var fixture = MouseHost.Create();
        fixture.A.IsEnabled = false; // D inside
        fixture.Log.Clear();

        fixture.Down(13, 7);

        // ND7: nearest effectively-enabled self-or-ancestor; neither A nor D in the route.
        Assert.Equal(["Root.OnPreviewMouseDown", "Root.OnMouseDown"], fixture.Log);
    }

    [Fact]
    public void N070_ButtonsHeld_MirrorsMaskBetweenDownAndUp_UpRoutedAtHit()
    {
        using var fixture = MouseHost.Create();

        fixture.Down(13, 7);
        Assert.Equal(MouseButtons.Left, fixture.Dispatcher.ButtonsHeld);

        fixture.Log.Clear();
        fixture.Up(13, 7);

        Assert.Equal(MouseButtons.None, fixture.Dispatcher.ButtonsHeld);
        Assert.Contains("D.OnMouseUp", fixture.Log); // routed at the hit element (no capture in play)
    }

    [Fact]
    public void N071_NoShownRoot_Unhandled_NoThrow()
    {
        using var fixture = MouseHost.Create(show: false);

        var result = fixture.Down(5, 5);

        Assert.Equal(InputDispatchResult.DispatchedUnhandled, result); // ND5
        Assert.Empty(fixture.Log);
    }

    [Fact]
    public void N204_PublicHitTest_DispatchTargetExact_PureQuery()
    {
        using var fixture = MouseHost.Create();
        fixture.Move(13, 7); // establish bookkeeping the query must not disturb
        fixture.Log.Clear();

        // The public position query returns exactly the dispatch target (doc §7.4 made P2-exact).
        Assert.Same(fixture.D, fixture.Dispatcher.HitTest(new CellPosition(13, 7)));

        fixture.A.IsEnabled = false; // D inside — the ND7 disabled hop
        Assert.Same(fixture.Root, fixture.Dispatcher.HitTest(new CellPosition(13, 7)));
        fixture.A.IsEnabled = true;

        Assert.Null(fixture.Dispatcher.HitTest(new CellPosition(200, 50))); // out-of-viewport miss

        // Pure: no routing, no hover change, LastPointerPosition untouched.
        Assert.DoesNotContain(fixture.Log, static entry => entry.Contains("Mouse"));
        Assert.True(fixture.D.IsPointerOver); // the hover chain from the real Move is intact
        Assert.Equal(new CellPosition(13, 7), fixture.Dispatcher.LastPointerPosition);
    }
}
