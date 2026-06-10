using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

// Drop shadow: translucent background cells outside the element; the compositor darkens the target beneath.
// Inner shadow: read-modify-write that darkens each cell's own fill at draw time (stores opaque), preserving
// the glyph. Tests run over a NON-black base so darkening is observable and the inner/target distinction shows.
public class ShadowTests
{
    private static readonly Color Black = Color.FromRgb(0, 0, 0);
    private static readonly Color White = Color.FromRgb(255, 255, 255);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);

    [Fact]
    public void DropShadow_FallsOffMonotonically_OverNonBlackBase()
    {
        var b = DrawHarness.Render(10, 8,
            ctx => ctx.DrawDropShadow(new Rect(2, 2, 4, 3), ShadowGeometry.Drop(radius: 2, offset: 1, strength: 0.5), Black),
            baseBackground: White);

        // Cells stepping away from the element's lower-right cast get progressively lighter (less darkened).
        int near = b[6, 5].Style.Background.Red;
        int mid = b[7, 5].Style.Background.Red;
        int far = b[8, 5].Style.Background.Red;
        Assert.True(near < mid && mid < far, $"expected monotonic falloff, got {near} < {mid} < {far}");
        Assert.True(far < 255, "the outermost shadow cell still darkens the base");
    }

    [Fact]
    public void DropShadow_DoesNotPaintInsideElement()
    {
        var b = DrawHarness.Render(10, 8,
            ctx => ctx.DrawDropShadow(new Rect(2, 2, 4, 3), ShadowGeometry.Drop(radius: 2), Black),
            baseBackground: White);
        Assert.Equal(White, b[3, 3].Style.Background);   // inside the element footprint → untouched (element occludes)
    }

    [Fact]
    public void InnerShadow_DarkensOwnFill_AtEdges_PreservesInterior()
    {
        // The defect this pins: inner shadow must blend against the element's FILL, not the composite target.
        var fill = Color.FromRgb(150, 150, 150);
        var b = DrawHarness.Render(8, 6, ctx =>
        {
            ctx.FillRectangle(new Rect(0, 0, 8, 6), fill);
            ctx.DrawInnerShadow(new Rect(0, 0, 8, 6), ShadowGeometry.Inner(radius: 1, strength: 0.5), Black);
        }, baseBackground: Blue);

        Assert.True(b[0, 0].Style.Background.Red < fill.Red, "edge cell darkened below the fill");
        Assert.Equal(fill, b[3, 3].Style.Background);   // interior beyond the radius keeps the untouched fill
    }

    [Fact]
    public void InnerShadow_PreservesGlyph()
    {
        var fill = Color.FromRgb(150, 150, 150);
        var b = DrawHarness.Render(8, 4, ctx =>
        {
            ctx.FillRectangle(new Rect(0, 0, 8, 4), fill);
            ctx.Set(0, 0, "A", Style.Default.WithForeground(White).WithBackground(fill));
            ctx.DrawInnerShadow(new Rect(0, 0, 8, 4), ShadowGeometry.Inner(radius: 1, strength: 0.5), Black);
        });
        Assert.Equal("A", b[0, 0].Grapheme);   // the glyph survives the read-modify-write
    }

    [Fact]
    public void InnerShadow_LeftEdgeOnly_DarkensOnlyLeft()
    {
        var fill = Color.FromRgb(150, 150, 150);
        var b = DrawHarness.Render(8, 4, ctx =>
        {
            ctx.FillRectangle(new Rect(0, 0, 8, 4), fill);
            ctx.DrawInnerShadow(new Rect(0, 0, 8, 4), ShadowGeometry.Inner(radius: 1, strength: 0.5, edges: ShadowEdges.Left), Black);
        });
        Assert.True(b[0, 1].Style.Background.Red < fill.Red, "left edge darkened");
        Assert.Equal(fill, b[7, 1].Style.Background);   // right edge untouched (not a casting edge)
    }

    [Fact]
    public void Shadow_NoneEdges_IsNoOp()
    {
        var b = DrawHarness.Render(8, 6,
            ctx => ctx.DrawDropShadow(new Rect(2, 2, 3, 2), ShadowGeometry.Drop(edges: ShadowEdges.None), Black),
            baseBackground: White);
        Assert.Equal(White, b[5, 4].Style.Background);   // nothing cast
    }

    [Fact]
    public void DropShadow_FactoryEdges_CastsOnlyChosenSides()
    {
        // The Drop factory's edges argument restricts which sides cast.
        var b = DrawHarness.Render(8, 6,
            ctx => ctx.DrawDropShadow(new Rect(2, 1, 3, 2), ShadowGeometry.Drop(edges: ShadowEdges.Bottom | ShadowEdges.Right), Black),
            baseBackground: White);
        Assert.True(b[5, 2].Style.Background.Red < 255, "lower-right cast");   // bottom/right side darkened
        Assert.Equal(White, b[1, 1].Style.Background);                          // left side not cast
    }

    [Fact]
    public void DropShadow_BottomRight_NoCornerOverhang()
    {
        // A bottom+right shadow must hug those two sides — never overhang above the top-right corner or left
        // of the bottom-left corner (the band-past-the-corner bug).
        var b = DrawHarness.Render(12, 10,
            ctx => ctx.DrawDropShadow(new Rect(3, 3, 5, 4),
                                      ShadowGeometry.Drop(radius: 1, offset: 0, edges: ShadowEdges.Bottom | ShadowEdges.Right), Black),
            baseBackground: White);
        Assert.True(b[8, 5].Style.Background.Red < 255, "right band casts");    // (8,5): right of element, within its rows
        Assert.True(b[5, 7].Style.Background.Red < 255, "bottom band casts");   // (5,7): below element, within its cols
        Assert.Equal(White, b[8, 2].Style.Background);   // above the top-right corner → no overhang
        Assert.Equal(White, b[2, 7].Style.Background);   // left of the bottom-left corner → no overhang
    }

    [Fact]
    public void DefaultShadows_DoNotThrow()
    {
        DrawHarness.Render(8, 6, ctx =>
        {
            ctx.DrawDropShadow(new Rect(2, 2, 3, 2));
            ctx.FillRectangle(new Rect(2, 2, 3, 2), Color.FromRgb(150, 150, 150));
            ctx.DrawInnerShadow(new Rect(2, 2, 3, 2));
        });
    }
}
