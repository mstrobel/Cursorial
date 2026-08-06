using Cursorial.Media;

namespace Cursorial.Tests.Output;

public class ColorLerpTests
{
    private static void AssertRgba(Color c, byte r, byte g, byte b, byte a)
    {
        Assert.Equal(ColorKind.Rgb, c.Kind);
        Assert.Equal(r, c.Red);
        Assert.Equal(g, c.Green);
        Assert.Equal(b, c.Blue);
        Assert.Equal(a, c.Alpha);
    }

    // Oracle values: premultiplied-sRGB lerp, banker's rounding (matches Math.Round default).
    [Fact]
    public void OpaqueColors_AreStraightChannelLerps()
    {
        AssertRgba(Color.Lerp(Color.FromRgb(0, 0, 0), Color.FromRgb(100, 200, 40), 0.5), 50, 100, 20, 255);
        AssertRgba(Color.Lerp(Color.FromRgb(0, 0, 0), Color.FromRgb(100, 200, 40), 0.25), 25, 50, 10, 255);
        AssertRgba(Color.Lerp(Color.FromRgb(255, 0, 0), Color.FromRgb(0, 0, 255), 0.5), 128, 0, 128, 255);
    }

    [Fact]
    public void Endpoints_ReturnFromAndTo()
    {
        var from = Color.FromRgb(10, 20, 30);
        var to = Color.FromRgb(200, 100, 50);
        AssertRgba(Color.Lerp(from, to, 0.0), 10, 20, 30, 255);
        AssertRgba(Color.Lerp(from, to, 1.0), 200, 100, 50, 255);
    }

    [Fact]
    public void PremultipliedAlpha_KeepsTheOpaqueNeighborsHue()
    {
        // Transparent red → opaque blue: the transparent end contributes no color, so the midpoint is
        // pure blue at half alpha (NOT a muddy purple, which straight-alpha lerp would give).
        var c = Color.Lerp(Color.FromRgba(255, 0, 0, 0), Color.FromRgba(0, 0, 255, 255), 0.5);
        AssertRgba(c, 0, 0, 255, 128);
    }

    [Fact]
    public void OutOfRangeProgress_ExtrapolatesAndClampsChannels()
    {
        AssertRgba(Color.Lerp(Color.FromRgb(0, 0, 0), Color.FromRgb(100, 100, 100), 1.5), 150, 150, 150, 255);
        AssertRgba(Color.Lerp(Color.FromRgb(0, 0, 0), Color.FromRgb(100, 100, 100), -0.5), 0, 0, 0, 255);
    }

    [Fact]
    public void NonRgbEndpoints_SnapToTheNearer()
    {
        var pal = Color.FromPalette(1);
        var rgb = Color.FromRgb(255, 255, 255);

        Assert.Equal(pal, Color.Lerp(pal, rgb, 0.3));    // t < 0.5 → from
        Assert.Equal(rgb, Color.Lerp(pal, rgb, 0.7));    // t ≥ 0.5 → to
        Assert.Equal(Color.Default, Color.Lerp(Color.Default, rgb, 0.4));
    }
}
