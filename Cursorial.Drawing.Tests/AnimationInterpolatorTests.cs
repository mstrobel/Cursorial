using Cursorial.Animation;
using Cursorial.Drawing;
using Cursorial.Drawing.Charts;
using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;

// ReSharper disable RedundantCast

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

public class SizeInterpolatorTests
{
    private static Size I(Size a, Size b, double t) => SizeInterpolator.Instance.Interpolate(a, b, t);

    [Fact]
    public void Interpolates_AndRounds_EachDimension()
    {
        // 0→3 at 0.5 is 1.5 → rounds away from zero to 2 (per-cell, like Int32Interpolator).
        Assert.Equal(new Size(2, 2), I(new Size(0, 0), new Size(3, 3), 0.5));
    }

    [Fact]
    public void Endpoints()
    {
        Assert.Equal(new Size(4, 9), I(new Size(4, 9), new Size(40, 90), 0.0));
        Assert.Equal(new Size(40, 90), I(new Size(4, 9), new Size(40, 90), 1.0));
    }

    [Fact]
    public void OvershootingEasing_ClampsDimensionsToZero()
    {
        // Progress past 1 (e.g. an anticipation/back ease) would drive 10→0 negative; clamp pins it at 0
        // so a Size never goes negative.
        Assert.Equal(Size.Empty, I(new Size(10, 10), new Size(0, 0), 1.5));
    }

    [Fact]
    public void Singleton() => Assert.Same(SizeInterpolator.Instance, SizeInterpolator.Instance);
}

public class RectInterpolatorTests
{
    private static Rect I(Rect a, Rect b, double t) => RectInterpolator.Instance.Interpolate(a, b, t);

    [Fact]
    public void Interpolates_AnchorAndExtent()
    {
        // Slide (0,0)→(10,20) and grow 4×4 → 8×8, halfway.
        var r = I(new Rect(0, 0, 4, 4), new Rect(10, 20, 8, 8), 0.5);
        Assert.Equal(new Rect(5, 10, 6, 6), r);
    }

    [Fact]
    public void Endpoints()
    {
        var from = new Rect(2, 3, 5, 5);
        var to = new Rect(20, 30, 9, 9);
        Assert.Equal(from, I(from, to, 0.0));
        Assert.Equal(to, I(from, to, 1.0));
    }

    [Fact]
    public void OvershootingEasing_ClampsToZero_WithoutThrowing()
    {
        // Without the ≥0 clamp this would compute negative dimensions and the Rect ctor would throw.
        var r = I(new Rect(10, 10, 10, 10), new Rect(0, 0, 0, 0), 1.5);
        Assert.Equal(new Rect(0, 0, 0, 0), r);
    }

    [Fact]
    public void Singleton() => Assert.Same(RectInterpolator.Instance, RectInterpolator.Instance);
}

public class PointInterpolatorTests
{
    private static PointD I(PointD a, PointD b, double t) => PointInterpolator.Instance.Interpolate(a, b, t);

    [Fact]
    public void Interpolates_EachAxis_Continuously()
    {
        var p = I(new PointD(0, 0), new PointD(1, 2), 0.5);
        Assert.Equal(0.5, p.X, 10);
        Assert.Equal(1.0, p.Y, 10);
    }

    [Fact]
    public void IsUnbounded_Extrapolates()
    {
        // Continuous value space — no clamp/round, so an overshooting ease extrapolates cleanly.
        var p = I(new PointD(0, 0), new PointD(10, 0), 1.5);
        Assert.Equal(15.0, p.X, 10);
    }

    [Fact]
    public void Singleton() => Assert.Same(PointInterpolator.Instance, PointInterpolator.Instance);
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

    [Fact]
    public void SizeAnimation_Grows()
    {
        var a = new SizeAnimation(new Size(10, 4), new Size(30, 24), OneSecond);
        Assert.IsAssignableFrom<Animation<Size>>(a);
        Assert.Equal(new Size(20, 14), a.ValueAt(TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public void RectAnimation_SlidesAndResizes()
    {
        var a = new RectAnimation(new Rect(0, 0, 4, 4), new Rect(10, 20, 8, 8), OneSecond);
        Assert.IsAssignableFrom<Animation<Rect>>(a);
        Assert.Equal(new Rect(5, 10, 6, 6), a.ValueAt(TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public void PointAnimation_Moves()
    {
        var a = new PointAnimation(new PointD(0, 0), new PointD(4, 8), OneSecond);
        Assert.IsAssignableFrom<Animation<PointD>>(a);
        var mid = a.ValueAt(TimeSpan.FromSeconds(0.5));
        Assert.Equal(2.0, mid.X, 10);
        Assert.Equal(4.0, mid.Y, 10);
    }
}

public class PenInterpolatorTests
{
    private static Pen I(Pen a, Pen b, double t) => PenInterpolator.Instance.Interpolate(a, b, t);

    [Fact]
    public void Brush_BlendsThroughBrushInterpolator()
    {
        var a = new Pen(new SolidColorBrush(Color.FromRgb(0, 0, 0)));
        var b = new Pen(new SolidColorBrush(Color.FromRgb(100, 200, 40)));
        var s = Assert.IsType<SolidColorBrush>(I(a, b, 0.5).Brush);
        Assert.Equal(Color.FromRgb(50, 100, 20), s.Color);
    }

    [Fact]
    public void Brush_ReferenceEqual_PassesThroughSameInstance()
    {
        var shared = new SolidColorBrush(Color.FromRgb(10, 20, 30));
        var result = I(new Pen(shared), new Pen(shared).WithWeight(StrokeWeight.Heavy), 0.25);
        Assert.Same(shared, result.Brush);
    }

    [Fact]
    public void Brush_NullOnEitherSide_SnapsAtMidpoint()
    {
        var brush = new SolidColorBrush(Color.FromRgb(255, 0, 0));
        var a = new Pen((IBrush?) null);
        var b = new Pen(brush);
        Assert.Null(I(a, b, 0.3).Brush);          // from's null wins below the midpoint
        Assert.Same(brush, I(a, b, 0.7).Brush);   // to's brush wins at/after it

        Assert.Null(I(a, new Pen((IBrush?) null), 0.5).Brush);   // both null stays null
    }

    [Fact]
    public void DiscreteMembers_SnapAtMidpoint()
    {
        var a = Pens.Heavy.WithDash(LineDash.Triple).WithJunction(JunctionMode.Break)
            .WithAttributes(TextAttributes.Bold);
        var b = Pens.Double.WithCorners(CornerStyle.Rounded).WithEndCap(EndCap.Stub)
            .WithGlyphSet(GlyphSet.Ascii);

        var early = I(a, b, 0.49);
        Assert.Equal(StrokeWeight.Heavy, early.Weight);
        Assert.Equal(LineDash.Triple, early.Dash);
        Assert.Equal(JunctionMode.Break, early.Junction);
        Assert.Equal(TextAttributes.Bold, early.Attributes);

        var late = I(a, b, 0.5);
        Assert.Equal(StrokeWeight.Double, late.Weight);
        Assert.Equal(CornerStyle.Rounded, late.Corners);
        Assert.Equal(EndCap.Stub, late.EndCap);
        Assert.Equal(GlyphSet.Ascii, late.GlyphSet);
        Assert.Equal(TextAttributes.None, late.Attributes);
    }

    [Fact]
    public void IdenticalEndpoints_RoundTrip()
    {
        var pen = Pens.Rounded.WithColor(Color.FromRgb(1, 2, 3));
        var result = I(pen, pen, 0.5);
        Assert.Equal(pen.Weight, result.Weight);
        Assert.Equal(pen.Corners, result.Corners);
        var s = Assert.IsType<SolidColorBrush>(result.Brush);
        Assert.Equal(Color.FromRgb(1, 2, 3), s.Color);
    }

    [Fact]
    public void Singleton() => Assert.Same(PenInterpolator.Instance, PenInterpolator.Instance);
}

public class PenAnimationTests
{
    [Fact]
    public void PenAnimation_IsAnAnimationOfPen_AndInterpolates()
    {
        var a = new PenAnimation(
            new Pen(new SolidColorBrush(Color.FromRgb(0, 0, 0))),
            Pens.Heavy.WithBrush(new SolidColorBrush(Color.FromRgb(200, 100, 50))),
            TimeSpan.FromSeconds(1));
        Assert.IsAssignableFrom<Animation<Pen>>(a);

        var mid = a.ValueAt(TimeSpan.FromSeconds(0.5));
        var s = Assert.IsType<SolidColorBrush>(mid.Brush);
        Assert.Equal(Color.FromRgb(100, 50, 25), s.Color);
        Assert.Equal(StrokeWeight.Heavy, mid.Weight);   // discrete snapped to `to` at the midpoint

        Assert.Equal(StrokeWeight.Heavy, a.ValueAt(TimeSpan.FromSeconds(1)).Weight);
    }
}
