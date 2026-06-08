using Cursorial.Animation;
using Cursorial.Output;

namespace Cursorial.Tests.Animation;

public class ColorAnimationTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static void AssertRgba(Color c, byte r, byte g, byte b, byte a)
    {
        Assert.Equal(ColorKind.Rgb, c.Kind);
        Assert.Equal((r, g, b, a), (c.Red, c.Green, c.Blue, c.Alpha));
    }

    [Fact]
    public void Interpolator_DelegatesToColorLerp()
    {
        var from = Color.FromRgb(0, 0, 0);
        var to = Color.FromRgb(100, 200, 40);
        Assert.Equal(Color.Lerp(from, to, 0.25), ColorInterpolator.Instance.Interpolate(from, to, 0.25));
    }

    [Fact]
    public void Interpolator_SingletonAndShortcut()
    {
        Assert.Same(ColorInterpolator.Instance, ColorInterpolator.Instance);
        Assert.Same(ColorInterpolator.Instance, Interpolators.Color);
    }

    [Fact]
    public void ColorAnimation_IsAnAnimationOfColor_AndInterpolates()
    {
        var a = new ColorAnimation(Color.FromRgb(0, 0, 0), Color.FromRgb(100, 200, 40), OneSecond);
        Assert.IsAssignableFrom<Animation<Color>>(a);

        AssertRgba(a.ValueAt(TimeSpan.Zero), 0, 0, 0, 255);
        AssertRgba(a.ValueAt(TimeSpan.FromSeconds(0.5)), 50, 100, 20, 255);
        AssertRgba(a.ValueAt(OneSecond), 100, 200, 40, 255);
    }

    [Fact]
    public void ColorAnimation_HonorsEasing()
    {
        // QuadIn(0.5) = 0.25 → 25% of the way from black to (100,200,40).
        var a = new ColorAnimation(Color.FromRgb(0, 0, 0), Color.FromRgb(100, 200, 40), OneSecond, Easings.QuadIn);
        AssertRgba(a.ValueAt(TimeSpan.FromSeconds(0.5)), 25, 50, 10, 255);
    }

    [Fact]
    public void ColorAnimation_ComposesWithCombinators()
    {
        IAnimation<Color> loop = new ColorAnimation(Color.FromRgb(0, 0, 0), Color.FromRgb(100, 200, 40), OneSecond).Loop();
        Assert.Equal(TimeSpan.MaxValue, loop.Duration);
        AssertRgba(loop.ValueAt(TimeSpan.FromSeconds(1.5)), 50, 100, 20, 255);   // wraps
    }
}
