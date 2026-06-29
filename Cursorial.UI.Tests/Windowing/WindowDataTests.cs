using Cursorial.Drawing;
using Cursorial.Rendering;
using Cursorial.UI;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Windowing;

/// <summary>P7-W1 — the windowing value types: <see cref="WindowShadow"/> and the
/// <see cref="WindowChrome"/> role attached property.</summary>
public sealed class WindowDataTests
{
    [Fact] // WindowShadow.None / Default / IsNone
    public void WindowShadow_NoneAndDefault()
    {
        Assert.True(WindowShadow.None.IsNone);
        Assert.Equal(default, WindowShadow.None.GetMargins());

        var d = WindowShadow.Default;
        Assert.False(d.IsNone);
        Assert.Equal(2, d.Geometry.Radius);
        Assert.Equal(ShadowEdges.Bottom | ShadowEdges.Right, d.Geometry.Edges);
    }

    [Fact] // GetMargins grows a casting L/R edge by the full radius and a casting T/B edge by ~half (cell aspect)
    public void WindowShadow_GetMargins_GrowsCastEdges()
    {
        // Default = Drop(radius: 5, edges: Bottom | Right) → rv = (5+1)/2 = 3.
        var margins = WindowShadow.Default.GetMargins();
        Assert.Equal(new Margins(left: 0, top: 0, right: 2, bottom: 1), margins);
    }

    [Fact] // WindowChrome.HitTestRole attached property round-trips (default None)
    public void WindowChrome_HitTestRole_RoundTrips()
    {
        var element = new UIControls.Border();
        Assert.Equal(WindowHitTestRole.None, WindowChrome.GetHitTestRole(element));

        WindowChrome.SetHitTestRole(element, WindowHitTestRole.Drag | WindowHitTestRole.Maximize);
        Assert.Equal(WindowHitTestRole.Drag | WindowHitTestRole.Maximize, WindowChrome.GetHitTestRole(element));
    }
}
