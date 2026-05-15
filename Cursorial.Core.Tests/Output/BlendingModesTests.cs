using Cursorial.Output;

namespace Cursorial.Tests.Output;

public class BlendingModesTests
{
    // ---- SourceOver / Default ----

    [Fact]
    public void SourceOver_ReturnsSourceVerbatim()
    {
        var s = Color.FromRgb(255, 128, 0);
        var b = Color.FromRgb(0, 0, 200);
        Assert.Equal(s, BlendingModes.SourceOver.Blend(s, b));
    }

    [Fact]
    public void Default_IsSameAsSourceOver()
    {
        Assert.Same(BlendingModes.SourceOver, BlendingModes.Default);
    }

    // ---- Non-RGB short circuit ----

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NonRgbInputs_AllModesShortCircuitToSource(bool sourceIsDefault, bool backdropIsDefault)
    {
        var source = sourceIsDefault ? Color.Default : Color.FromPalette(3);
        var backdrop = backdropIsDefault ? Color.Default : Color.FromPalette(7);

        IBlendingMode[] allModes =
        [
            BlendingModes.Multiply, BlendingModes.Screen, BlendingModes.Overlay,
            BlendingModes.Darken, BlendingModes.Lighten, BlendingModes.Plus
        ];

        foreach (var mode in allModes)
            Assert.Equal(source, mode.Blend(source, backdrop));
    }

    [Fact]
    public void RgbSourceWithDefaultBackdrop_ShortCircuitsToSource()
    {
        var s = Color.FromRgb(10, 20, 30);
        Assert.Equal(s, BlendingModes.Multiply.Blend(s, Color.Default));
        Assert.Equal(s, BlendingModes.Screen.Blend(s, Color.Default));
    }

    // ---- Multiply ----

    [Fact]
    public void Multiply_WhiteSourceWithAnyBackdrop_ReturnsBackdrop()
    {
        // src.r * back.r / 255: when src is white, result = back.
        var backdrop = Color.FromRgb(100, 50, 200);
        Assert.Equal(backdrop, BlendingModes.Multiply.Blend(Color.FromRgb(255, 255, 255), backdrop));
    }

    [Fact]
    public void Multiply_BlackSourceWithAnyBackdrop_ReturnsBlack()
    {
        var result = BlendingModes.Multiply.Blend(Color.FromRgb(0, 0, 0), Color.FromRgb(200, 200, 200));
        Assert.Equal(Color.FromRgb(0, 0, 0), result);
    }

    [Fact]
    public void Multiply_HalfByHalf_IsApproximatelyQuarter()
    {
        // 128 * 128 / 255 ≈ 64.
        var result = BlendingModes.Multiply.Blend(Color.FromRgb(128, 128, 128), Color.FromRgb(128, 128, 128));
        Assert.Equal(Color.FromRgb(64, 64, 64), result);
    }

    // ---- Screen ----

    [Fact]
    public void Screen_BlackSourceWithAnyBackdrop_ReturnsBackdrop()
    {
        // 255 - (255-0)*(255-back)/255 = back
        var backdrop = Color.FromRgb(100, 50, 200);
        Assert.Equal(backdrop, BlendingModes.Screen.Blend(Color.FromRgb(0, 0, 0), backdrop));
    }

    [Fact]
    public void Screen_WhiteSourceWithAnyBackdrop_ReturnsWhite()
    {
        var result = BlendingModes.Screen.Blend(Color.FromRgb(255, 255, 255), Color.FromRgb(100, 100, 100));
        Assert.Equal(Color.FromRgb(255, 255, 255), result);
    }

    // ---- Darken / Lighten ----

    [Fact]
    public void Darken_ChannelWiseMinimum()
    {
        var s = Color.FromRgb(50, 200, 100);
        var b = Color.FromRgb(100, 100, 100);
        Assert.Equal(Color.FromRgb(50, 100, 100), BlendingModes.Darken.Blend(s, b));
    }

    [Fact]
    public void Lighten_ChannelWiseMaximum()
    {
        var s = Color.FromRgb(50, 200, 100);
        var b = Color.FromRgb(100, 100, 100);
        Assert.Equal(Color.FromRgb(100, 200, 100), BlendingModes.Lighten.Blend(s, b));
    }

    // ---- Plus ----

    [Fact]
    public void Plus_ChannelWiseAddSaturated()
    {
        var s = Color.FromRgb(100, 200, 50);
        var b = Color.FromRgb(50, 100, 100);
        Assert.Equal(Color.FromRgb(150, 255, 150), BlendingModes.Plus.Blend(s, b)); // 200+100 saturates to 255.
    }

    // ---- Overlay ----

    [Fact]
    public void Overlay_DarkBackdrop_BehavesLikeMultiply()
    {
        // For b < 128 the formula is 2*s*b/255 — like multiply but doubled.
        // s=128, b=64: 2*128*64/255 = 64.
        var s = Color.FromRgb(128, 128, 128);
        var b = Color.FromRgb(64, 64, 64);
        Assert.Equal(Color.FromRgb(64, 64, 64), BlendingModes.Overlay.Blend(s, b));
    }

    [Fact]
    public void Overlay_LightBackdrop_BehavesLikeScreen()
    {
        // For b >= 128 the formula is 255 - 2*(255-s)*(255-b)/255 — like screen but doubled.
        // s=128, b=192: 255 - 2*127*63/255 = 255 - 62 = 193.
        var s = Color.FromRgb(128, 128, 128);
        var b = Color.FromRgb(192, 192, 192);
        var result = BlendingModes.Overlay.Blend(s, b);
        Assert.InRange(result.Red, (byte) 190, (byte) 196);
    }
}