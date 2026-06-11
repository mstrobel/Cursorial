using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

public class GradientBrushTests
{
    private static readonly Color Black = Color.FromRgb(0, 0, 0);
    private static readonly Color White = Color.FromRgb(255, 255, 255);

    private static GradientStop[] BlackToWhite => [new(0.0, Black), new(1.0, White)];

    private static Color SampleAt(IBrush brush, int col, int row, Rect bounds) =>
        brush.ColorAt(col, row, bounds);

    [Fact]
    public void LinearHorizontal_RampsAcrossCellCenters()
    {
        var brush = new LinearGradientBrush(BlackToWhite);
        var bounds = new Rect(0, 0, 4, 1);

        // Cell-center sampling: t = (col + 0.5) / 4 → 0.125, 0.375, 0.625, 0.875 → grays.
        Assert.Equal(32, SampleAt(brush, 0, 0, bounds).Red);
        Assert.Equal(96, SampleAt(brush, 1, 0, bounds).Red);
        Assert.Equal(159, SampleAt(brush, 2, 0, bounds).Red);
        Assert.Equal(223, SampleAt(brush, 3, 0, bounds).Red);
    }

    [Fact]
    public void LinearVertical_RampsDownRows_ViaRelativePoints()
    {
        // Top → Bottom relative points produce the same ramp, oriented vertically.
        var brush = new LinearGradientBrush(BlackToWhite, startPoint: RelativePoint.Top, endPoint: RelativePoint.Bottom);
        var bounds = new Rect(0, 0, 1, 4);

        Assert.Equal(32, SampleAt(brush, 0, 0, bounds).Red);
        Assert.Equal(96, SampleAt(brush, 0, 1, bounds).Red);
        Assert.Equal(159, SampleAt(brush, 0, 2, bounds).Red);
        Assert.Equal(223, SampleAt(brush, 0, 3, bounds).Red);
    }

    [Fact]
    public void Gradient_AcceptsTupleEndpointsAtNullableCallSite()
    {
        // Tuple → RelativePoint → RelativePoint? must chain at the call site, with int and double literals.
        var withInts = new LinearGradientBrush(BlackToWhite, startPoint: (0, 0), endPoint: (0, 1));
        var withDoubles = new LinearGradientBrush(BlackToWhite, startPoint: (0.0, 0.0), endPoint: (0.0, 1.0));

        Assert.Equal(new RelativePoint(0, 1), withInts.EndPoint);
        Assert.Equal(new RelativePoint(0, 1), withDoubles.EndPoint);
    }

    [Fact]
    public void LinearEndpointsBeyondUnit_WithReflect_TileTheRamp()
    {
        // The animated-gradient building block: endpoints translated past 1 (start (1,0) → end (2,0))
        // with Reflect spread still paint the box — the vector's negative parameters mirror back in.
        var brush = new LinearGradientBrush(BlackToWhite,
                                            startPoint: new RelativePoint(1, 0), endPoint: new RelativePoint(2, 0),
                                            spread: GradientSpread.Reflect);
        var bounds = new Rect(0, 0, 4, 1);

        Assert.Equal(223, SampleAt(brush, 0, 0, bounds).Red);   // t=-0.875 → reflect → 0.875
        Assert.Equal(32, SampleAt(brush, 3, 0, bounds).Red);    // t=-0.125 → reflect → 0.125
    }

    [Fact]
    public void PremultipliedFadeToTransparent_KeepsHue_NoFringe()
    {
        var brush = new LinearGradientBrush([new(0.0, Color.FromRgb(255, 0, 0)), new(1.0, Color.Transparent)]);

        var c = SampleAt(brush, 0, 0, new Rect(0, 0, 1, 1));   // mid-gradient (t ≈ 0.5)

        Assert.Equal(255, c.Red);            // hue preserved — straight-alpha lerp would darken it toward 127
        Assert.InRange(c.Alpha, 120, 135);   // ~half alpha
    }

    [Fact]
    public void Spread_PadRepeatReflect_AtParameterPastOne()
    {
        var bounds = new Rect(0, 0, 2, 1);

        // Sampling column 3 over a 2-wide bounds gives t = 3.5/2 = 1.75 (past the end).
        Assert.Equal(255, SampleAt(new LinearGradientBrush(BlackToWhite, spread: GradientSpread.Pad), 3, 0, bounds).Red);      // clamp → 1.0
        Assert.Equal(191, SampleAt(new LinearGradientBrush(BlackToWhite, spread: GradientSpread.Repeat), 3, 0, bounds).Red);   // wrap → 0.75
        Assert.Equal(64, SampleAt(new LinearGradientBrush(BlackToWhite, spread: GradientSpread.Reflect), 3, 0, bounds).Red);   // mirror → 0.25
    }

    [Fact]
    public void Opacity_ScalesEveryStopAlpha()
    {
        var brush = new LinearGradientBrush(BlackToWhite, opacity: 0.5);
        Assert.InRange(SampleAt(brush, 1, 0, new Rect(0, 0, 4, 1)).Alpha, 120, 135);
    }

    [Fact]
    public void UnsortedStops_AreSortedByOffset()
    {
        var brush = new LinearGradientBrush([new(1.0, White), new(0.0, Black)]);

        Assert.Equal(0.0, brush.Stops[0].Offset);
        Assert.Equal(1.0, brush.Stops[1].Offset);
    }

    [Fact]
    public void Radial_DarkCenter_LightEdge()
    {
        var brush = new RadialGradientBrush(BlackToWhite);
        var bounds = new Rect(0, 0, 5, 5);

        Assert.Equal(0, SampleAt(brush, 2, 2, bounds).Red);     // center cell → first stop (black)
        Assert.Equal(255, SampleAt(brush, 0, 0, bounds).Red);   // far corner → clamped to last stop (white)
    }

    [Fact]
    public void Conic_SweepsClockwiseFromTop()
    {
        var brush = new ConicGradientBrush(BlackToWhite);
        var bounds = new Rect(0, 0, 3, 3);

        Assert.Equal(0, SampleAt(brush, 1, 0, bounds).Red);                            // 12 o'clock → t = 0 → black
        Assert.InRange(SampleAt(brush, 1, 2, bounds).Red, 120, 135);                   // 6 o'clock → t = 0.5 → mid-gray
        Assert.True(SampleAt(brush, 0, 1, bounds).Red > SampleAt(brush, 2, 1, bounds).Red); // 9 o'clock (0.75) brighter than 3 o'clock (0.25)
    }

    [Fact]
    public void LowAlphaPremultiplied_PreservesHue()
    {
        // The promised low-alpha precision case: a near-zero-alpha red faded to transparent must keep
        // its hue (Red == 255), not collapse toward black, even with alpha in the 1–4 range.
        var brush = new LinearGradientBrush([new(0.0, Color.FromRgba(255, 0, 0, 3)), new(1.0, Color.Transparent)]);

        var c = SampleAt(brush, 0, 0, new Rect(0, 0, 1, 1));   // mid-gradient
        Assert.Equal(255, c.Red);
        Assert.InRange(c.Alpha, 1, 3);
    }

    [Fact]
    public void EmptyStops_Throws()
    {
        Assert.Throws<ArgumentException>(() => new LinearGradientBrush(Array.Empty<GradientStop>()));
    }

    [Fact]
    public void NonFiniteOpacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LinearGradientBrush([new(0.0, Black), new(1.0, White)], opacity: double.NaN));
    }

    [Fact]
    public void RadialGradient_CellAspectRatio_CompressesTheVerticalRadiusForScreenCircles()
    {
        // Over a 20×20 region a point 4.5 rows above the center is at fractional radius ~0.9 once the vertical
        // radius is aspect-compressed (×0.5 → 5 rows), so it reads nearer the edge (whiter) than the uncorrected
        // ellipse (×1.0 → 10 rows, ~0.45). A true on-screen circle is shorter (in rows) than it is wide (in cols).
        var bounds = new Rect(0, 0, 20, 20);
        int corrected = new RadialGradientBrush(Black, White) { CellAspectRatio = 0.5 }.ColorAt(10, 5, bounds).Red;
        int plain = new RadialGradientBrush(Black, White).ColorAt(10, 5, bounds).Red;   // default 1.0 → box ellipse
        Assert.True(corrected > plain, $"aspect-corrected radial should be nearer the edge (whiter): {corrected} vs {plain}");
        Assert.True(plain < 200, $"the uncorrected box ellipse hasn't reached the edge color at this point ({plain})");
    }
}
