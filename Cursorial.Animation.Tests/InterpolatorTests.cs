using Cursorial.Animation;

namespace Cursorial.Tests.Animation;

public class InterpolatorTests
{
    [Fact]
    public void Double_Interpolates()
    {
        var i = DoubleInterpolator.Instance;
        Assert.Equal(0.0, i.Interpolate(0, 10, 0.0));
        Assert.Equal(3.0, i.Interpolate(0, 10, 0.3));
        Assert.Equal(10.0, i.Interpolate(0, 10, 1.0));
    }

    [Fact]
    public void Double_ExtrapolatesPastUnit_ForOvershootEasings()
    {
        var i = DoubleInterpolator.Instance;
        Assert.Equal(15.0, i.Interpolate(0, 10, 1.5));
        Assert.Equal(-5.0, i.Interpolate(0, 10, -0.5));
    }

    [Fact]
    public void Int32_RoundsAwayFromZero()
    {
        var i = Int32Interpolator.Instance;
        Assert.Equal(5, i.Interpolate(0, 10, 0.5));
        Assert.Equal(3, i.Interpolate(0, 5, 0.5));    // 2.5 → 3
        Assert.Equal(-3, i.Interpolate(0, -5, 0.5));  // -2.5 → -3
        Assert.Equal(7, i.Interpolate(4, 10, 0.5));   // 7.0
    }

    [Fact]
    public void Singletons_AreShared()
    {
        Assert.Same(DoubleInterpolator.Instance, DoubleInterpolator.Instance);
        Assert.Same(Int32Interpolator.Instance, Int32Interpolator.Instance);
        Assert.Same(DoubleInterpolator.Instance, Interpolators.Double);
        Assert.Same(Int32Interpolator.Instance, Interpolators.Int32);
    }
}
