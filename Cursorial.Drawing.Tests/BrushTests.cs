using Cursorial.Drawing;
using Cursorial.Output;

namespace Cursorial.Tests.Drawing;

public class BrushTests
{
    private static readonly BrushExtent AnyExtent = new(0, 0, 10, 10);

    [Fact]
    public void ImplicitColorConversion_ProducesSolidBrush()
    {
        Brush brush = Color.FromRgb(10, 20, 30);

        Assert.True(brush.IsSolid);
        Assert.Equal(Color.FromRgb(10, 20, 30), brush.SolidColor);
    }

    [Fact]
    public void Default_IsOpaqueSolidOverDefaultColor()
    {
        var brush = Brush.Default;

        Assert.True(brush.IsSolid);
        Assert.True(brush.IsDefault);
        Assert.True(brush.SolidColor.IsDefault);   // NOT transparent — the struct default is a no-op, not invisible
    }

    [Fact]
    public void SolidSample_IsPositionIndependent()
    {
        var brush = Brush.Solid(Color.FromRgb(1, 2, 3));

        Assert.Equal(Color.FromRgb(1, 2, 3), brush.Sample(0, 0, AnyExtent));
        Assert.Equal(Color.FromRgb(1, 2, 3), brush.Sample(9, 9, AnyExtent));
    }

    [Fact]
    public void Opacity_ScalesRgbAlpha()
    {
        var brush = Brush.Solid(Color.FromRgb(255, 0, 0), 0.5);   // opaque red at 50%

        var sampled = brush.Sample(0, 0, AnyExtent);
        Assert.Equal(ColorKind.Rgb, sampled.Kind);
        Assert.Equal(128, sampled.Alpha);   // 255 * 0.5, rounded
    }

    [Fact]
    public void Opacity_OnNonRgbColor_IsIgnored()
    {
        // Terminal-default / palette carry no alpha, so opacity can't apply.
        var brush = Brush.Solid(Color.Default, 0.5);
        Assert.True(brush.Sample(0, 0, AnyExtent).IsDefault);
    }
}
