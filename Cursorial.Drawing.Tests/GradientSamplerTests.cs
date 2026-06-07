using Cursorial.Drawing;
using Cursorial.Output;

namespace Cursorial.Tests.Drawing;

public class GradientSamplerTests
{
    private static readonly Color Black = Color.FromRgb(0, 0, 0);
    private static readonly Color White = Color.FromRgb(255, 255, 255);

    private static GradientStop[] BlackToWhite => [new(0.0, Black), new(1.0, White)];

    private static GradientSampler Linear(BrushExtent extent, GradientSpread spread = GradientSpread.Pad, double opacity = 1.0) =>
        new(new GradientData(GradientKind.Linear, BlackToWhite, spread, opacity), extent);

    [Fact]
    public void LinearHorizontal_RampsAcrossCellCenters()
    {
        var s = Linear(new BrushExtent(0, 0, 4, 1));

        // Cell-center sampling: t = (col + 0.5) / 4 → 0.125, 0.375, 0.625, 0.875 → grays.
        Assert.Equal(32, s.Sample(0, 0).Red);
        Assert.Equal(96, s.Sample(1, 0).Red);
        Assert.Equal(159, s.Sample(2, 0).Red);
        Assert.Equal(223, s.Sample(3, 0).Red);
    }

    [Fact]
    public void PremultipliedFadeToTransparent_KeepsHue_NoFringe()
    {
        var stops = new GradientStop[] { new(0.0, Color.FromRgb(255, 0, 0)), new(1.0, Color.Transparent) };
        var s = new GradientSampler(new GradientData(GradientKind.Linear, stops), new BrushExtent(0, 0, 1, 1));

        var c = s.Sample(0, 0);   // mid-gradient (t ≈ 0.5)

        Assert.Equal(255, c.Red);            // hue preserved — straight-alpha lerp would darken it toward 127
        Assert.InRange(c.Alpha, 120, 135);   // ~half alpha
    }

    [Fact]
    public void Spread_PadRepeatReflect_AtParameterPastOne()
    {
        // Sampling column 3 over a 2-wide extent gives t = 3.5/2 = 1.75 (past the end).
        Assert.Equal(255, Linear(new BrushExtent(0, 0, 2, 1), GradientSpread.Pad).Sample(3, 0).Red);      // clamp → 1.0
        Assert.Equal(191, Linear(new BrushExtent(0, 0, 2, 1), GradientSpread.Repeat).Sample(3, 0).Red);   // wrap → 0.75
        Assert.Equal(64, Linear(new BrushExtent(0, 0, 2, 1), GradientSpread.Reflect).Sample(3, 0).Red);   // mirror → 0.25
    }

    [Fact]
    public void Opacity_ScalesEveryStopAlpha()
    {
        var s = Linear(new BrushExtent(0, 0, 4, 1), opacity: 0.5);
        Assert.InRange(s.Sample(1, 0).Alpha, 120, 135);
    }

    [Fact]
    public void UnsortedStops_AreSortedByOffset()
    {
        var data = new GradientData(GradientKind.Linear, [new(1.0, White), new(0.0, Black)]);

        Assert.Equal(0.0, data.Stops[0].Offset);
        Assert.Equal(1.0, data.Stops[1].Offset);
    }

    [Fact]
    public void Radial_DarkCenter_LightEdge()
    {
        var s = new GradientSampler(new GradientData(GradientKind.Radial, BlackToWhite), new BrushExtent(0, 0, 5, 5));

        Assert.Equal(0, s.Sample(2, 2).Red);     // center cell → first stop (black)
        Assert.Equal(255, s.Sample(0, 0).Red);   // far corner → clamped to last stop (white)
    }

    [Fact]
    public void Conic_SweepsClockwiseFromTop()
    {
        var s = new GradientSampler(new GradientData(GradientKind.Conic, BlackToWhite), new BrushExtent(0, 0, 3, 3));

        Assert.Equal(0, s.Sample(1, 0).Red);                  // 12 o'clock → t = 0 → black
        Assert.InRange(s.Sample(1, 2).Red, 120, 135);         // 6 o'clock → t = 0.5 → mid-gray
        Assert.True(s.Sample(0, 1).Red > s.Sample(2, 1).Red); // 9 o'clock (0.75) brighter than 3 o'clock (0.25)
    }

    [Fact]
    public void LowAlphaPremultiplied_PreservesHue()
    {
        // The promised low-alpha precision case: a near-zero-alpha red faded to transparent must keep
        // its hue (Red == 255), not collapse toward black, even with alpha in the 1–4 range.
        var stops = new GradientStop[] { new(0.0, Color.FromRgba(255, 0, 0, 3)), new(1.0, Color.Transparent) };
        var s = new GradientSampler(new GradientData(GradientKind.Linear, stops), new BrushExtent(0, 0, 1, 1));

        var c = s.Sample(0, 0);   // mid-gradient
        Assert.Equal(255, c.Red);
        Assert.InRange(c.Alpha, 1, 3);
    }

    [Fact]
    public void EmptyStops_Throws()
    {
        Assert.Throws<ArgumentException>(() => new GradientData(GradientKind.Linear, Array.Empty<GradientStop>()));
    }

    [Fact]
    public void NonFiniteOpacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GradientData(GradientKind.Linear, [new(0.0, Black), new(1.0, White)], opacity: double.NaN));
    }
}
