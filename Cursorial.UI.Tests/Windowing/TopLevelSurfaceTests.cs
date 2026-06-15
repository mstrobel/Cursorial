using System.Collections.Generic;

using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.UI;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Windowing;

/// <summary>
/// P7-W0 — <see cref="TopLevelSurface"/>: the surface primitive wrapping an S1 <c>RenderTree</c> +
/// <c>LayoutManager</c> at a screen offset. Proves the two contracts the window manager relies on: every
/// layer the surface contributes is translated to the surface's screen offset (so window/popup placement
/// is a composite-offset change, §8.5), and the surface's content rect drives screen-point containment +
/// hit testing (offset-correct, exclusive on the far edge).
/// </summary>
public sealed class TopLevelSurfaceTests
{
    private static TopLevelSurface CreateSurface(int left, int top, Size size)
    {
        var root = new UIControls.Border { Background = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)) };
        // The internal ctor (InternalsVisibleTo) — the window manager is the production constructor (P7-W0 core).
        var surface = new TopLevelSurface(root, new ScenePool(), OutputCapabilities.None, guard: null)
        {
            Left = left,
            Top = top,
            Size = size,
        };
        surface.RunLayoutPass();
        surface.RunRenderPass();
        return surface;
    }

    [Fact] // CollectLayers translates every layer by the surface's screen offset (the §8.5 placement contract)
    public void CollectLayers_TranslatesLayersByScreenOffset()
    {
        var atOrigin = CreateSurface(0, 0, new Size(10, 4));
        var shifted = CreateSurface(5, 3, new Size(10, 4));

        var originLayers = new List<SceneLayer>();
        atOrigin.CollectLayers(originLayers);
        var shiftedLayers = new List<SceneLayer>();
        shifted.CollectLayers(shiftedLayers);

        Assert.NotEmpty(originLayers);                       // the root zone always contributes a base layer
        Assert.Equal(originLayers.Count, shiftedLayers.Count); // identical content ⇒ identical layer count
        for (var i = 0; i < originLayers.Count; i++)
        {
            Assert.Equal(originLayers[i].Parameters.OffsetColumn + 5, shiftedLayers[i].Parameters.OffsetColumn);
            Assert.Equal(originLayers[i].Parameters.OffsetRow + 3, shiftedLayers[i].Parameters.OffsetRow);
        }
    }

    [Fact] // Contains is offset-correct and exclusive on the far edge
    public void Contains_HonorsScreenOffset_AndIsHalfOpen()
    {
        var surface = CreateSurface(5, 3, new Size(10, 4)); // covers columns 5..14, rows 3..6

        Assert.True(surface.Contains(5, 3));    // top-left corner
        Assert.True(surface.Contains(14, 6));   // bottom-right cell (inclusive)
        Assert.False(surface.Contains(4, 3));   // one column left of the surface
        Assert.False(surface.Contains(5, 2));   // one row above
        Assert.False(surface.Contains(15, 3));  // one past the right edge (half-open)
        Assert.False(surface.Contains(5, 7));   // one past the bottom edge
    }

    [Fact] // HitTest maps the screen point into surface-local coordinates; outside the content rect is null
    public void HitTest_MapsScreenToSurfaceLocal_AndMissesOutside()
    {
        var surface = CreateSurface(5, 3, new Size(10, 4));

        Assert.Same(surface.Root, surface.HitTest(5, 3)); // the filled Border occupies the whole content rect
        Assert.Null(surface.HitTest(4, 3));               // outside the surface → no hit
    }
}
