// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;

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
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 12) });
        var root = new UIControls.StackPanel();
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        var wm = host.Application.WindowManager!;
        var surface = Assert.Single(wm.Surfaces);
        Assert.Same(surface, wm.RootSurface);
        // The WM hosts the assigned root inside a framework RootElementHost (the modal-blocked look's
        // carrier, W4-c); the public API keeps reflecting the assigned element.
        var rootHost = Assert.IsType<RootElementHost>(surface.Root);
        Assert.Same(root, rootHost.Content);
        Assert.Same(root, host.Application.RootElement);
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
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 12) });
        host.ShowRoot(new UIControls.StackPanel());
        Assert.True(host.RunUntilIdle());

        host.SendResize(60, 20);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(new Size(60, 20), host.Application.WindowManager!.RootSurface!.Size);
    }

    [Fact] // clearing the root tears the surface down (scenes back to the pool)
    public void ClearingRoot_RemovesTheSurface()
    {
        using var host = UIHeadlessHost.Create();
        host.ShowRoot(new UIControls.StackPanel());
        Assert.True(host.RunUntilIdle());
        Assert.Single(host.Application.WindowManager!.Surfaces);

        host.Application.RootElement = null;
        Assert.True(host.RunUntilIdle());

        Assert.Empty(host.Application.WindowManager!.Surfaces);
        Assert.Null(host.Application.WindowManager!.RootSurface);
    }

    /// <summary>
    /// <see cref="WindowManager.SampleCell"/> reports what is on the screen, not the compositor's internal
    /// encoding of it. A layer whose scene is an opacity-group <b>surface</b> stores a replacing blank — a
    /// blank that must REPLACE rather than merge when the surface is blended down — as a private marker
    /// grapheme; raw, the inspector would show a glyph on a cell the terminal draws empty.
    /// </summary>
    [Fact]
    public void SampleCell_ResolvesAGroupSurfacesReplacingBlank()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 12) });
        host.ShowRoot(new UIControls.StackPanel());
        Assert.True(host.RunUntilIdle());

        var wm = host.Application.WindowManager!;
        var surface = Assert.Single(wm.Surfaces);

        var layers = new List<SceneLayer>();
        surface.CollectLayers(layers);
        var zone = layers[0];

        // Make that zone's scene a group surface carrying the marker: a wide glyph cut in half by its
        // member's clip degrades to a blank that still has to replace, which intermediate mode marks.
        var glyphStyle = Cursorial.Output.Style.Default
                                  .WithForeground(Color.FromRgb(0, 0, 255))
                                  .WithBackground(Color.FromRgb(255, 0, 0));

        using var member = Scene.Create(zone.Scene.Columns, zone.Scene.Rows);
        member.Draw(ctx => ctx.Set(0, 0, "漢", glyphStyle));
        zone.Scene.CompositeInto(SceneCompositor.ForIntermediate(),
                                 [new SceneLayer(member, new CompositeParameters(clip: new Rect(0, 0, 1, 1)))]);

        Assert.Equal("\uFFFF", zone.Scene.GetCell(0, 0).Grapheme);   // the scene really does hold the marker

        var samples = wm.SampleCell(zone.Parameters.OffsetColumn, zone.Parameters.OffsetRow);

        Assert.All(samples, sample => Assert.NotEqual("\uFFFF", sample.Cell?.Grapheme));

        // Blank, but with the cut glyph's own colors — the sample is the cell the flat path would have
        // written, which is exactly what the composition inspector is for.
        Assert.NotNull(samples[0].Cell);
        var cell = samples[0].Cell!.Value;
        Assert.Null(cell.Grapheme);
        Assert.Equal(CellKind.Single, cell.Kind);
        Assert.Equal(Color.FromRgb(255, 0, 0), cell.Style.Background);
        Assert.Equal(Color.FromRgb(0, 0, 255), cell.Style.Foreground);
    }
}
