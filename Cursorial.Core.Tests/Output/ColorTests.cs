using Cursorial.Output;

namespace Cursorial.Tests.Output;

public class ColorTests
{
    [Fact]
    public void Default_IsDefaultKind()
    {
        var c = Color.Default;
        Assert.Equal(ColorKind.Default, c.Kind);
        Assert.True(c.IsDefault);
    }

    [Fact]
    public void DefaultConstructed_EqualsDefault()
    {
        Color c = default;
        Assert.Equal(Color.Default, c);
        Assert.True(c.IsDefault);
    }

    [Fact]
    public void FromPalette_CarriesIndex()
    {
        var c = Color.FromPalette(42);
        Assert.Equal(ColorKind.Palette, c.Kind);
        Assert.Equal((byte)42, c.PaletteIndex);
        Assert.False(c.IsDefault);
    }

    [Fact]
    public void FromRgb_CarriesComponents()
    {
        var c = Color.FromRgb(10, 20, 30);
        Assert.Equal(ColorKind.Rgb, c.Kind);
        Assert.Equal((byte)10, c.Red);
        Assert.Equal((byte)20, c.Green);
        Assert.Equal((byte)30, c.Blue);
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Assert.Equal(Color.FromRgb(1, 2, 3), Color.FromRgb(1, 2, 3));
        Assert.NotEqual(Color.FromRgb(1, 2, 3), Color.FromRgb(1, 2, 4));
        Assert.NotEqual(Color.FromPalette(1), Color.FromRgb(1, 0, 0));
    }

    [Fact]
    public void ToString_DescribesKindAndPayload()
    {
        Assert.Equal("default", Color.Default.ToString());
        Assert.Equal("palette(7)", Color.FromPalette(7).ToString());
        Assert.Equal("rgb(255,128,0)", Color.FromRgb(255, 128, 0).ToString());
    }

    // ---- Alpha ----

    [Fact]
    public void FromRgb_DefaultsToFullyOpaque()
    {
        Assert.Equal((byte)255, Color.FromRgb(1, 2, 3).Alpha);
        Assert.True(Color.FromRgb(1, 2, 3).IsOpaque);
    }

    [Fact]
    public void FromRgba_CarriesExplicitAlpha()
    {
        var c = Color.FromRgba(1, 2, 3, 128);
        Assert.Equal((byte)128, c.Alpha);
        Assert.False(c.IsOpaque);
    }

    [Fact]
    public void FromPalette_DefaultsToFullyOpaque()
    {
        Assert.Equal((byte)255, Color.FromPalette(5).Alpha);
    }

    [Fact]
    public void WithAlpha_RgbReturnsCopyWithNewAlpha()
    {
        var c = Color.FromRgb(10, 20, 30).WithAlpha(64);
        Assert.Equal(Color.FromRgba(10, 20, 30, 64), c);
    }

    [Fact]
    public void WithAlpha_DefaultColorIsNoOp()
    {
        Assert.Equal(Color.Default, Color.Default.WithAlpha(128));
    }

    [Fact]
    public void IsOpaque_NonRgbAlwaysOpaque()
    {
        Assert.True(Color.Default.IsOpaque);
        Assert.True(Color.FromPalette(3).IsOpaque);
        Assert.True(Color.FromPalette(3).WithAlpha(50).IsOpaque); // alpha ignored for palette
    }

    [Fact]
    public void DefaultConstructed_StillEqualsDefaultStatic_DespiteAlphaByte()
    {
        // default(Color) has Kind=Default and alpha=0. Color.Default also has alpha=0 (we
        // chose 0 specifically so this equality holds).
        Assert.Equal(Color.Default, default(Color));
    }

    [Fact]
    public void Equality_AlphaIsPartOfIt()
    {
        Assert.NotEqual(Color.FromRgb(1, 2, 3), Color.FromRgba(1, 2, 3, 128));
        Assert.Equal(Color.FromRgb(1, 2, 3), Color.FromRgba(1, 2, 3, 255));
    }

    [Fact]
    public void ToString_IncludesAlphaWhenNotFullyOpaque()
    {
        Assert.Equal("rgba(10,20,30,128)", Color.FromRgba(10, 20, 30, 128).ToString());
        Assert.Equal("rgb(10,20,30)", Color.FromRgb(10, 20, 30).ToString()); // alpha=255 hidden
    }
}
