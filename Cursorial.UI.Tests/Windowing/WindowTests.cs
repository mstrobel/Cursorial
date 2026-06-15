// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using System;

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
            Top = 2,
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
