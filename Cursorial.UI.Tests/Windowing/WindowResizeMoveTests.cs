// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using System;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Testing;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Windowing;

/// <summary>
/// P7-W5 — chrome move/resize/maximize interactions (§8.3/§8.7), the no-auto-shrink screen-resize policy
/// with the fit-to-viewport affordance, the §8.8 deferred-topology queue, and shutdown
/// (<see cref="WindowManager.CloseAllAsync"/> + title → OSC 2).
/// </summary>
public sealed class WindowResizeMoveTests
{
    private static (UITestHost Host, WindowManager Wm) ShownRoot(bool captureBytes = false)
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(60, 20), CaptureFrameBytes = captureBytes });
        host.ShowRoot(new UIControls.StackPanel());
        Assert.True(host.RunUntilIdle());
        return (host, host.Application.WindowManager!);
    }

    private static Window At(int left, int top, int width = 20, int height = 8) => new()
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = left,
        Top = top,
        Width = width,
        Height = height,
    };

    private static MouseEvent Mouse(MouseEventKind kind, int col, int row, MouseButton button, MouseButtons held) => new()
    {
        Kind = kind,
        Position = new CellPosition(col, row),
        Button = button,
        ButtonsHeld = held,
        Modifiers = KeyModifiers.None,
        Timestamp = default, // SendInput stamps the fake clock
    };

    private static void Drag(UITestHost host, (int Col, int Row) from, (int Col, int Row) to)
    {
        host.SendInput(Mouse(MouseEventKind.ButtonDown, from.Col, from.Row, MouseButton.Left, MouseButtons.None));
        host.SendInput(Mouse(MouseEventKind.Drag, to.Col, to.Row, MouseButton.None, MouseButtons.Left));
        host.SendInput(Mouse(MouseEventKind.ButtonUp, to.Col, to.Row, MouseButton.Left, MouseButtons.None));
    }

    [Fact] // dragging the title bar moves the window (screen-anchored delta; SetCurrentValue Left/Top)
    public void TitleBarDrag_MovesWindow()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var w = At(10, 5);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        // The bordered chrome insets content by 1; the title bar sits at inner row Top+1.
        Drag(host, (12, 6), (15, 9)); // press the title bar, drag +3,+3
        Assert.True(host.RunUntilIdle());

        Assert.Equal(13, w.Left);
        Assert.Equal(8, w.Top);
    }

    [Fact] // dragging the ◢ grip resizes the window (Width/Height via SetCurrentValue)
    public void ResizeGripDrag_ResizesWindow()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var w = At(10, 5);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        // Inside the 1-cell border, the grip sits at inner (Left+W-2, Top+H-2) = (28,11); drag +3,+2.
        Drag(host, (28, 11), (31, 13));
        Assert.True(host.RunUntilIdle());

        Assert.Equal(23, w.Width);
        Assert.Equal(10, w.Height);
    }

    [Fact] // double-clicking the title bar maximizes; again restores the prior bounds
    public void TitleBarDoubleClick_TogglesMaximize()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var w = At(10, 5);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        host.SendClick(12, 6, clickCount: 2); // title bar at inner row Top+1
        Assert.True(host.RunUntilIdle());
        Assert.Equal(WindowState.Maximized, w.WindowState);
        Assert.Equal(new Size(60, 20), w.ActualSize); // fills the viewport
        Assert.Equal(0, w.Left);
        Assert.Equal(0, w.Top);

        host.SendClick(2, 1, clickCount: 2); // maximized: title bar at inner (1,1)
        Assert.True(host.RunUntilIdle());
        Assert.Equal(WindowState.Normal, w.WindowState);
        Assert.Equal(new Size(20, 8), w.ActualSize); // restored
        Assert.Equal(10, w.Left);
        Assert.Equal(5, w.Top);
    }

    [Fact] // a window opened partly off-screen reads clipped; FitToViewport moves it fully inside
    public void FitToViewport_MovesClippedWindowFullyInside()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var w = At(50, 18); // 20×8 at (50,18) overhangs the 60×20 viewport on both edges
        w.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.True(w.IsClippedByViewport);

        w.FitToViewport();
        Assert.True(host.RunUntilIdle());

        Assert.Equal(40, w.Left); // clamped to cols − width
        Assert.Equal(12, w.Top);  // clamped to rows − height
        Assert.False(w.IsClippedByViewport);
        Assert.Equal(new Size(20, 8), w.ActualSize); // not shrunk (it fit)
    }

    [Fact] // a terminal shrink does NOT shrink the window — it clamps MinVisible into view and flags clipped
    public void ScreenShrink_DoesNotShrinkWindow_ClampsAndFlagsClipped()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var w = At(30, 5);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        host.SendResize(40, 20); // window right edge (50) now overhangs 40
        Assert.True(host.RunUntilIdle());

        Assert.Equal(new Size(20, 8), w.ActualSize); // size preserved (no auto-shrink)
        Assert.Equal(30, w.Left);                    // still ≥ MinVisible visible → not pulled
        Assert.True(w.IsClippedByViewport);
    }

    [Fact] // a topology mutation requested during layout is deferred to the next frame's drain (§8.8)
    public void DeferredTopology_ShowDuringLayout_AppliesNextFrame()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(60, 20) });
        using var hostScope = host;
        var wm = host.Application.WindowManager!;

        var second = At(2, 2);
        var hook = new MeasureHook { OnMeasure = () => second.Show(wm) };
        host.ShowRoot(hook);

        host.RunFrame();                    // root's MeasureOverride (layout-locked) requests the Show → deferred
        Assert.Empty(wm.Windows);

        host.RunFrame();                    // DrainDeferredTopology applies it before this frame's layout
        Assert.Same(second, Assert.Single(wm.Windows));
    }

    [Fact] // shutdown closes every window and completes pending ShowDialogAsync tasks (null result)
    public void CloseAllAsync_ClosesWindows_AndCompletesDialogs()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var a = At(2, 2);
        a.Show(wm);
        var dialog = At(30, 2);
        var task = dialog.ShowDialogAsync();
        Assert.True(host.RunUntilIdle());
        Assert.Equal(2, wm.Windows.Count);

        wm.CloseAllAsync().GetAwaiter().GetResult();

        Assert.Empty(wm.Windows);
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Null(task.Result);
    }

    [Fact] // the active window's title mirrors to the terminal as OSC 2
    public void ActiveWindowTitle_EmittedAsOsc2()
    {
        var (host, wm) = ShownRoot(captureBytes: true);
        using var hostScope = host;

        var w = At(2, 2);
        w.Title = "Greetings";
        w.Show(wm);
        host.RunFrame(); // OnLayoutCompleted mirrors the active title; Phase 6 drains the control sequence

        var bytes = host.LastFrameBytes.ToArray();
        Assert.True(SubsequenceIndex(bytes, "\x1b]2;Greetings"u8.ToArray()) >= 0, "frame bytes should contain the OSC 2 title");
    }

    private static int SubsequenceIndex(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match)
                return i;
        }

        return -1;
    }

    [Fact] // a resize that clips a window raises the WM fit badge; refitting (or no clip) hides it
    public void ResizeClippingWindow_ShowsFitBadge()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var w = At(30, 5);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.False(wm.IsFitBadgeVisible); // nothing clipped yet

        host.SendResize(40, 20); // window right edge (50) now overhangs 40 → clipped
        Assert.True(host.RunUntilIdle());

        Assert.True(w.IsClippedByViewport);
        Assert.True(wm.IsFitBadgeVisible); // the badge appeared on the clipping resize
    }

    [Fact] // a resize that leaves every window fully visible shows no badge
    public void Resize_NoClip_NoBadge()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var w = At(2, 2);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        host.SendResize(50, 18); // window (cols 2..21, rows 2..9) still fits
        Assert.True(host.RunUntilIdle());

        Assert.False(w.IsClippedByViewport);
        Assert.False(wm.IsFitBadgeVisible);
    }

    [Fact] // the badge's "Fit windows" action refits every clipped window and hides the badge
    public void FitBadge_FitAll_RefitsAndHides()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var w = At(30, 5);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());
        host.SendResize(40, 20);
        Assert.True(host.RunUntilIdle());
        Assert.True(wm.IsFitBadgeVisible);

        wm.FitAllWindowsToViewport();
        Assert.True(host.RunUntilIdle());

        Assert.False(w.IsClippedByViewport); // pulled fully into the 40-wide viewport
        Assert.True(w.Left + w.ActualSize.Columns <= 40);
        Assert.False(wm.IsFitBadgeVisible);  // nothing left to fit
    }

    [Fact] // dismiss (✕) hides the badge and leaves the windows as-is (still clipped)
    public void FitBadge_Dismiss_HidesButLeavesWindows()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var w = At(30, 5);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());
        host.SendResize(40, 20);
        Assert.True(host.RunUntilIdle());
        Assert.True(wm.IsFitBadgeVisible);

        wm.DismissFitBadge();
        Assert.True(host.RunUntilIdle());

        Assert.False(wm.IsFitBadgeVisible);
        Assert.True(w.IsClippedByViewport); // dismiss does not move the window
    }

    [Fact] // setting WindowState=Maximized directly (not via the gesture) fills + restores like the gesture
    public void WindowStateMaximized_SetDirectly_FillsAndRestores()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var w = At(10, 5);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        w.WindowState = WindowState.Maximized; // direct property assignment, not a double-click
        Assert.True(host.RunUntilIdle());
        Assert.Equal(new Size(60, 20), w.ActualSize); // fills the viewport (element stretches — Width/Height nulled)
        Assert.Equal(0, w.Left);
        Assert.Equal(0, w.Top);

        w.WindowState = WindowState.Normal;
        Assert.True(host.RunUntilIdle());
        Assert.Equal(new Size(20, 8), w.ActualSize); // restored to the pre-maximize bounds
        Assert.Equal(10, w.Left);
        Assert.Equal(5, w.Top);
    }

    [Fact] // a shown window's root gets the caps-* capability classes stamped (P7 multi-surface styling)
    public void WindowSurface_GetsCapabilityClasses()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var w = At(10, 5);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        // The default test host negotiates KittyTruecolor → the window root carries the truecolor tier class
        // (the StyleEngine stamps every surface root, not just the app root).
        Assert.True(w.Classes.Contains("caps-truecolor"));
    }

    private sealed class MeasureHook : UIElement
    {
        private bool _fired;

        public Action? OnMeasure { get; init; }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (!_fired)
            {
                _fired = true;
                OnMeasure?.Invoke();
            }

            return new Size(10, 4);
        }
    }
}
