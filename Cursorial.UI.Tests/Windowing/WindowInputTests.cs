// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Windowing;

/// <summary>
/// P7-W3 — the WindowManager as the S3 topology (replacing SingleRootWindowTopology): an uncaptured press
/// hit-tests against the surface stack (top-down), activates an inactive enabled window, and is swallowed on
/// a modal-blocked window (the gate stays active). (Light dismiss + the :modal-attention pulse + capture
/// release ride W3-b/W4.)
/// </summary>
public sealed class WindowInputTests
{
    private static (UIHeadlessHost Host, WindowManager Wm) ShownRoot()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        host.ShowRoot(new UIControls.StackPanel());
        Assert.True(host.RunUntilIdle());
        return (host, host.Application.WindowManager!);
    }

    private static Window At(UIHeadlessHost host, int left, int top)
    {
        return host.NewWindow(
            windowStartupLocation: WindowStartupLocation.Manual,
            left: left,
            top: top,
            width: 20,
            height: 8
        );
    }

    [Fact] // a press on an inactive enabled window activates it (activation-on-press through the WM topology)
    public void ClickInactiveWindow_ActivatesIt()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var a = At(host, 2, 2);   // columns 2..21
        a.Show(wm);
        var b = At(host, 30, 2);  // columns 30..49 — non-overlapping with A
        b.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.True(b.IsActive);   // newest shown is active
        Assert.False(a.IsActive);

        host.SendClick(5, 4);      // inside A
        Assert.True(host.RunUntilIdle());

        Assert.True(a.IsActive);   // the press raised A
        Assert.False(b.IsActive);
    }

    [Fact] // a press on a modal-blocked window is swallowed — it does not activate; the gate stays active
    public void ClickBlockedWindow_DoesNotActivate_GateStays()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var a = At(host, 2, 2);
        a.Show(wm);
        Assert.True(host.RunUntilIdle());

        var dialog = At(host, 30, 2);
        dialog.Owner = a;
        _ = dialog.ShowDialogAsync();
        Assert.True(host.RunUntilIdle());
        Assert.True(dialog.IsActive);
        Assert.False(wm.IsInputEnabled(a)); // A is blocked behind the modal

        host.SendClick(5, 4);               // inside the blocked A
        host.RunFrame();                    // process the click (the pulse timer keeps RunUntilIdle non-idle)

        Assert.False(a.IsActive);           // swallowed — A did not activate
        Assert.True(dialog.IsActive);       // the gate stays active
        Assert.False(wm.IsInputEnabled(a)); // still blocked
    }

    [Fact] // a press on a blocked window pulses :modal-attention on the gate, cleared by the ~600ms timer (W3-b)
    public void ClickBlockedWindow_PulsesModalAttentionOnGate_ClearedAfterTimeout()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var a = At(host, 2, 2);
        a.Show(wm);
        Assert.True(host.RunUntilIdle());

        var dialog = At(host, 30, 2);
        dialog.Owner = a;
        var attention = 0;
        dialog.ModalAttention += (_, _) => attention++;
        _ = dialog.ShowDialogAsync();
        Assert.True(host.RunUntilIdle());

        host.SendClick(5, 4); // press the blocked A
        host.RunFrame();      // process the click → the pulse sets :modal-attention (the 600ms timer is now pending)

        Assert.True((dialog.InteractionStateInternal & InteractionState.ModalAttention) != 0);
        Assert.Equal(1, attention); // the code-behind ModalAttentionEvent fired once

        host.AdvanceTime(TimeSpan.FromMilliseconds(700)); // past the ~600ms cue lifetime
        Assert.True(host.RunUntilIdle());

        Assert.True((dialog.InteractionStateInternal & InteractionState.ModalAttention) == 0); // cleared
    }

    [Fact] // a modal blocking a window releases pointer capture held inside it (mid-gesture modal, W3-b)
    public void ModalBlock_ReleasesCaptureHeldInsideBlockedWindow()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var a = At(host, 2, 2);
        a.Show(wm);
        Assert.True(host.RunUntilIdle());

        Assert.True(a.CaptureMouse()); // A holds capture (a mid-gesture drag)
        Assert.Same(a, host.Application.InputDispatcher.MouseCaptureTarget);

        var dialog = At(host, 30, 2);
        dialog.Owner = a;
        _ = dialog.ShowDialogAsync();  // blocks A → releases the capture held inside it
        Assert.True(host.RunUntilIdle());

        Assert.Null(host.Application.InputDispatcher.MouseCaptureTarget);
    }

    [Fact] // a modal blocking a window closes the popups anchored in it — including NESTED popups, whose
           // anchors resolve transitively through their outer popup's anchor chain. Left open, a context
           // menu / filter popup stays fully interactive while its blocked owner cannot be, and its events
           // keep reaching the blocked owner's handlers through the popup→owner bridge (a modal-bypass
           // hole). PopupCloseReason.HostBlocked gets its first producer here.
    public void ModalBlock_ClosesPopupsAnchoredInBlockedWindow_IncludingNested()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var anchorButton = new UIControls.Button { Width = 6, Height = 1, Content = "menu" };
        var a = At(host, 2, 2);
        a.Content = anchorButton;
        a.Show(wm);
        Assert.True(host.RunUntilIdle());

        // Outer popup anchored in A; inner popup anchored in the OUTER's content (the submenu shape).
        var innerAnchor = new UIControls.Button { Width = 6, Height = 1, Content = "sub" };
        var outerContent = new UIControls.StackPanel { Children = { innerAnchor } };
        var outer = new Popup { Child = outerContent, PlacementTarget = anchorButton };
        var inner = new Popup { Child = new UIControls.Button { Width = 6, Height = 1, Content = "x" }, PlacementTarget = innerAnchor };

        PopupCloseReason? outerReason = null, innerReason = null;
        outer.Closed += (_, e) => outerReason = e.Reason;
        inner.Closed += (_, e) => innerReason = e.Reason;

        outer.Open();
        Assert.True(host.RunUntilIdle());
        inner.Open();
        Assert.True(host.RunUntilIdle());
        Assert.Equal(2, wm.Popups.Count);

        var dialog = At(host, 30, 2);
        _ = dialog.ShowDialogAsync(); // blocks A (the modal owns nothing) → A's popup chain closes
        Assert.True(host.RunUntilIdle());

        Assert.False(outer.IsOpen);
        Assert.False(inner.IsOpen);
        Assert.Empty(wm.Popups);
        Assert.Equal(PopupCloseReason.HostBlocked, outerReason);
        Assert.NotNull(innerReason); // closed with the chain (HostBlocked directly, or the outer's cascade)
    }

    [Fact] // the migration-review pin: capture held INSIDE a popup anchored in a window that becomes
           // modal-blocked is released (mid-gesture modal over a filter-popup drag). The popup closes on the
           // block (HostBlocked) and the surface-close path force-releases the capture — pinned here so a
           // future narrowing of capture/ancestry walks cannot regress it silently.
    public void ModalBlock_ReleasesCaptureHeldInPopupOfBlockedWindow()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var anchorButton = new UIControls.Button { Width = 6, Height = 1, Content = "menu" };
        var a = At(host, 2, 2);
        a.Content = anchorButton;
        a.Show(wm);
        Assert.True(host.RunUntilIdle());

        var thumb = new UIControls.Button { Width = 6, Height = 1, Content = "drag" };
        var popup = new Popup { Child = thumb, PlacementTarget = anchorButton };
        popup.Open();
        Assert.True(host.RunUntilIdle());

        Assert.True(thumb.CaptureMouse()); // a mid-gesture drag inside the popup
        Assert.Same(thumb, host.Application.InputDispatcher.MouseCaptureTarget);

        var dialog = At(host, 30, 2);
        _ = dialog.ShowDialogAsync(); // blocks A → the popup closes → its surface's capture releases
        Assert.True(host.RunUntilIdle());

        Assert.False(popup.IsOpen);
        Assert.Null(host.Application.InputDispatcher.MouseCaptureTarget);
    }

    [Fact] // the ROOT band is modal-gated like a blocked window: while a modal is up, a press on root
           // content is swallowed (no routing into the root) and pulses the gate's :modal-attention cue.
           // Without the gate, root presses routed normally BENEATH the modal (the root-band bypass hole —
           // the blocked-swallow branch required a non-null HostWindow, and the root surface has none).
    public void ModalUp_RootBandPress_SwallowedAndPulsesGate()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        using var hostScope = host;

        var rootButton = new UIControls.Button { Width = 10, Height = 1, Content = "root" };
        host.ShowRoot(new UIControls.StackPanel { Children = { rootButton } });
        Assert.True(host.RunUntilIdle());
        var wm = host.Application.WindowManager!;

        var sawPress = false;
        rootButton.AddHandler(UIElement.MouseDownEvent, (object? _, Cursorial.UI.Input.MouseButtonEventArgs _) => sawPress = true, handledEventsToo: true);

        var dialog = At(host, 30, 4);
        _ = dialog.ShowDialogAsync();
        Assert.True(host.RunUntilIdle());

        host.SendClick(2, 0); // on the root button, beneath the modal band
        host.RunFrame();      // process the click (the pulse timer keeps RunUntilIdle non-idle)

        Assert.False(sawPress); // swallowed — never routed into root content
        Assert.True((dialog.InteractionStateInternal & InteractionState.ModalAttention) != 0); // the gate pulsed
    }

    [Fact] // a modal push closes popups anchored in ROOT content (the root band blocks with the push) —
           // same HostBlocked path as blocked windows.
    public void ModalPush_ClosesRootAnchoredPopups()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        using var hostScope = host;

        var anchor = new UIControls.Button { Width = 10, Height = 1, Content = "anchor" };
        host.ShowRoot(new UIControls.StackPanel { Children = { anchor } });
        Assert.True(host.RunUntilIdle());
        var wm = host.Application.WindowManager!;

        var popup = new Popup { Child = new UIControls.Button { Width = 6, Height = 1, Content = "x" }, PlacementTarget = anchor };
        PopupCloseReason? reason = null;
        popup.Closed += (_, e) => reason = e.Reason;
        popup.Open();
        Assert.True(host.RunUntilIdle());
        Assert.Single(wm.Popups);

        var dialog = At(host, 30, 4);
        _ = dialog.ShowDialogAsync();
        Assert.True(host.RunUntilIdle());

        Assert.False(popup.IsOpen);
        Assert.Empty(wm.Popups);
        Assert.Equal(PopupCloseReason.HostBlocked, reason);
    }

    [Fact] // a modal push releases capture held by ROOT content (the W3-b mid-gesture argument, root
           // edition — previously only blocked WINDOWS released their captures).
    public void ModalPush_ReleasesCaptureHeldInRootContent()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        using var hostScope = host;

        var dragee = new UIControls.Button { Width = 10, Height = 1, Content = "drag" };
        host.ShowRoot(new UIControls.StackPanel { Children = { dragee } });
        Assert.True(host.RunUntilIdle());

        Assert.True(dragee.CaptureMouse());
        Assert.Same(dragee, host.Application.InputDispatcher.MouseCaptureTarget);

        var dialog = At(host, 30, 4);
        _ = dialog.ShowDialogAsync();
        Assert.True(host.RunUntilIdle());

        Assert.Null(host.Application.InputDispatcher.MouseCaptureTarget);
    }

    [Fact] // the root element is hosted in a framework RootElementHost; a modal push applies the
           // blocked-band look to it (the obscured class + the Window PART_ObscuredOverlay darkening
           // recipe), cleared when the last modal closes. RootElement keeps reflecting the assigned value.
    public void ModalPush_AppliesObscuredLookToRootHost_ClearedOnLastClose()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        using var hostScope = host;

        var userRoot = new UIControls.StackPanel();
        host.ShowRoot(userRoot);
        Assert.True(host.RunUntilIdle());
        var wm = host.Application.WindowManager!;

        var rootHost = Assert.IsType<RootElementHost>(wm.RootSurface!.Root); // the wrapper hosts the root…
        Assert.Same(userRoot, rootHost.Content);                             // …and the assigned value is intact
        Assert.Same(userRoot, host.Application.RootElement);                 // the public API reflects the user's element
        Assert.False(rootHost.Classes.Contains("obscured"));

        var dialog = At(host, 30, 4);
        _ = dialog.ShowDialogAsync();
        Assert.True(host.RunUntilIdle());
        Assert.True(rootHost.Classes.Contains("obscured")); // dimmed with the push

        dialog.Close();
        Assert.True(host.RunUntilIdle());
        Assert.False(rootHost.Classes.Contains("obscured")); // the last modal closed — un-dimmed
    }

    [Fact] // ROUTING-REVIEW: the gesture TAIL's window→app-root leg — an APPLICATION-root chord fires while
           // focus sits inside a WINDOW. Windows are route islands (a window's key route never reaches the
           // app root), so before the tail this required per-root reinstalls (EnsureFrameworkBindings); the
           // tail generalizes that: after the route returns unhandled, the InputBindings-only sweep
           // continues at the application root.
    public void AppRootChord_FiresFromWindowFocus()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        using var hostScope = host;

        var appRoot = new UIControls.StackPanel();
        var fired = false;
        appRoot.InputBindings.Add(new Cursorial.UI.Input.KeyBinding(
            new Cursorial.UI.Input.KeyGesture(Cursorial.Input.Key.Character, Cursorial.Input.KeyModifiers.Control, "J"),
            new RelayTestCommand(() => fired = true)));
        host.ShowRoot(appRoot);
        Assert.True(host.RunUntilIdle());

        var content = new UIControls.Button { Width = 6, Height = 1, Content = "in A" };
        var a = At(host, 2, 2);
        a.Content = content;
        a.Show(host.Application.WindowManager!);
        Assert.True(host.RunUntilIdle());
        Assert.True(content.Focus());
        Assert.True(host.RunUntilIdle());

        host.SendKey(Cursorial.Input.Key.Character, Cursorial.Input.KeyModifiers.Control, "J");
        Assert.True(host.RunUntilIdle());

        Assert.True(fired); // the app-root chord reached from window-focused content (the tail's leg 2)
    }

    [Fact] // ROUTING-REVIEW: the tail's leg-1 → leg-2 HANDOFF — a popup anchored in WINDOW content walks the
           // owner chain (leg 1) to the window root, then continues at the application root (leg 2): an
           // app-root chord fires from popup-in-window focus.
    public void AppRootChord_FiresFromPopupInWindowFocus()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        using var hostScope = host;

        var appRoot = new UIControls.StackPanel();
        var fired = false;
        appRoot.InputBindings.Add(new Cursorial.UI.Input.KeyBinding(
            new Cursorial.UI.Input.KeyGesture(Cursorial.Input.Key.Character, Cursorial.Input.KeyModifiers.Control, "J"),
            new RelayTestCommand(() => fired = true)));
        host.ShowRoot(appRoot);
        Assert.True(host.RunUntilIdle());

        var anchor = new UIControls.Button { Width = 6, Height = 1, Content = "anchor" };
        var a = At(host, 2, 2);
        a.Content = anchor;
        a.Show(host.Application.WindowManager!);
        Assert.True(host.RunUntilIdle());

        var inner = new UIControls.Button { Width = 6, Height = 1, Content = "in popup" };
        var popup = new Popup { Child = inner, PlacementTarget = anchor };
        popup.Open();
        Assert.True(host.RunUntilIdle());
        Assert.True(inner.Focus());
        Assert.True(host.RunUntilIdle());

        host.SendKey(Cursorial.Input.Key.Character, Cursorial.Input.KeyModifiers.Control, "J");
        Assert.True(host.RunUntilIdle());

        Assert.True(fired); // leg 1 climbed anchor → window root; leg 2 delivered the app-root chord
    }

    [Fact] // ROUTING-REVIEW: the tail's leg-2 MODAL gate — app-root chords do NOT fire while a modal holds
           // focus (modality: an app-level chord must not commandeer the blocked main UI). Framework chords
           // are unaffected: EnsureFrameworkBindings installs them on the modal's own root, on-route.
    public void AppRootChord_DoesNotFireFromModalFocus()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        using var hostScope = host;

        var appRoot = new UIControls.StackPanel();
        var fired = false;
        appRoot.InputBindings.Add(new Cursorial.UI.Input.KeyBinding(
            new Cursorial.UI.Input.KeyGesture(Cursorial.Input.Key.Character, Cursorial.Input.KeyModifiers.Control, "J"),
            new RelayTestCommand(() => fired = true)));
        host.ShowRoot(appRoot);
        Assert.True(host.RunUntilIdle());

        var content = new UIControls.Button { Width = 6, Height = 1, Content = "in modal" };
        var dialog = At(host, 30, 4);
        dialog.Content = content;
        _ = dialog.ShowDialogAsync();
        Assert.True(host.RunUntilIdle());
        Assert.True(content.Focus());
        Assert.True(host.RunUntilIdle());

        host.SendKey(Cursorial.Input.Key.Character, Cursorial.Input.KeyModifiers.Control, "J");
        Assert.True(host.RunUntilIdle());

        Assert.False(fired); // the modal gate held: app chords stay out of modal sessions
    }

    [Fact] // ROUTING-REVIEW × MODAL-INTEGRITY: a modal push CLOSES popups anchored in the window it
           // blocks — StaysOpen included (modality outranks StaysOpen) — so the gesture tail's leg-1
           // "anchor window blocked" gate is unconstructible in normal topology and remains defense-in-
           // depth. Pinned here: the nested StaysOpen chain closes with the push, and the blocked window's
           // chord does not fire from post-push focus.
    public void ModalPush_ClosesStaysOpenPopupChain_BlockedWindowChordDoesNotFire()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 20) });
        using var hostScope = host;
        host.ShowRoot(new UIControls.StackPanel());
        Assert.True(host.RunUntilIdle());

        var anchor = new UIControls.Button { Width = 6, Height = 1, Content = "anchor" };
        var a = At(host, 2, 2);
        a.Content = anchor;
        a.Show(host.Application.WindowManager!);
        Assert.True(host.RunUntilIdle());

        var fired = false;
        a.InputBindings.Add(new Cursorial.UI.Input.KeyBinding(
            new Cursorial.UI.Input.KeyGesture(Cursorial.Input.Key.Character, Cursorial.Input.KeyModifiers.Control, "K"),
            new RelayTestCommand(() => fired = true)));

        // Nested StaysOpen popups: outer anchored in the window, inner anchored in the OUTER's content.
        var innerAnchor = new UIControls.Button { Width = 6, Height = 1, Content = "sub" };
        var outer = new Popup { Child = innerAnchor, PlacementTarget = anchor, StaysOpen = true };
        var innerButton = new UIControls.Button { Width = 6, Height = 1, Content = "x" };
        var inner = new Popup { Child = innerButton, PlacementTarget = innerAnchor, StaysOpen = true };
        outer.Open();
        Assert.True(host.RunUntilIdle());
        inner.Open();
        Assert.True(host.RunUntilIdle());
        Assert.True(innerButton.Focus());
        Assert.True(host.RunUntilIdle());

        // The modal has CONTENT so activation pulls focus into it deterministically.
        var dialog = At(host, 30, 10);
        dialog.Content = new UIControls.Button { Width = 6, Height = 1, Content = "modal" };
        _ = dialog.ShowDialogAsync();
        Assert.True(host.RunUntilIdle());

        Assert.False(host.Application.WindowManager!.IsInputEnabled(a));
        Assert.False(outer.IsOpen); // the push closed the chain, StaysOpen notwithstanding
        Assert.False(inner.IsOpen);

        host.SendKey(Cursorial.Input.Key.Character, Cursorial.Input.KeyModifiers.Control, "K");
        Assert.True(host.RunUntilIdle());

        Assert.False(fired); // the blocked window's chord is unreachable from modal focus
    }

    private sealed class RelayTestCommand(Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
