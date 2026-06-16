// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Testing;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Windowing;

/// <summary>
/// P7-W1 — <see cref="Window"/> modeless lifecycle on the real frame loop: <see cref="Window.Show(WindowManager)"/>
/// installs a host surface above the root + activates; explicit size/position land; <see cref="Window.Close()"/>
/// tears the surface down and fires <c>Closed</c>; a <c>Closing</c> veto keeps it shown; <c>Owner</c> is immutable
/// once shown. (Chrome-click close + window focus/hit-testing arrive with the W3 topology swap.)
/// </summary>
public sealed class WindowTests
{
    private static (UITestHost Host, WindowManager Wm) ShownRoot()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(60, 20) });
        host.ShowRoot(new UIControls.StackPanel());
        Assert.True(host.RunUntilIdle());
        return (host, host.Application.WindowManager!);
    }

    [Fact]
    public void Show_InstallsHostSurfaceAboveRoot_AndActivates()
    {
        var (host, wm) = ShownRoot();
        using var _ = host;

        var window = new Window
        {
            Title = "Hi",
            Content = "Body",
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = 20,
            Height = 8,
            Left = 3,
            Top = 2
        };
        window.Show(wm);
        Assert.True(host.RunUntilIdle());

        Assert.Same(window, Assert.Single(wm.Windows));
        Assert.True(window.IsShown);
        Assert.True(window.IsActive);
        Assert.Same(wm, window.Manager);
        Assert.Equal(2, wm.Surfaces.Count);                       // root + window
        Assert.Equal(new Size(20, 8), window.ActualSize);
        Assert.Equal(3, wm.Windows[0].HostSurface!.Left);
        Assert.Equal(2, wm.Windows[0].HostSurface!.Top);
        Assert.Null(wm.RootSurface!.HostWindow);                  // the root stays chrome-less
        Assert.Same(window, wm.Surfaces[1].HostWindow);
    }

    [Fact] // a caret published inside a window is folded by the window's screen offset (window-local → absolute screen cell)
    public void Caret_InsideWindow_FoldsSurfaceOffset()
    {
        var (host, wm) = ShownRoot();
        using var _ = host;

        var box = new UIControls.TextBox { Text = "hi", Width = 14 };
        var window = new Window
        {
            Content = box,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = 24,
            Height = 6,
            Left = 12,
            Top = 5,
        };
        window.Show(wm);
        Assert.True(host.RunUntilIdle());

        box.Focus();
        box.CaretIndex = 2; // caret at the end of "hi"
        Assert.True(host.RunUntilIdle());

        Assert.True(host.FrameBuffer.CursorVisible);
        // The presenter publishes the caret in window-LOCAL cells; the WM folds the window's screen offset,
        // so the absolute cursor lands inside the window's screen region (Left=12, Top=5) — NOT near the
        // origin, which is the bug this guards (a window-local caret leaking through unoffset).
        Assert.True(host.FrameBuffer.CursorColumn >= window.Left,
            $"cursor column {host.FrameBuffer.CursorColumn} should be at/after the window Left {window.Left}");
        Assert.True(host.FrameBuffer.CursorRow >= window.Top,
            $"cursor row {host.FrameBuffer.CursorRow} should be at/after the window Top {window.Top}");
    }

    [Fact] // borderless chrome: the active window's title band is the accent fill — distinct from its body, and it re-colours when deactivated
    public void Chrome_ActiveTitleBand_DiffersFromBody_AndReColoursOnDeactivate()
    {
        var (host, wm) = ShownRoot();
        using var _ = host;

        var a = new Window
        {
            Title = "A",
            Content = "body",
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 5, Top = 3, Width = 20, Height = 8,
        };
        a.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.True(a.IsActive);

        // The title band (row Top) is the accent fill; the body row (Top+1) is the window surface — distinct
        // fills, no drawn border between them (the borderless tonal seam).
        var activeTitleBg = host.GetCell(7, 3).Style.Background; // inside the title band, left of the controls
        var bodyBg = host.GetCell(7, 4).Style.Background;        // the body row below it
        Assert.NotEqual(activeTitleBg, bodyBg);

        // A second window steals activation → A's band re-colours (accent → the recessed inactive surface).
        var b = new Window
        {
            Title = "B",
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 30, Top = 3, Width = 20, Height = 8,
        };
        b.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.False(a.IsActive);

        var inactiveTitleBg = host.GetCell(7, 3).Style.Background;
        Assert.NotEqual(activeTitleBg, inactiveTitleBg);
    }

    [Fact]
    public void Close_RemovesSurface_AndRaisesClosed()
    {
        var (host, wm) = ShownRoot();
        using var _ = host;

        var closed = 0;
        var window = new Window { Width = 10, Height = 4 };
        window.Closed += (_, _) => closed++;
        window.Show(wm);
        Assert.True(host.RunUntilIdle());

        window.Close();
        Assert.True(host.RunUntilIdle());

        Assert.False(window.IsShown);
        Assert.Empty(wm.Windows);
        Assert.Single(wm.Surfaces);   // just the root again
        Assert.Equal(1, closed);
        Assert.Null(window.Manager);
    }

    [Fact]
    public void Closing_Veto_KeepsWindowShown()
    {
        var (host, wm) = ShownRoot();
        using var _ = host;

        var window = new Window { Width = 10, Height = 4 };
        window.Closing += (_, e) => e.Cancel = true; // veto the cancelable Programmatic close
        window.Show(wm);
        Assert.True(host.RunUntilIdle());

        window.Close();
        Assert.True(host.RunUntilIdle());

        Assert.True(window.IsShown);
        Assert.Same(window, Assert.Single(wm.Windows));
    }

    [Fact]
    public void Owner_IsImmutableOnceShown()
    {
        var (host, wm) = ShownRoot();
        using var _ = host;

        var window = new Window { Width = 10, Height = 4 };
        window.Show(wm);
        Assert.True(host.RunUntilIdle());

        Assert.Throws<InvalidOperationException>(() => window.Owner = new Window());
    }
}
