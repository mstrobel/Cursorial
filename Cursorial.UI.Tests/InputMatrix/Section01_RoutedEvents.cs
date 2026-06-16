using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.UI;
using Cursorial.UI.Input;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.InputMatrix;

/// <summary>Input matrix §1 — routed events, handler store, route mechanics (N1–N18).</summary>
public class Section01_RoutedEvents
{
    private static KeyEvent Enter => new()
    {
        Key = Key.Enter,
        Modifiers = KeyModifiers.None,
        Kind = KeyEventKind.Down,
        Timestamp = DateTimeOffset.UnixEpoch
    };

    private static (List<string> Log, Probe Root, Probe A, Probe B) Chain()
    {
        var log = new List<string>();
        var root = new Probe("Root", log);
        var a = new Probe("A", log);
        var b = new Probe("B", log);
        root.AddChild(a);
        a.AddChild(b);
        return (log, root, a, b);
    }

    [Fact]
    public void N001_Register_DistinctIdentities_DenseIndexes_PropertiesReadBack()
    {
        var first = RoutedEvent<KeyEventArgs>.Register("N1First", RoutingStrategy.Bubble, typeof(Probe));
        var second = RoutedEvent<KeyEventArgs>.Register("N1Second", RoutingStrategy.Bubble, typeof(Probe));

        Assert.NotSame(first, second);
        Assert.Equal(first.GlobalIndex + 1, second.GlobalIndex); // dense (suite is serialized)
        Assert.Equal("N1First", first.Name);
        Assert.Equal(RoutingStrategy.Bubble, first.Strategy);
        Assert.Same(typeof(Probe), first.OwnerType);
        Assert.Same(typeof(KeyEventArgs), first.ArgsType);
    }

    [Fact]
    public void N002_HandlerStore_LazyAllocated_AddRemoveReturnsToFunctionalEmpty()
    {
        var probe = new Probe("P", []);
        Assert.Null(probe.EventHandlerStoreForDebug); // never allocated without a handler

        EventHandler<KeyEventArgs> handler = static (_, _) => { };
        probe.AddHandler(UIElement.KeyDownEvent, handler);
        Assert.NotNull(probe.EventHandlerStoreForDebug);
        Assert.Equal(1, probe.EventHandlerStoreForDebug!.Count);

        probe.RemoveHandler(UIElement.KeyDownEvent, handler);
        Assert.Equal(0, probe.EventHandlerStoreForDebug!.Count); // functional-empty
    }

    [Fact]
    public void N003_Bubble_VirtualThenHandlersPerNode_TargetToRoot()
    {
        var (log, root, a, b) = Chain();
        root.AddLog(UIElement.KeyDownEvent, "Root.h1");
        a.AddLog(UIElement.KeyDownEvent, "A.h1");
        b.AddLog(UIElement.KeyDownEvent, "B.h1");

        b.RaiseEvent(new KeyEventArgs(UIElement.KeyDownEvent, b, Enter));

        Assert.Equal(["B.OnKeyDown", "B.h1", "A.OnKeyDown", "A.h1", "Root.OnKeyDown", "Root.h1"], log);
    }

    [Fact]
    public void N004_Tunnel_RootToTarget_VirtualThenHandlersPerNode()
    {
        var (log, root, a, b) = Chain();
        root.AddLog(UIElement.PreviewKeyDownEvent, "Root.h1");
        a.AddLog(UIElement.PreviewKeyDownEvent, "A.h1");
        b.AddLog(UIElement.PreviewKeyDownEvent, "B.h1");

        b.RaiseEvent(new KeyEventArgs(UIElement.PreviewKeyDownEvent, b, Enter));

        Assert.Equal(
            ["Root.OnPreviewKeyDown", "Root.h1", "A.OnPreviewKeyDown", "A.h1", "B.OnPreviewKeyDown", "B.h1"],
            log);
    }

    [Fact]
    public void N005_Direct_OnlyTargetInvoked_NoWalk()
    {
        var (log, root, a, b) = Chain();
        root.AddLog(UIElement.MouseEnterEvent, "Root.h1");
        a.AddLog(UIElement.MouseEnterEvent, "A.h1");
        b.AddLog(UIElement.MouseEnterEvent, "B.h1");

        var device = new MouseEvent
        {
            Kind = MouseEventKind.Move,
            Position = new CellPosition(0, 0),
            Button = MouseButton.None,
            ButtonsHeld = MouseButtons.None,
            Modifiers = KeyModifiers.None,
            Timestamp = DateTimeOffset.UnixEpoch
        };
        b.RaiseEvent(new MouseEventArgs(UIElement.MouseEnterEvent, b, device));

        Assert.Equal(["B.OnMouseEnter", "B.h1"], log);
    }

    [Fact]
    public void N006_Handled_SkipsVirtualsAndNormalHandlers_HandledEventsTooStillRuns()
    {
        var (log, root, a, b) = Chain();
        a.AddLog(UIElement.KeyDownEvent, "A.h1", action: static e => e.Handled = true);
        root.AddLog(UIElement.KeyDownEvent, "Root.h1");
        root.AddLog(UIElement.KeyDownEvent, "Root.h2", handledEventsToo: true);

        b.RaiseEvent(new KeyEventArgs(UIElement.KeyDownEvent, b, Enter));

        Assert.DoesNotContain("Root.OnKeyDown", log);
        Assert.DoesNotContain("Root.h1", log);
        Assert.Contains("Root.h2", log); // the route ran to completion
    }

    [Fact]
    public void N007_HandledEventsTooHandler_MayUnhandle_DownstreamResumes()
    {
        var (log, root, a, b) = Chain();
        a.AddLog(UIElement.KeyDownEvent, "A.h1", action: static e => e.Handled = true);
        root.AddLog(UIElement.KeyDownEvent, "Root.h1");
        root.AddLog(UIElement.KeyDownEvent, "Root.h2", handledEventsToo: true, action: static e => e.Handled = false);
        root.AddLog(UIElement.KeyDownEvent, "Root.h3");

        b.RaiseEvent(new KeyEventArgs(UIElement.KeyDownEvent, b, Enter));

        Assert.DoesNotContain("Root.h1", log); // still handled when reached
        Assert.Contains("Root.h2", log);
        Assert.Contains("Root.h3", log); // un-handling resumed normal invocation downstream (ND2)
    }

    [Fact]
    public void N008_PreviewHandled_MainPhaseRunsHandledEventsTooOnly_ResultHandled()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();
        fixture.Log.Clear();

        fixture.Root.AddLog(UIElement.PreviewKeyDownEvent, "Root.p1", action: static e => e.Handled = true);
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.hKeyDown", handledEventsToo: true);

        var result = fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Enter));

        Assert.Equal(InputDispatchResult.DispatchedHandled, result);
        Assert.DoesNotContain("B.OnKeyDown", fixture.Log); // virtuals skipped once handled
        Assert.Contains("B.hKeyDown", fixture.Log);        // handledEventsToo still runs
    }

    [Fact]
    public void N009_PooledArgs_SameInstanceAcrossNodesAndPhases_SourceEqualsOriginalSourceEqualsTarget()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();

        var seen = new List<KeyEventArgs>();
        var sources = new List<(UIElement? Source, UIElement? Original)>();
        void Capture(KeyEventArgs e)
        {
            seen.Add(e);
            sources.Add((e.Source, e.OriginalSource));
        }

        fixture.Root.AddLog(UIElement.PreviewKeyDownEvent, "Root.p1", action: Capture);
        fixture.B.AddLog(UIElement.PreviewKeyDownEvent, "B.p1", action: Capture);
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.h1", action: Capture);
        fixture.Root.AddLog(UIElement.KeyDownEvent, "Root.h1", action: Capture);

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Enter));

        Assert.Equal(4, seen.Count);
        Assert.All(seen, args => Assert.Same(seen[0], args)); // one pooled instance, both phases
        
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        Assert.All(sources, pair =>
                            {
                                Assert.Same(fixture.B, pair.Source);
                                Assert.Same(fixture.B, pair.Original); // ND22
                            });
    }

#if DEBUG
    [Fact]
    public void N010_CapturedPooledArgs_StaleAfterDispatch_DeviceRecordRetainable()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();

        KeyEventArgs? captured = null;
        KeyEvent? copiedDevice = null;
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.h1", action: e =>
        {
            captured = e;
            copiedDevice = e.Device; // the wrapped record is immutable and retainable
        });

        fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Enter));

        Assert.NotNull(captured);
        Assert.Throws<InvalidOperationException>(() => captured!.Handled); // stale stamp
        Assert.NotNull(copiedDevice);
        Assert.Equal(Key.Enter, copiedDevice!.Key); // the device record remains valid
    }
#endif

    [Fact]
    public void N011_CallerNewArgs_CallerOwned_ValidAfterRaise()
    {
        var log = new List<string>();
        var routedEvent = RoutedEvent<RoutedEventArgs>.Register("N11", RoutingStrategy.Bubble, typeof(Probe));
        var probe = new Probe("P", log);

        var args = new RoutedEventArgs(routedEvent, probe);
        probe.RaiseEvent(args);

        Assert.Same(routedEvent, args.RoutedEvent); // never pooled, never stamped
        Assert.Same(probe, args.Source);
        Assert.False(args.Handled);
        args.Handled = true; // still writable — caller-owned
        Assert.True(args.Handled);
    }

    [Fact]
    public void N012_NestedRaiseEvent_DistinctPooledInstances_OuterUncorrupted()
    {
        var (log, root, a, b) = Chain();
        var c = new Probe("C", log);
        root.AddChild(c);

        KeyEventArgs? outerSeen = null;
        KeyEventArgs? innerSeen = null;
        a.AddLog(UIElement.KeyDownEvent, "A.h1", action: e =>
        {
            outerSeen = e;
            var inner = c.Rent(UIElement.KeyDownEvent);
            innerSeen = inner;
            c.RaiseEvent(inner); // nested dispatch completes before the outer continues
        });

        (UIElement? Source, RoutedEvent? Event)? outerAtRoot = null;
        root.AddLog(UIElement.KeyDownEvent, "Root.h1", action: e => outerAtRoot = (e.Source, e.RoutedEvent));

        var outer = b.Rent(UIElement.KeyDownEvent);
        b.RaiseEvent(outer);

        Assert.NotNull(outerSeen);
        Assert.NotNull(innerSeen);
        Assert.NotSame(outerSeen, innerSeen); // distinct free-list instances
        Assert.Equal(
            ["B.OnKeyDown", "A.OnKeyDown", "A.h1", "C.OnKeyDown", "Root.OnKeyDown", "Root.h1", "Root.OnKeyDown", "Root.h1"],
            log); // the nested raise (C→Root, hitting Root's handler too) finishes inside A.h1, then the outer resumes
        Assert.NotNull(outerAtRoot);
        Assert.Same(b, outerAtRoot!.Value.Source); // outer args uncorrupted by the nested dispatch
        Assert.Same(UIElement.KeyDownEvent, outerAtRoot.Value.Event);
    }

    [Fact]
    public void N013_RentEvent_ReturnedAtRaiseCompletion_NextRentalReuses_DoubleRaiseThrows()
    {
        var log = new List<string>();
        var routedEvent = RoutedEvent<RoutedEventArgs>.Register("N13", RoutingStrategy.Bubble, typeof(Probe));
        var probe = new Probe("P", log);

        var first = probe.Rent(routedEvent);
        probe.RaiseEvent(first); // returned to the pool at completion

        var second = probe.Rent(routedEvent);
        Assert.Same(first, second); // the free-list reused the instance

        probe.RaiseEvent(second);
#if DEBUG
        Assert.Throws<InvalidOperationException>(() => probe.RaiseEvent(second)); // one rental → one raise
#endif
    }

    [Fact]
    public void N014_HandlerException_PropagatesOutOfProcessEvent_NextDispatchFunctionsNormally()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();

        var shouldThrow = true;
        fixture.A.AddLog(UIElement.KeyDownEvent, "A.h1", action: _ =>
        {
            if (shouldThrow)
            {
                shouldThrow = false;
                throw new FormatException("matrix N14");
            }
        });

        Assert.Throws<FormatException>(() => fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Enter)));

        fixture.Log.Clear();
        var result = fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Enter)); // pooled resources released on unwind
        Assert.Equal(InputDispatchResult.DispatchedUnhandled, result);
        Assert.Contains("Root.OnKeyDown", fixture.Log); // full route ran
    }

    [Fact]
    public void N015_DetachTargetMidBubble_InFlightRouteUnchanged()
    {
        var (log, _, a, b) = Chain();
        a.AddLog(UIElement.KeyDownEvent, "A.h1", action: _ => a.RemoveChild(b)); // detach the target mid-route

        b.RaiseEvent(new KeyEventArgs(UIElement.KeyDownEvent, b, Enter));

        Assert.Contains("Root.OnKeyDown", log); // remaining nodes still invoked (ND3)
        Assert.Null(b.VisualParent);
    }

#if DEBUG
    [Fact]
    public void N016_ReentrantProcessEvent_DebugAssertion()
    {
        using var fixture = InputHost.CreateChain();
        fixture.B.Focus();

        // ReSharper disable AccessToDisposedClosure
        fixture.B.AddLog(UIElement.KeyDownEvent, "B.h1", action: _ =>
            fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Escape)));
        // ReSharper restore AccessToDisposedClosure

        Assert.Throws<InvalidOperationException>(() => fixture.Dispatcher.ProcessEvent(fixture.Key(Key.Enter)));
    }
#endif

    [Fact]
    public void N017_LogicalHopAtSurfaceRoot_RouteContinuesThroughLogicalParent()
    {
        var log = new List<string>();
        var hostRoot = new Probe("HostRoot", log);
        var host = new Probe("Host", log);
        hostRoot.AddChild(host);

        var popupRoot = new Probe("PopupRoot", log);
        var child = new Probe("Child", log);
        popupRoot.AddChild(child);
        host.AddLogical(popupRoot); // popup root in another visual tree, logically parented to the host

        child.RaiseEvent(new KeyEventArgs(UIElement.KeyDownEvent, child, Enter));

        Assert.Equal(["Child.OnKeyDown", "PopupRoot.OnKeyDown", "Host.OnKeyDown", "HostRoot.OnKeyDown"], log);
    }

    [Fact]
    public void N018_SameDelegateTwice_InvokedTwice_OneRemoveRemovesOne()
    {
        var log = new List<string>();
        var probe = new Probe("P", log);
        EventHandler<KeyEventArgs> handler = (_, _) => log.Add("h");
        probe.AddHandler(UIElement.KeyDownEvent, handler);
        probe.AddHandler(UIElement.KeyDownEvent, handler);

        probe.RaiseEvent(new KeyEventArgs(UIElement.KeyDownEvent, probe, Enter));
        Assert.Equal(2, log.Count(entry => entry == "h"));

        probe.RemoveHandler(UIElement.KeyDownEvent, handler);
        log.Clear();
        probe.RaiseEvent(new KeyEventArgs(UIElement.KeyDownEvent, probe, Enter));
        Assert.Equal(1, log.Count(entry => entry == "h"));
    }

    [Fact]
    public void N202_HandlerListMutationDuringRaise_InFlightSweepStable_AddedHandlerDeferred()
    {
        var log = new List<string>();
        var probe = new Probe("P", log);

        EventHandler<KeyEventArgs> h2 = (_, _) => log.Add("h2");
        EventHandler<KeyEventArgs> h3 = (_, _) => log.Add("h3");
        EventHandler<KeyEventArgs> h4 = (_, _) => log.Add("h4");
        EventHandler<KeyEventArgs> h1 = null!;
        h1 = (_, _) =>
        {
            log.Add("h1");
            probe.RemoveHandler(UIElement.KeyDownEvent, h1); // self-removal (the one-shot pattern)
            probe.RemoveHandler(UIElement.KeyDownEvent, h2); // removes a later registration
            probe.AddHandler(UIElement.KeyDownEvent, h4);    // added mid-raise
        };

        probe.AddHandler(UIElement.KeyDownEvent, h1);
        probe.AddHandler(UIElement.KeyDownEvent, h2);
        probe.AddHandler(UIElement.KeyDownEvent, h3);

        // First raise: the in-flight node sweep is stable (ND27) — h1, h2, h3 all run; h4 does not.
        probe.RaiseEvent(new KeyEventArgs(UIElement.KeyDownEvent, probe, Enter));
        Assert.Equal(["P.OnKeyDown", "h1", "h2", "h3"], log);

        // Second raise: the mutations are visible — exactly h3 then h4.
        log.Clear();
        probe.RaiseEvent(new KeyEventArgs(UIElement.KeyDownEvent, probe, Enter));
        Assert.Equal(["P.OnKeyDown", "h3", "h4"], log);
    }
}
