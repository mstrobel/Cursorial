using Cursorial.Media;

namespace Cursorial.Tests.Output;

// FromHsl/FromHsv channel conversion must round (not truncate) and clamp (not wrap mod 256).
public class ColorHslTests
{
    [Fact]
    public void HslRoundTrip_IsExact_AcrossTheGamut()
    {
        // Truncating the channel cast drifted ~40% of colors by ±1 on a round-trip; rounding makes it exact.
        for (int r = 0; r <= 255; r += 17)
        for (int g = 0; g <= 255; g += 17)
        for (int b = 0; b <= 255; b += 17)
        {
            var c = Color.FromRgb((byte) r, (byte) g, (byte) b);
            var (h, s, l) = c.ToHsl();
            var back = Color.FromHsl(h, s, l);
            Assert.Equal(c, back);
        }
    }

    [Fact]
    public void HsvRoundTrip_IsExact_AcrossTheGamut()
    {
        for (int r = 0; r <= 255; r += 17)
        for (int g = 0; g <= 255; g += 17)
        for (int b = 0; b <= 255; b += 17)
        {
            var c = Color.FromRgb((byte) r, (byte) g, (byte) b);
            var (h, s, v) = c.ToHsv();
            var back = Color.FromHsv(h, s, v);
            Assert.Equal(c, back);
        }
    }

    [Fact]
    public void BrightenAndDarken_ByZero_AreTheIdentity()
    {
        // A no-op shade must not silently shift the color (the truncation bug made Brighten(0) drop a channel).
        var c = Color.FromRgb(0, 11, 66);
        Assert.Equal(c, c.Brighten(0.0));
        Assert.Equal(c, c.Darken(0.0));
    }

    [Fact]
    public void OutOfRangeValue_Clamps_DoesNotWrapModulo256()
    {
        // value 1.3 drives red to 1.3*255 = 331.5; the old truncating cast wrapped (331 & 0xFF = 75 → dark red).
        // Clamping saturates to bright red instead.
        var c = Color.FromHsv(hue: 0.0, saturation: 1.0, value: 1.3);
        Assert.Equal(Color.FromRgb(255, 0, 0), c);
    }

    [Fact]
    public void OverSaturation_Clamps_DoesNotWrapNegativeChannels()
    {
        // saturation 1.2 drives the non-dominant channels to -0.2*255 = -51; the old cast wrapped to 205
        // (near-white pink). Clamping floors them at 0 (a fully saturated red).
        var c = Color.FromHsv(hue: 0.0, saturation: 1.2, value: 1.0);
        Assert.Equal(Color.FromRgb(255, 0, 0), c);
    }
}
