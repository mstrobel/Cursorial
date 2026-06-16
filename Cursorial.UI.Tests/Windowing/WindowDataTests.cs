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
        Assert.Equal(1, d.Geometry.Radius);
        Assert.Equal(1, d.Geometry.OffsetColumn);
    }

    [Fact] // GetMargins grows the cast (lower-right) edges by offset+radius, the opposite edges by 0
    public void WindowShadow_GetMargins_GrowsCastEdges()
    {
        var margins = WindowShadow.Default.GetMargins(); // Drop(radius:1, offset:1)
        Assert.Equal(new Margins(left: 0, top: 0, right: 2, bottom: 2), margins);
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
