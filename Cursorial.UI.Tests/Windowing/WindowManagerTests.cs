// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Testing;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Windowing;

/// <summary>
/// P7-W0 — the <see cref="WindowManager"/> as the host's layout+render+window system, replacing the
/// single-root stand-ins. The whole UI suite already runs through it (every UITestHost frame); these
/// pin the W0-specific surface wiring: <c>ShowRoot</c> creates exactly one chrome-less root surface
/// (<c>HostWindow == null</c>) wrapping the root's tree, a viewport resize re-fits it, and clearing the
/// root tears the surface down.
/// </summary>
public sealed class WindowManagerTests
{
    [Fact] // ShowRoot installs one chrome-less root surface wrapping the root's RenderTree
    public void ShowRoot_InstallsSingleChromelessRootSurface()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 12) });
        var root = new UIControls.StackPanel();
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        var wm = host.Application.WindowManager!;
        var surface = Assert.Single(wm.Surfaces);
        Assert.Same(surface, wm.RootSurface);
        Assert.Same(root, surface.Root);
        Assert.Null(surface.HostWindow);                  // the chrome-less application root (also the inline case)
        Assert.Same(surface.RenderTree, wm.Tree);         // the W0-compat single-tree accessor
        Assert.Equal(new Size(40, 12), surface.Size);     // fills the viewport
        Assert.True(surface.Contains(0, 0));
        Assert.True(surface.Contains(39, 11));
        Assert.False(surface.Contains(40, 12));
    }

    [Fact] // a viewport resize re-fits the root surface (and the next frame stays clean)
    public void ViewportResize_RefitsRootSurface()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 12) });
        host.ShowRoot(new UIControls.StackPanel());
        Assert.True(host.RunUntilIdle());

        host.SendResize(60, 20);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(new Size(60, 20), host.Application.WindowManager!.RootSurface!.Size);
    }

    [Fact] // clearing the root tears the surface down (scenes back to the pool)
    public void ClearingRoot_RemovesTheSurface()
    {
        using var host = UITestHost.Create();
        host.ShowRoot(new UIControls.StackPanel());
        Assert.True(host.RunUntilIdle());
        Assert.Single(host.Application.WindowManager!.Surfaces);

        host.Application.RootElement = null;
        Assert.True(host.RunUntilIdle());

        Assert.Empty(host.Application.WindowManager!.Surfaces);
        Assert.Null(host.Application.WindowManager!.RootSurface);
    }
}
