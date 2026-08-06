using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Rendering;

using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// #5 — gradient brushes are element-declarable in XAML: <c>&lt;LinearGradientBrush&gt;</c> /
/// <c>&lt;RadialGradientBrush&gt;</c> / <c>&lt;ConicGradientBrush&gt;</c> with <c>&lt;GradientStop&gt;</c>
/// content (the brushes' ContentProperty), bounds-relative points via the <c>"x,y"</c>
/// <see cref="RelativePoint"/> converter, and order-agnostic sampling (element stops need no sort).
/// </summary>
public sealed class XamlGradientBrushTests
{
    private const string Ns = " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact]
    public void LinearGradient_ElementWithStopsAndPoints()
    {
        var brush = new XamlLoader().Load<LinearGradientBrush>(
            "<LinearGradientBrush" + Ns + " StartPoint=\"0,0\" EndPoint=\"1,0\" Spread=\"Repeat\">" +
              "<GradientStop Offset=\"0\" Color=\"#ff0000\"/>" +
              "<GradientStop Offset=\"1\" Color=\"#0000ff\"/>" +
            "</LinearGradientBrush>");

        Assert.Equal(new RelativePoint(0, 0), brush.StartPoint);
        Assert.Equal(new RelativePoint(1, 0), brush.EndPoint);
        Assert.Equal(GradientSpread.Repeat, brush.Spread);
        Assert.Equal(2, brush.Stops.Count);
        Assert.Equal(Color.FromHex("#ff0000"), brush.Stops[0].Color);
        Assert.Equal(0.0, brush.Stops[0].Offset);
        Assert.Equal(Color.FromHex("#0000ff"), brush.Stops[1].Color);

        // It samples (cell-center): the left of a 4×1 box is red-dominant, the right blue-dominant.
        var rect = new Rect(0, 0, 4, 1);
        var left = brush.ColorAt(0, 0, rect);
        var right = brush.ColorAt(3, 0, rect);
        Assert.True(left.Red > left.Blue, $"left should be red-dominant ({left.Red},{left.Blue})");
        Assert.True(right.Blue > right.Red, $"right should be blue-dominant ({right.Red},{right.Blue})");
    }

    [Fact] // Element stops in DESCENDING declaration order still sample correctly (order-agnostic).
    public void LinearGradient_UnsortedElementStops_SampleOrderAgnostic()
    {
        var brush = new XamlLoader().Load<LinearGradientBrush>(
            "<LinearGradientBrush" + Ns + " StartPoint=\"0,0\" EndPoint=\"1,0\">" +
              "<GradientStop Offset=\"1\" Color=\"#0000ff\"/>" +   // blue first (offset 1)
              "<GradientStop Offset=\"0\" Color=\"#ff0000\"/>" +   // red second (offset 0)
            "</LinearGradientBrush>");

        // Declaration order is preserved (element authoring does not sort), but sampling is order-agnostic:
        // the left is still red-dominant (offset 0), the right blue-dominant (offset 1).
        Assert.Equal(1.0, brush.Stops[0].Offset); // declaration order, not sorted
        var rect = new Rect(0, 0, 4, 1);
        var left = brush.ColorAt(0, 0, rect);
        var right = brush.ColorAt(3, 0, rect);
        Assert.True(left.Red > left.Blue, $"left should be red-dominant ({left.Red},{left.Blue})");
        Assert.True(right.Blue > right.Red, $"right should be blue-dominant ({right.Red},{right.Blue})");
    }

    [Fact]
    public void RadialGradient_ElementAuthoring()
    {
        var brush = new XamlLoader().Load<RadialGradientBrush>(
            "<RadialGradientBrush" + Ns + " Center=\"0.5,0.5\" RadiusX=\"0.4\" RadiusY=\"0.3\">" +
              "<GradientStop Offset=\"0\" Color=\"#ffffff\"/>" +
              "<GradientStop Offset=\"1\" Color=\"#000000\"/>" +
            "</RadialGradientBrush>");

        Assert.Equal(new RelativePoint(0.5, 0.5), brush.Center);
        Assert.Equal(0.4, brush.RadiusX);
        Assert.Equal(0.3, brush.RadiusY);
        Assert.Equal(brush.Center, brush.GradientOrigin); // defaults to Center when unset
        Assert.Equal(2, brush.Stops.Count);
    }

    [Fact]
    public void ConicGradient_ElementAuthoring()
    {
        var brush = new XamlLoader().Load<ConicGradientBrush>(
            "<ConicGradientBrush" + Ns + " Center=\"0.5,0.5\" AngleDegrees=\"90\">" +
              "<GradientStop Offset=\"0\" Color=\"#ff0000\"/>" +
              "<GradientStop Offset=\"1\" Color=\"#00ff00\"/>" +
            "</ConicGradientBrush>");

        Assert.Equal(new RelativePoint(0.5, 0.5), brush.Center);
        Assert.Equal(90.0, brush.AngleDegrees);
        Assert.Equal(2, brush.Stops.Count);
    }

    [Fact] // Invalid relative point -> a positioned conversion error.
    public void RelativePoint_BadFormat_Errors()
    {
        var ex = Assert.Throws<XamlParseException>(() => new XamlLoader().Load<LinearGradientBrush>(
            "<LinearGradientBrush" + Ns + " StartPoint=\"nope\">" +
              "<GradientStop Offset=\"0\" Color=\"#000\"/>" +
            "</LinearGradientBrush>"));
        Assert.Contains("relative point", ex.Message);
    }
}
