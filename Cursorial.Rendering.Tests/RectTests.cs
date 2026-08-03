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
}
