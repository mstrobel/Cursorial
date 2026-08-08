using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;

namespace Cursorial.Tests.Drawing;

// Deferred §11 correctness hardening, batched: the wide-glyph composite-union right-edge degrade, the
// radial focal-point projection, CompositeParameters Mode normalization, and the Scene.Create ushort cap.
public class CorrectnessHardeningTests
{
    private static readonly Color Green = Color.FromRgb(0, 200, 0);
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);

    [Fact]
    public void WideGlyphAtCompositeUnionRightEdge_DegradesToBlank_NoStaleContinuation()
    {
        // A wide glyph whose continuation would land one column past the composite union used to strand a
        // stale WideContinuation outside the reset + MarkDirty range — a dirty-region hole a
        // RestrictToDirtyRegions renderer never revisits. It now degrades to a blank cell at the edge.
        //
        // The first composite always unions the full target, so the bisecting clip only narrows the union on
        // an INCREMENTAL frame: paint an empty scene first, then re-raster with a wide glyph at the clip edge.
        var scene = Scene.Create(6, 1);
        scene.Draw(_ => { });                                 // frame 1: empty

        var buffer = new CellBuffer(8, 1);                    // target wider than the clip → col 5 isn't the buffer edge
        var compositor = new SceneCompositor(CellStyle.Default);
        var clip = new Rect(0, 0, 5, 1);                      // ends at col 5, bisecting the wide glyph
        var layers = new[] { new SceneLayer(scene, new CompositeParameters(clip: clip)) };
        compositor.Composite(layers, buffer.AsView());        // frame 1: full-target union

        scene.Invalidate();
        scene.Draw(ctx => ctx.DrawText(4, 0, "中", Green));   // frame 2: WideLeft at col 4, continuation at col 5
        compositor.Composite(layers, buffer.AsView());        // incremental: union = clipped footprint [0,5)

        Assert.NotEqual(CellKind.WideLeft, buffer[4, 0].Kind);          // degraded, not a spilling wide glyph
        Assert.NotEqual(CellKind.WideContinuation, buffer[5, 0].Kind);  // no stale continuation past the union
    }

    [Fact]
    public void RadialGradient_FocalPointOutsideEllipse_StillVaries()
    {
        // A focal point well outside the unit ellipse used to make the ray cast degenerate (a flat outer
        // color). It's projected just inside (SVG rule), so the gradient still varies across the bounds.
        var brush = new RadialGradientBrush(Red, Blue, gradientOrigin: new RelativePoint(5.0, 5.0));
        var bounds = new Rect(0, 0, 10, 10);
        Assert.NotEqual(brush.ColorAt(5, 5, bounds), brush.ColorAt(0, 0, bounds));
    }

    [Fact]
    public void CompositeParameters_NullAndExplicitDefaultMode_AreEqual()
    {
        var withNull = new CompositeParameters(mode: null);
        var withDefault = new CompositeParameters(mode: BlendingModes.Default);
        Assert.Equal(withNull, withDefault);   // both mean source-over — normalized to compare equal
        Assert.Null(withDefault.Mode);         // the explicit Default was normalized to null
    }

    [Fact]
    public void SceneCreate_RejectsNonPositiveOrBeyondUshort()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Scene.Create(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => Scene.Create(70_000, 4));   // > ushort.MaxValue
        Assert.Throws<ArgumentOutOfRangeException>(() => Scene.Create(4, 70_000));
    }
}
