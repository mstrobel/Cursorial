using Cursorial.Animation;
using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

public class RelativePointInterpolatorTests
{
    [Fact]
    public void Interpolates_EachAxis()
    {
        var p = RelativePointInterpolator.Instance.Interpolate(new RelativePoint(0, 0), new RelativePoint(1, 2), 0.5);
        Assert.Equal(0.5, p.X, 10);
        Assert.Equal(1.0, p.Y, 10);
    }

    [Fact]
    public void IsUnbounded_Extrapolates()
    {
        var p = RelativePointInterpolator.Instance.Interpolate(new RelativePoint(0, 0), new RelativePoint(1, 0), 2.0);
        Assert.Equal(2.0, p.X, 10);
    }

    [Fact]
    public void Singleton() => Assert.Same(RelativePointInterpolator.Instance, RelativePointInterpolator.Instance);
}

public class BrushInterpolatorTests
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static IBrush I(IBrush a, IBrush b, double t) => BrushInterpolator.Instance.Interpolate(a, b, t);

    [Fact]
    public void Solid_BlendsColor()
    {
        var r = I(new SolidColorBrush(Color.FromRgb(0, 0, 0)), new SolidColorBrush(Color.FromRgb(100, 200, 40)), 0.5);
        var s = Assert.IsType<SolidColorBrush>(r);
        Assert.Equal(Color.FromRgb(50, 100, 20), s.Color);
    }

    [Fact]
    public void Linear_BlendsEndpointsStopsAndOpacity()
    {
        var a = new LinearGradientBrush([new(0, Red), new(1, Blue)],
            startPoint: new RelativePoint(0, 0), endPoint: new RelativePoint(1, 0), opacity: 1.0);
        var b = new LinearGradientBrush([new(0, Blue), new(1, Red)],
            startPoint: new RelativePoint(1, 0), endPoint: new RelativePoint(2, 0), opacity: 0.0);

        var g = Assert.IsType<LinearGradientBrush>(I(a, b, 0.5));
        Assert.Equal(0.5, g.StartPoint.X, 10);     // endpoints lerp
        Assert.Equal(1.5, g.EndPoint.X, 10);
        Assert.Equal(0.5, g.Opacity, 10);          // opacity lerps
        Assert.Equal(Color.FromRgb(128, 0, 128), g.Stops[0].Color);   // red→blue at 0.5 (premultiplied)
        Assert.Equal(Color.FromRgb(128, 0, 128), g.Stops[1].Color);   // blue→red at 0.5
    }

    [Fact]
    public void Linear_Spread_SnapsAtMidpoint()
    {
        var a = new LinearGradientBrush(Red, Blue, spread: GradientSpread.Pad);
        var b = new LinearGradientBrush(Red, Blue, spread: GradientSpread.Reflect);
        Assert.Equal(GradientSpread.Pad, ((LinearGradientBrush) I(a, b, 0.3)).Spread);
        Assert.Equal(GradientSpread.Reflect, ((LinearGradientBrush) I(a, b, 0.7)).Spread);
    }

    [Fact]
    public void Radial_BlendsCenterAndRadii()
    {
        var a = new RadialGradientBrush(Red, Blue, center: new RelativePoint(0, 0), radiusX: 0.2, radiusY: 0.2);
        var b = new RadialGradientBrush(Red, Blue, center: new RelativePoint(1, 1), radiusX: 0.6, radiusY: 0.8);
        var g = Assert.IsType<RadialGradientBrush>(I(a, b, 0.5));
        Assert.Equal(0.5, g.Center.X, 10);
        Assert.Equal(0.4, g.RadiusX, 10);
        Assert.Equal(0.5, g.RadiusY, 10);
    }

    [Fact]
    public void Conic_BlendsAngle()
    {
        var a = new ConicGradientBrush(Red, Blue, angleDegrees: 0);
        var b = new ConicGradientBrush(Red, Blue, angleDegrees: 180);
        Assert.Equal(90.0, ((ConicGradientBrush) I(a, b, 0.5)).AngleDegrees, 10);
    }

    [Fact]
    public void MismatchedStopCounts_Snap()
    {
        var a = new LinearGradientBrush([new(0, Red), new(1, Blue)]);
        var b = new LinearGradientBrush([new(0, Red), new(0.5, Blue), new(1, Red)]);
        Assert.Same(a, I(a, b, 0.3));
        Assert.Same(b, I(a, b, 0.7));
    }

    [Fact]
    public void DisparateTypes_Snap()
    {
        IBrush solid = new SolidColorBrush(Red);
        IBrush linear = new LinearGradientBrush(Red, Blue);
        Assert.Same(solid, I(solid, linear, 0.3));
        Assert.Same(linear, I(solid, linear, 0.7));
    }

    [Fact]
    public void ScrollingGradient_SweepsEndpoints_PreservingReflect()
    {
        // The Consolonia scrolling-gradient case: animate a Reflect gradient's endpoints past 1, looped.
        var a = new LinearGradientBrush([new(0, Red), new(1, Blue)],
            new RelativePoint(0, 0), new RelativePoint(1, 0), GradientSpread.Reflect);
        var b = new LinearGradientBrush([new(0, Red), new(1, Blue)],
            new RelativePoint(1, 0), new RelativePoint(2, 0), GradientSpread.Reflect);
        var anim = new BrushAnimation(a, b, TimeSpan.FromSeconds(1)).Loop();

        var mid = Assert.IsType<LinearGradientBrush>(anim.ValueAt(TimeSpan.FromSeconds(0.5)));
        Assert.Equal(0.5, mid.StartPoint.X, 10);          // endpoints swept past unit and back
        Assert.Equal(1.5, mid.EndPoint.X, 10);
        Assert.Equal(GradientSpread.Reflect, mid.Spread);  // spread survives the sweep
    }

    [Fact]
    public void Singleton() => Assert.Same(BrushInterpolator.Instance, BrushInterpolator.Instance);
}

public class CompositeParametersInterpolatorTests
{
    private static CompositeParameters Interp(CompositeParameters a, CompositeParameters b, double t) =>
        CompositeParametersInterpolator.Instance.Interpolate(a, b, t);

    [Fact]
    public void BlendsOffsetAndOpacity()
    {
        var r = Interp(new CompositeParameters(0, 0, 255), new CompositeParameters(10, 4, 0), 0.5);
        Assert.Equal(5, r.OffsetColumn);
        Assert.Equal(2, r.OffsetRow);
        Assert.Equal(128, r.Opacity);   // 255→0 at 0.5 → round(127.5)=128
    }

    [Fact]
    public void Endpoints()
    {
        var from = new CompositeParameters(2, 3, 200);
        var to = new CompositeParameters(20, 30, 50);
        Assert.Equal(from, Interp(from, to, 0.0));
        Assert.Equal(to.Opacity, Interp(from, to, 1.0).Opacity);
        Assert.Equal(to.OffsetColumn, Interp(from, to, 1.0).OffsetColumn);
    }

    [Fact]
    public void Clip_SnapsAtMidpoint()
    {
        var from = new CompositeParameters(0, 0, 255, clip: new Rect(0, 0, 5, 5));
        var to = new CompositeParameters(0, 0, 255, clip: new Rect(1, 1, 9, 9));
        Assert.Equal(from.Clip, Interp(from, to, 0.3).Clip);
        Assert.Equal(to.Clip, Interp(from, to, 0.7).Clip);
    }

    [Fact]
    public void Singleton() => Assert.Same(CompositeParametersInterpolator.Instance, CompositeParametersInterpolator.Instance);
}

public class DrawingAnimationConvenienceTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    [Fact]
    public void BrushAnimation_IsAnAnimationOfBrush_AndInterpolates()
    {
        var a = new BrushAnimation(new SolidColorBrush(Color.FromRgb(0, 0, 0)),
                                   new SolidColorBrush(Color.FromRgb(100, 200, 40)), OneSecond);
        Assert.IsAssignableFrom<Animation<IBrush>>(a);
        var mid = Assert.IsType<SolidColorBrush>(a.ValueAt(TimeSpan.FromSeconds(0.5)));
        Assert.Equal(Color.FromRgb(50, 100, 20), mid.Color);
    }

    [Fact]
    public void CompositeParametersAnimation_Slides()
    {
        var a = new CompositeParametersAnimation(new CompositeParameters(0, 0, 255), new CompositeParameters(10, 0, 255), OneSecond);
        Assert.IsAssignableFrom<Animation<CompositeParameters>>(a);
        Assert.Equal(5, a.ValueAt(TimeSpan.FromSeconds(0.5)).OffsetColumn);
    }
}
