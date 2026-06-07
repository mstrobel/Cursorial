using Cursorial.Drawing;

namespace Cursorial.Tests.Drawing;

public class RelativePointTests
{
    [Fact]
    public void Constructor_StoresXY()
    {
        var p = new RelativePoint(0.25, 0.75);
        Assert.Equal(0.25, p.X);
        Assert.Equal(0.75, p.Y);
    }

    [Fact]
    public void Default_IsTopLeft()
    {
        Assert.Equal(RelativePoint.TopLeft, default(RelativePoint));
        Assert.Equal(0.0, RelativePoint.TopLeft.X);
        Assert.Equal(0.0, RelativePoint.TopLeft.Y);
    }

    [Theory]
    [InlineData(0.5, 0.0)]   // Top
    [InlineData(1.0, 0.0)]   // TopRight
    [InlineData(0.0, 0.5)]   // Left
    [InlineData(0.5, 0.5)]   // Center
    [InlineData(1.0, 0.5)]   // Right
    [InlineData(0.0, 1.0)]   // BottomLeft
    [InlineData(0.5, 1.0)]   // Bottom
    [InlineData(1.0, 1.0)]   // BottomRight
    public void NamedConstants_HaveExpectedCoordinates(double x, double y)
    {
        // Each named compass point is the (x, y) it claims.
        RelativePoint[] all =
        [
            RelativePoint.Top, RelativePoint.TopRight, RelativePoint.Left, RelativePoint.Center,
            RelativePoint.Right, RelativePoint.BottomLeft, RelativePoint.Bottom, RelativePoint.BottomRight
        ];
        Assert.Contains(new RelativePoint(x, y), all);
    }

    [Fact]
    public void TupleConversion_IsImplicit()
    {
        RelativePoint p = (0.3, 0.6);
        Assert.Equal(new RelativePoint(0.3, 0.6), p);
    }

    [Fact]
    public void ValuesOutsideUnitRange_AreAllowed()
    {
        // The animation use case (Consolonia-style scrolling gradient) translates endpoints past 1.
        var p = new RelativePoint(2.5, -1.0);
        Assert.Equal(2.5, p.X);
        Assert.Equal(-1.0, p.Y);
    }
}
