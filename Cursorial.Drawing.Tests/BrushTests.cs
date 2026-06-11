using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

public class BrushTests
{
    private static readonly Rect AnyBounds = new(0, 0, 10, 10);

    [Fact]
    public void ImplicitColorConversion_ProducesSolidBrush()
    {
        SolidColorBrush brush = Color.FromRgb(10, 20, 30);

        Assert.Equal(Color.FromRgb(10, 20, 30), brush.Color);
    }

    [Fact]
    public void Brushes_Default_IsSolidOverDefaultColor()
    {
        var brush = Brushes.Default;

        Assert.True(brush.Color.IsDefault);   // NOT transparent — the default color is a no-op, not invisible
    }

    [Fact]
    public void Brushes_Transparent_IsTransparentColor()
    {
        Assert.Equal(Color.Transparent, Brushes.Transparent.Color);
        Assert.Equal(0, Brushes.Transparent.Color.Alpha);
    }

    [Fact]
    public void SolidSample_IsPositionIndependent()
    {
        var brush = new SolidColorBrush(Color.FromRgb(1, 2, 3));

        Assert.Equal(Color.FromRgb(1, 2, 3), brush.ColorAt(0, 0, AnyBounds));
        Assert.Equal(Color.FromRgb(1, 2, 3), brush.ColorAt(9, 9, AnyBounds));
    }

    [Fact]
    public void Opacity_ScalesRgbAlpha()
    {
        var brush = new SolidColorBrush(Color.FromRgb(255, 0, 0), 0.5);   // opaque red at 50%

        var sampled = brush.ColorAt(0, 0, AnyBounds);
        Assert.Equal(ColorKind.Rgb, sampled.Kind);
        Assert.Equal(128, sampled.Alpha);   // 255 * 0.5, rounded
    }

    [Fact]
    public void Opacity_OnNonRgbColor_IsIgnored()
    {
        // Terminal-default / palette carry no alpha, so opacity can't apply.
        var brush = new SolidColorBrush(Color.Default, 0.5);
        Assert.True(brush.ColorAt(0, 0, AnyBounds).IsDefault);
    }

    [Fact]
    public void NonFiniteOpacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SolidColorBrush(Color.FromRgb(1, 2, 3), double.NaN));
    }

    [Fact]
    public void TwoColorLinearConstructor_BuildsEndpointStops()
    {
        // The convenience ctor is the verbose [new(0,start), new(1,end)] in one line.
        var brush = new LinearGradientBrush(Color.FromRgb(0, 0, 0), Color.FromRgb(255, 255, 255));

        Assert.Equal(2, brush.Stops.Count);
        Assert.Equal(0.0, brush.Stops[0].Offset);
        Assert.Equal(Color.FromRgb(0, 0, 0), brush.Stops[0].Color);
        Assert.Equal(1.0, brush.Stops[1].Offset);
        Assert.Equal(Color.FromRgb(255, 255, 255), brush.Stops[1].Color);
    }

    [Fact]
    public void TwoColorRadialAndConicConstructors_BuildEndpointStops()
    {
        var radial = new RadialGradientBrush(Color.FromRgb(1, 1, 1), Color.FromRgb(2, 2, 2));
        var conic = new ConicGradientBrush(Color.FromRgb(3, 3, 3), Color.FromRgb(4, 4, 4));

        Assert.Equal(new[] { Color.FromRgb(1, 1, 1), Color.FromRgb(2, 2, 2) }, radial.Stops.Select(s => s.Color));
        Assert.Equal(new[] { Color.FromRgb(3, 3, 3), Color.FromRgb(4, 4, 4) }, conic.Stops.Select(s => s.Color));
    }

    [Fact]
    public void GradientStop_TupleConversion_IsImplicit()
    {
        // A stop list can be written as tuples; the brush sees real GradientStops.
        var brush = new LinearGradientBrush([(0.0, Color.FromRgb(10, 0, 0)), (1.0, Color.FromRgb(0, 0, 10))]);

        Assert.Equal(Color.FromRgb(10, 0, 0), brush.Stops[0].Color);
        Assert.Equal(Color.FromRgb(0, 0, 10), brush.Stops[1].Color);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonFiniteGeometry_Throws(double bad)
    {
        var stops = new GradientStop[] { new(0.0, Color.FromRgb(0, 0, 0)), new(1.0, Color.FromRgb(255, 255, 255)) };

        // Mirrors the opacity guard: bad geometry fails loudly rather than silently flattening the gradient.
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinearGradientBrush(stops, startPoint: new RelativePoint(bad, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadialGradientBrush(stops, radiusX: bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadialGradientBrush(stops, gradientOrigin: new RelativePoint(0, bad)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConicGradientBrush(stops, angleDegrees: bad));
    }
}
