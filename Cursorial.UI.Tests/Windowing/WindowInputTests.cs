// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Testing;

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
    private static (UITestHost Host, WindowManager Wm) ShownRoot()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(60, 20) });
        host.ShowRoot(new UIControls.StackPanel());
        Assert.True(host.RunUntilIdle());
        return (host, host.Application.WindowManager!);
    }

    private static Window At(int left, int top) => new()
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = left,
        Top = top,
        Width = 20,
        Height = 8
    };

    [Fact] // a press on an inactive enabled window activates it (activation-on-press through the WM topology)
    public void ClickInactiveWindow_ActivatesIt()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var a = At(2, 2);   // columns 2..21
        a.Show(wm);
        var b = At(30, 2);  // columns 30..49 — non-overlapping with A
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

        var a = At(2, 2);
        a.Show(wm);
        Assert.True(host.RunUntilIdle());

        var dialog = At(30, 2);
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

        var a = At(2, 2);
        a.Show(wm);
        Assert.True(host.RunUntilIdle());

        var dialog = At(30, 2);
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

        var a = At(2, 2);
        a.Show(wm);
        Assert.True(host.RunUntilIdle());

        Assert.True(a.CaptureMouse()); // A holds capture (a mid-gesture drag)
        Assert.Same(a, host.Application.InputDispatcher.MouseCaptureTarget);

        var dialog = At(30, 2);
        dialog.Owner = a;
        _ = dialog.ShowDialogAsync();  // blocks A → releases the capture held inside it
        Assert.True(host.RunUntilIdle());

        Assert.Null(host.Application.InputDispatcher.MouseCaptureTarget);
    }
}
