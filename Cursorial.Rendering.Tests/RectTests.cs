using Cursorial.Rendering;

namespace Cursorial.Tests.Rendering;

public class RectTests
{
    [Fact]
    public void Intersection_OfNonOriginRects_IsTheOverlap()
    {
        // Regression: the end coordinates were fed to the (columns, rows) SIZE parameters, which
        // is only coincidentally correct for origin-anchored rects — a selection tint anchored
        // mid-viewport came back viewport-wide (the highlight smeared to the right edge).
        var a = new Rect(60, 5, 17, 5);   // [60,77) × [5,10)
        var b = new Rect(0, 0, 78, 10);   // [0,78) × [0,10)

        Assert.Equal(new Rect(60, 5, 17, 5), a.Intersection(b));
        Assert.Equal(new Rect(60, 5, 17, 5), b.Intersection(a));
    }

    [Fact]
    public void Intersection_PartialOverlap_ClipsBothEdges()
    {
        var a = new Rect(4, 2, 10, 6);    // [4,14) × [2,8)
        var b = new Rect(8, 5, 10, 6);    // [8,18) × [5,11)

        Assert.Equal(new Rect(8, 5, 6, 3), a.Intersection(b));
        Assert.Equal(new Rect(8, 5, 6, 3), b.Intersection(a));
    }

    [Fact]
    public void Intersection_Disjoint_IsEmpty()
    {
        var a = new Rect(0, 0, 5, 5);
        var b = new Rect(5, 0, 5, 5);     // shares only the seam — half-open, no overlap

        Assert.Equal(Rect.Empty, a.Intersection(b));
        Assert.Equal(Rect.Empty, b.Intersection(a));
    }

    [Fact]
    public void Union_Disjoint_SpansBoth()
    {
        var a = new Rect(0, 0, 3, 1);     // [0,3)  × [0,1)
        var b = new Rect(6, 1, 4, 1);     // [6,10) × [1,2)

        Assert.Equal(new Rect(0, 0, 10, 2), a.Union(b));
        Assert.Equal(new Rect(0, 0, 10, 2), b.Union(a));
    }

    [Fact]
    public void Union_Nested_IsTheOuterRect()
    {
        var outer = new Rect(4, 2, 10, 6);
        var inner = new Rect(6, 3, 2, 2);

        Assert.Equal(outer, outer.Union(inner));
        Assert.Equal(outer, inner.Union(outer));
    }

    [Fact]
    public void Union_OfNonOriginRects_DoesNotReachBackToTheOrigin()
    {
        var a = new Rect(37, 4, 5, 1);    // [37,42) × [4,5)
        var b = new Rect(40, 4, 5, 1);    // [40,45) × [4,5)

        Assert.Equal(new Rect(37, 4, 8, 1), a.Union(b));
    }

    [Fact]
    public void Union_WithEmpty_IsTheIdentity()
    {
        // An empty rect covers no cells, so it has no position to contribute. Rect.Empty sits at the
        // origin; if it counted, an accumulator seeded with it would come back stretched to (0, 0) on
        // its very first step — 42 columns wide here instead of 5.
        var r = new Rect(37, 4, 5, 1);

        Assert.Equal(r, Rect.Empty.Union(r));
        Assert.Equal(r, r.Union(Rect.Empty));
        Assert.Equal(Rect.Empty, Rect.Empty.Union(Rect.Empty));
    }

    [Fact]
    public void Union_WithZeroWidthRect_IsTheIdentity()
    {
        // Emptiness is EITHER extent being zero, not just the default rect: a zero-COLUMN block sitting
        // at column 90 must not widen a union that ends at column 42.
        var r = new Rect(37, 4, 5, 1);
        var flat = new Rect(90, 4, 0, 1);

        Assert.Equal(r, r.Union(flat));
        Assert.Equal(r, flat.Union(r));
    }
}
