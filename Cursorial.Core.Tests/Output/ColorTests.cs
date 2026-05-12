using Cursorial.Core.Output;

namespace Cursorial.Core.Tests.Output;

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
}
