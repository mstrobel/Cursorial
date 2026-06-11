using Cursorial.Input.Events;
using Cursorial.UI;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.InputMatrix;

// ReSharper disable InconsistentNaming

/// <summary>
/// Input matrix §10 — <see cref="InteractionState"/> plumbing, the ND11 batching/observer seam,
/// and the pressed-holder set (N136–N147).
/// </summary>
public class Section10_InteractionState
{
    private static (UITestHost Host, List<string> Log, Probe Root, Btn A, Btn B, StateSink Sink) CreateHost()
    {
        var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var a = new Btn("A", log);
        var b = new Btn("B", log);
        root.AddChild(a);
        root.AddChild(b);
        host.ShowRoot(root); // activation auto-focuses A (first tab stop, N115)
        host.Application.FocusManager.ClearFocus();
        log.Clear();

        var sink = new StateSink();
        host.Application.InteractionStateObserver = sink;
        return (host, log, root, a, b, sink);
    }

    [Fact]
    public void N136_UnbatchedFlip_OneImmediateNotification_AfterCommit()
    {
        var (host, _, _, _, b, sink) = CreateHost();
        using var _host = host;

        InteractionState? maskInsideCallback = null;
        host.Application.InteractionStateObserver = new ProbeSink(
            sink,
            (element, _, _) => maskInsideCallback = element.InteractionStateInternal);

        b.SetState(InteractionState.Pressed, true);

        Assert.True((b.InteractionStateInternal & InteractionState.Pressed) != 0);
        var notification = Assert.Single(sink.Notifications);
        Assert.Same(b, notification.Element);
        Assert.Equal(InteractionState.None, notification.OldState);
        Assert.Equal(InteractionState.Pressed, notification.NewState);
        Assert.Equal(InteractionState.Pressed, maskInsideCallback); // delivered AFTER commit
    }

    [Fact]
    public void N137_RedundantFlip_EqualityGated_NoNotificationNoHolderChurn()
    {
        var (host, _, _, _, b, sink) = CreateHost();
        using var _host = host;

        b.SetState(InteractionState.Pressed, true);
        sink.Notifications.Clear();

        b.SetState(InteractionState.Pressed, true);

        Assert.Empty(sink.Notifications);
        Assert.True(host.Application.InputDispatcher.IsPressedHolderInternal(b));
        Assert.Equal(1, host.Application.InputDispatcher.PressedHolderCountInternal);
    }

    [Fact]
    public void N138_BatchedFlips_OneCoalescedNotificationAtDispose()
    {
        var (host, _, _, _, b, sink) = CreateHost();
        using var _host = host;

        using (b.BeginUpdate())
        {
            b.SetState(InteractionState.PointerOver, true);
            b.SetState(InteractionState.FocusWithin, true);
            Assert.Empty(sink.Notifications); // zero notifications inside the scope
        }

        var notification = Assert.Single(sink.Notifications);
        Assert.Same(b, notification.Element);
        Assert.Equal(InteractionState.None, notification.OldState);
        Assert.Equal(InteractionState.PointerOver | InteractionState.FocusWithin, notification.NewState);
    }

    [Fact]
    public void N139_SetThenClearInOneBatch_NetZero_NoNotification()
    {
        var (host, _, _, _, b, sink) = CreateHost();
        using var _host = host;

        using (b.BeginUpdate())
        {
            b.SetState(InteractionState.Pressed, true);
            b.SetState(InteractionState.Pressed, false);
        }

        Assert.Empty(sink.Notifications);
    }

    [Fact]
    public void N140_NestedScopes_SingleFlushAtOutermostDispose()
    {
        var (host, _, _, _, b, sink) = CreateHost();
        using var _host = host;

        using (b.BeginUpdate())
        {
            b.SetState(InteractionState.PointerOver, true);
            using (b.BeginUpdate())
            {
                b.SetState(InteractionState.Pressed, true);
            }

            Assert.Empty(sink.Notifications); // inner dispose does not flush
        }

        var notification = Assert.Single(sink.Notifications);
        Assert.Equal(InteractionState.PointerOver | InteractionState.Pressed, notification.NewState);
    }

    [Fact]
    public void N141_BatchTouchingTwoElements_OnePerElement_FirstFlipOrder()
    {
        var (host, _, _, a, b, sink) = CreateHost();
        using var _host = host;

        using (a.BeginUpdate())
        {
            b.SetState(InteractionState.PointerOver, true); // B flips first
            a.SetState(InteractionState.PointerOver, true);
            b.SetState(InteractionState.Pressed, true);     // second B flip — same record
        }

        Assert.Equal(2, sink.Notifications.Count);
        Assert.Same(b, sink.Notifications[0].Element); // first-flip order
        Assert.Same(a, sink.Notifications[1].Element);
        Assert.Equal(InteractionState.PointerOver | InteractionState.Pressed, sink.Notifications[0].NewState);
    }

    [Fact]
    public void N142_HoverMove_RidesOneBatch_NoCommonPrefixNotifications()
    {
        using var fixture = MouseHost.Create();
        var sink = new StateSink();
        fixture.Host.Application.InteractionStateObserver = sink;

        fixture.Move(13, 7); // chain Root→A→D
        sink.Notifications.Clear();

        fixture.Move(31, 6); // the N74 move: leave D,A — enter B

        Assert.Equal(3, sink.Notifications.Count); // one per changed element
        Assert.Same(fixture.D, sink.Notifications[0].Element); // removed, deepest-first
        Assert.Same(fixture.A, sink.Notifications[1].Element);
        Assert.Same(fixture.B, sink.Notifications[2].Element); // added
        Assert.DoesNotContain(sink.Notifications, n => ReferenceEquals(n.Element, fixture.Root)); // common prefix silent
    }

    [Fact]
    public void N143_FocusChange_RidesOneBatch_DivergentChainsOnly()
    {
        var (host, _, root, a, b, sink) = CreateHost();
        using var _host = host;

        Assert.True(a.Focus());
        sink.Notifications.Clear();

        Assert.True(b.Focus());

        // One batch: A's Focused|FocusVisible|FocusWithin off, B's on; Root (common ancestor) silent.
        Assert.Equal(2, sink.Notifications.Count);
        var forA = sink.Notifications.Single(n => ReferenceEquals(n.Element, a));
        var forB = sink.Notifications.Single(n => ReferenceEquals(n.Element, b));
        Assert.Equal(InteractionState.None, forA.NewState);
        Assert.Equal(InteractionState.Focused | InteractionState.FocusWithin | InteractionState.FocusVisible, forB.NewState);
        Assert.DoesNotContain(sink.Notifications, n => ReferenceEquals(n.Element, root));
        Assert.True((root.InteractionStateInternal & InteractionState.FocusWithin) != 0); // unchanged, still set
    }

    [Fact]
    public void N144_PressedHolderSet_EnterLeaveDetach()
    {
        var (host, _, root, _, b, _) = CreateHost();
        using var _host = host;
        var dispatcher = host.Application.InputDispatcher;

        b.SetState(InteractionState.Pressed, true);
        Assert.True(dispatcher.IsPressedHolderInternal(b));

        b.SetState(InteractionState.Pressed, false);
        Assert.False(dispatcher.IsPressedHolderInternal(b));

        b.SetState(InteractionState.Pressed, true);
        root.RemoveChild(b); // detach removes (ND12)
        Assert.False(dispatcher.IsPressedHolderInternal(b));
        Assert.Equal(0, dispatcher.PressedHolderCountInternal);
    }

    [Fact]
    public void N145_TerminalFocusOut_ClearsAllPressed_OnePassOneNotificationEach()
    {
        var (host, _, _, a, b, sink) = CreateHost();
        using var _host = host;

        a.SetState(InteractionState.Pressed, true);
        b.SetState(InteractionState.Pressed, true); // B without capture — the C8 coverage
        sink.Notifications.Clear();

        host.Application.InputDispatcher.ProcessEvent(new FocusEvent { HasFocus = false, Timestamp = DateTimeOffset.UnixEpoch });

        Assert.Equal(0u, (uint)(a.InteractionStateInternal & InteractionState.Pressed));
        Assert.Equal(0u, (uint)(b.InteractionStateInternal & InteractionState.Pressed));
        Assert.Equal(2, sink.Notifications.Count); // one per holder
        Assert.Contains(sink.Notifications, n => ReferenceEquals(n.Element, a));
        Assert.Contains(sink.Notifications, n => ReferenceEquals(n.Element, b));
        Assert.Equal(0, host.Application.InputDispatcher.PressedHolderCountInternal);
    }

    [Fact]
    public void N146_IsEnabledCoreFlip_DisabledStatePushed_OneNotification()
    {
        using var host = UITestHost.Create();
        var log = new List<string>();
        var root = new Probe("Root", log);
        var btn = new CommandBtn("B", log);
        root.AddChild(btn);
        host.ShowRoot(root);
        host.Application.FocusManager.ClearFocus(); // isolate the :disabled push from the ND28 focus repair

        var sink = new StateSink();
        host.Application.InteractionStateObserver = sink;

        btn.SetIsEnabledCore(false);

        Assert.False(btn.IsEffectivelyEnabled);
        var off = Assert.Single(sink.Notifications, n => ReferenceEquals(n.Element, btn));
        Assert.True((off.NewState & InteractionState.Disabled) != 0);

        sink.Notifications.Clear();
        btn.SetIsEnabledCore(true);

        Assert.True(btn.IsEffectivelyEnabled);
        var on = Assert.Single(sink.Notifications, n => ReferenceEquals(n.Element, btn));
        Assert.Equal(0u, (uint)(on.NewState & InteractionState.Disabled));
    }

    [Fact]
    public void N147_IsPointerOver_ReadsTheBit()
    {
        using var fixture = MouseHost.Create();

        fixture.Move(13, 7);
        Assert.True(fixture.D.IsPointerOver);
        Assert.True(fixture.A.IsPointerOver);

        fixture.Move(31, 6);
        Assert.False(fixture.D.IsPointerOver); // false after leave
        Assert.True(fixture.B.IsPointerOver);
    }

    [Fact]
    public void N211_ObserverOpensBatchDuringFlush_NoReplay_NestedFlipDeliversOnceAfter()
    {
        var (host, _, root, a, b, sink) = CreateHost();
        using var _host = host;

        // The P3 styling-engine shape: the observer reacts to A's notification by opening its own
        // batch and flipping a THIRD element (root). ND29: no replay, the reaction delivers once.
        host.Application.InteractionStateObserver = new ProbeSink(sink, (element, _, _) =>
        {
            if (!ReferenceEquals(element, a))
                return;

            using var reaction = a.BeginUpdate(); // nested batch from inside the flush
            root.SetState(InteractionState.AccessKeyCue, true);
        });

        // ReSharper disable once UnusedVariable
        using (var batch = a.BeginUpdate())
        {
            a.SetState(InteractionState.Pressed, true);
            b.SetState(InteractionState.Pressed, true);
        }

        // A and B each exactly once for the original batch; root's reaction flip exactly once,
        // delivered after the original entries; nothing replayed.
        Assert.Equal(3, sink.Notifications.Count);
        Assert.Same(a, sink.Notifications[0].Element);
        Assert.Same(b, sink.Notifications[1].Element);
        Assert.Same(root, sink.Notifications[2].Element);
        Assert.True((root.InteractionStateInternal & InteractionState.AccessKeyCue) != 0);
    }

    /// <summary>A pass-through sink that also runs an in-callback probe (the N136 post-commit read).</summary>
    private sealed class ProbeSink(StateSink inner, Action<UIElement, InteractionState, InteractionState> probe) : IInteractionStateObserver
    {
        public void OnInteractionStateChanged(UIElement element, InteractionState oldState, InteractionState newState)
        {
            probe(element, oldState, newState);
            inner.OnInteractionStateChanged(element, oldState, newState);
        }
    }
}
