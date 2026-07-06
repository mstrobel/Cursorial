// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Testing;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Windowing;

/// <summary>
/// <see cref="SizeToContentMode"/> (§8.5): <see cref="SizeToContentMode.Always"/> windows keep tracking
/// their content — grow AND shrink — converging <b>inside</b> the layout phase so the resized frame is
/// the first one rendered (the no-flicker contract); <see cref="SizeToContentMode.Once"/> (the default)
/// fits at first show and then holds. Explicit <c>Width</c>/<c>Height</c> pins an axis in both modes;
/// restore-from-maximize re-fits tracked axes in both modes; an overhanging content grow follows the
/// §8.7 badge policy unless the window opts into <see cref="Window.AutoFitToViewport"/>.
/// </summary>
public sealed class WindowSizeToContentTests
{
    private static (UITestHost Host, WindowManager Wm) ShownRoot(int columns = 60, int rows = 20)
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(columns, rows) });
        host.ShowRoot(new UIControls.StackPanel());
        Assert.True(host.RunUntilIdle());
        return (host, host.Application.WindowManager!);
    }

    /// <summary>A fixed-size block whose text makes it findable in the rendered frame.</summary>
    private static UIControls.Border Block(int width, int height, string? text = null) => new()
    {
        Width = width,
        Height = height,
        Child = text is null ? null : new UIControls.TextBlock { Text = text }
    };

    private static Window TrackedWindow(
        UIControls.StackPanel content,
        SizeToContentMode mode,
        int left = 10,
        int top = 3)
    {
        return new Window
               {
                   Content = content,
                   SizeToContent = SizeToContent.WidthAndHeight,
                   SizeToContentMode = mode,
                   WindowStartupLocation = WindowStartupLocation.Manual,
                   Left = left,
                   Top = top
               };
    }

    private static bool FrameContains(UITestHost host, string text)
    {
        for (var row = 0; row < host.FrameBuffer.Rows; row++)
        {
            if (host.GetRowText(row).Contains(text))
                return true;
        }

        return false;
    }

    [Fact] // the core ask: content growth re-fits the window, and the FIRST frame after the change is already correct
    public void Always_ContentGrow_ResizesWithinOneFrame()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var content = new UIControls.StackPanel { Children = { Block(20, 2) } };
        var w = TrackedWindow(content, SizeToContentMode.Always);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        var before = w.ActualSize;

        content.Children.Add(Block(20, 3, "GROWN"));
        host.RunFrame(); // exactly ONE frame — the resize must converge inside its layout phase

        Assert.Equal(before.Rows + 3, w.ActualSize.Rows);
        Assert.True(FrameContains(host, "GROWN")); // the grown content rendered in that same frame — no flicker window
    }

    [Fact] // shrink tracks too
    public void Always_ContentShrink_ResizesBack()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var extra = Block(20, 3);
        var content = new UIControls.StackPanel { Children = { Block(20, 2), extra } };
        var w = TrackedWindow(content, SizeToContentMode.Always);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        var before = w.ActualSize;

        content.Children.Remove(extra);
        host.RunFrame();

        Assert.Equal(before.Rows - 3, w.ActualSize.Rows);
    }

    [Fact] // the default mode fits once and then holds — content changes do not resize the window
    public void Once_ContentGrow_HoldsFirstFit()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var content = new UIControls.StackPanel { Children = { Block(20, 2) } };
        var w = TrackedWindow(content, SizeToContentMode.Once);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        var before = w.ActualSize;

        content.Children.Add(Block(20, 3));
        Assert.True(host.RunUntilIdle());

        Assert.Equal(before, w.ActualSize);
    }

    [Fact] // an explicit axis is pinned; the other keeps tracking
    public void Always_ExplicitWidth_PinsWidthTracksHeight()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var content = new UIControls.StackPanel { Children = { Block(20, 2) } };
        var w = TrackedWindow(content, SizeToContentMode.Always);
        w.Width = 30;
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(30, w.ActualSize.Columns);
        var before = w.ActualSize;

        content.Children.Add(Block(40, 3)); // wider than the pinned width AND taller
        Assert.True(host.RunUntilIdle());

        Assert.Equal(30, w.ActualSize.Columns);          // pinned
        Assert.Equal(before.Rows + 3, w.ActualSize.Rows); // tracked
    }

    [Fact] // a resize drag writes explicit Width/Height — tracking stops on both axes (the user wins)
    public void Always_AfterExplicitResize_StopsTracking()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var content = new UIControls.StackPanel { Children = { Block(20, 2) } };
        var w = TrackedWindow(content, SizeToContentMode.Always);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        // What the resize-drag gesture does (Window.Interactions.cs): explicit size via SetCurrentValue.
        w.SetCurrentValue(UIElement.WidthProperty, 34);
        w.SetCurrentValue(UIElement.HeightProperty, 9);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(new Size(34, 9), w.ActualSize);

        content.Children.Add(Block(20, 3));
        Assert.True(host.RunUntilIdle());

        Assert.Equal(new Size(34, 9), w.ActualSize); // pinned by the drag
    }

    [Theory] // restore-from-maximize re-fits the tracked axes in BOTH modes (the SyncSurfaceSize relocation regression)
    [InlineData(SizeToContentMode.Once)]
    [InlineData(SizeToContentMode.Always)]
    public void MaximizeRestore_RefitsToContent(SizeToContentMode mode)
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var content = new UIControls.StackPanel { Children = { Block(20, 2) } };
        var w = TrackedWindow(content, mode);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        var fitted = w.ActualSize;

        w.WindowState = WindowState.Maximized;
        Assert.True(host.RunUntilIdle());
        Assert.Equal(new Size(60, 20), w.ActualSize); // fills the viewport

        w.WindowState = WindowState.Normal;
        Assert.True(host.RunUntilIdle());
        Assert.Equal(fitted, w.ActualSize); // back to the content fit, not stuck at the viewport
    }

    [Fact] // the chrome-less restore: nothing else invalidates layout, so FitToContentPending alone must arm the pass
    public void ChromelessOnce_MaximizeRestore_RefitsToContent()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var content = new UIControls.StackPanel { Children = { Block(20, 2) } };
        var w = TrackedWindow(content, SizeToContentMode.Once);
        w.WindowStyle = WindowStyle.None;
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        var fitted = w.ActualSize;

        w.WindowState = WindowState.Maximized;
        Assert.True(host.RunUntilIdle());
        Assert.Equal(new Size(60, 20), w.ActualSize);

        w.WindowState = WindowState.Normal;
        Assert.True(host.RunUntilIdle());
        Assert.Equal(fitted, w.ActualSize);
    }

    [Fact] // default §8.7 policy: an overhanging content grow does NOT move the window — the clip flag (badge) raises
    public void Always_GrowPastViewport_DefaultPolicy_ClipsInPlace()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var content = new UIControls.StackPanel { Children = { Block(20, 2) } };
        var w = TrackedWindow(content, SizeToContentMode.Always, top: 12); // near the bottom of the 20-row screen
        w.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.False(w.IsClippedByViewport);

        content.Children.Add(Block(20, 8)); // grows well past the bottom edge
        Assert.True(host.RunUntilIdle());

        Assert.Equal(12, w.Top);            // never auto-moved …
        Assert.True(w.IsClippedByViewport); // … the fit affordance is the user's lever
    }

    [Fact] // the TaskDialog opt-in: AutoFitToViewport shifts a grown window back into view, same frame
    public void Always_GrowPastViewport_AutoFit_ShiftsIntoView()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var content = new UIControls.StackPanel { Children = { Block(20, 2) } };
        var w = TrackedWindow(content, SizeToContentMode.Always, top: 12);
        w.AutoFitToViewport = true;
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        content.Children.Add(Block(20, 8));
        Assert.True(host.RunUntilIdle());

        Assert.Equal(20 - w.ActualSize.Rows, w.Top); // shifted up just enough to fit
        Assert.False(w.IsClippedByViewport);
    }

    [Fact] // Once windows must ALSO survive a transient terminal shrink — the fitted size is remembered, not ratcheted away
    public void Once_ViewportShrinkThenGrow_RecoversFittedSize()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var content = new UIControls.StackPanel { Children = { Block(50, 2) } };
        var w = TrackedWindow(content, SizeToContentMode.Once, left: 0, top: 0);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        var fitted = w.ActualSize;

        host.SendResize(40, 20); // the surface caps at the smaller viewport …
        Assert.True(host.RunUntilIdle());
        Assert.Equal(40, w.ActualSize.Columns);

        host.SendResize(60, 20); // … but the FIT was never lost — the re-grow restores it
        Assert.True(host.RunUntilIdle());
        Assert.Equal(fitted, w.ActualSize);
    }

    [Fact] // clearing a drag-pinned axis returns the window to its remembered content fit
    public void ClearingExplicitSize_ReturnsToFittedSize()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var content = new UIControls.StackPanel { Children = { Block(20, 2) } };
        var w = TrackedWindow(content, SizeToContentMode.Once);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        var fitted = w.ActualSize;

        w.SetCurrentValue(UIElement.WidthProperty, 40); // a resize drag pins the axis …
        Assert.True(host.RunUntilIdle());
        Assert.Equal(40, w.ActualSize.Columns);

        w.SetCurrentValue(UIElement.WidthProperty, null); // … clearing it falls back to the fit
        Assert.True(host.RunUntilIdle());
        Assert.Equal(fitted.Columns, w.ActualSize.Columns);
    }

    [Fact] // the public on-demand lever: FitToContent() re-fits a Once window to content that changed since the first fit
    public void Once_FitToContent_RefitsOnDemand()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var content = new UIControls.StackPanel { Children = { Block(20, 2) } };
        var w = TrackedWindow(content, SizeToContentMode.Once);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        var before = w.ActualSize;

        content.Children.Add(Block(20, 3));
        Assert.True(host.RunUntilIdle());
        Assert.Equal(before, w.ActualSize); // Once holds …

        w.FitToContent();
        Assert.True(host.RunUntilIdle());
        Assert.Equal(before.Rows + 3, w.ActualSize.Rows); // … until asked
    }

    [Fact] // stale fitted memory must not override Manual semantics when a window is re-shown non-tracking
    public void ReshownAsManual_IgnoresStaleFittedMemory()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var content = new UIControls.StackPanel { Children = { Block(20, 2) } };
        var w = TrackedWindow(content, SizeToContentMode.Once);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.True(w.ActualSize.Columns < 60); // content-fit, not viewport

        w.Close();
        Assert.True(host.RunUntilIdle());

        w.SizeToContent = SizeToContent.Manual; // no longer tracks; null Width/Height ⇒ viewport-sized
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(new Size(60, 20), w.ActualSize); // never snapped back to the dead incarnation's fit
    }

    [Fact] // a tracked window first shown Collapsed must fit its content when revealed (never stuck at 0×0)
    public void Once_ShownCollapsed_FitsWhenRevealed()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var content = new UIControls.StackPanel { Children = { Block(20, 2, "REVEALED") } };
        var w = TrackedWindow(content, SizeToContentMode.Once);
        w.Visibility = Visibility.Collapsed;
        w.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(default, w.ActualSize); // collapsed ⇒ empty

        w.Visibility = Visibility.Visible;
        Assert.True(host.RunUntilIdle());

        Assert.True(w.ActualSize.Columns >= 20 && w.ActualSize.Rows >= 2); // fit to the revealed content
        Assert.True(FrameContains(host, "REVEALED"));
    }

    [Fact] // Always windows recover a viewport-capped axis when the terminal grows back (no ratchet)
    public void Always_ViewportShrinkThenGrow_RecoversContentSize()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var content = new UIControls.StackPanel { Children = { Block(50, 2) } };
        var w = TrackedWindow(content, SizeToContentMode.Always, left: 0, top: 0);
        w.Show(wm);
        Assert.True(host.RunUntilIdle());

        var fitted = w.ActualSize; // content-driven, ≤ 60 columns

        host.SendResize(40, 20); // the tracked width caps at the smaller viewport
        Assert.True(host.RunUntilIdle());
        Assert.Equal(40, w.ActualSize.Columns);

        host.SendResize(60, 20); // …and releases back to the content size on re-grow
        Assert.True(host.RunUntilIdle());
        Assert.Equal(fitted, w.ActualSize);
    }
}
