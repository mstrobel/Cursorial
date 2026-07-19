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

    [Fact] // ROUTING-REVIEW: the tail's leg-1 MODAL gate resolves NESTED popup anchors to the root host —
           // a chord on a window's content does not fire from a popup chain whose host window is blocked
           // (popup surfaces carry no HostWindow; the gate walks anchors transitively).
    public void OwnerChainChord_DoesNotFire_WhenAnchorWindowModalBlocked()
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

        // Nested standalone popups: outer anchored in the window, inner anchored in the OUTER's content.
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

        // Block the anchor's window with a modal the popups do not belong to.
        var dialog = At(host, 30, 10);
        _ = dialog.ShowDialogAsync();
        Assert.True(host.RunUntilIdle());
        Assert.False(host.Application.WindowManager!.IsInputEnabled(a));

        // The popups (StaysOpen; anchored in the now-blocked A) still exist; a chord typed with focus in
        // the INNER popup must not reach A's InputBindings through the tail.
        if (host.Application.FocusManager.FocusedElement != innerButton)
            _ = innerButton.Focus(); // the modal took focus; put it back for the delivery test
        Assert.True(host.RunUntilIdle());

        host.SendKey(Cursorial.Input.Key.Character, Cursorial.Input.KeyModifiers.Control, "K");
        Assert.True(host.RunUntilIdle());

        Assert.False(fired); // the transitive gate resolved inner → outer → A (blocked) and delivered nothing
    }

    private sealed class RelayTestCommand(Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
