using Cursorial.Animation;

namespace Cursorial.Tests.Animation;

public class AnimationTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    [Fact]
    public void Linear_InterpolatesEndpointsAndMidpoint()
    {
        var a = new Animation<double>(0.0, 10.0, OneSecond, DoubleInterpolator.Instance);

        Assert.Equal(0.0, a.ValueAt(TimeSpan.Zero));
        Assert.Equal(5.0, a.ValueAt(TimeSpan.FromSeconds(0.5)));
        Assert.Equal(10.0, a.ValueAt(OneSecond));
    }

    [Fact]
    public void ClampsOutOfRangeElapsed_ToFirstAndLastFrame()
    {
        var a = new Animation<double>(2.0, 8.0, OneSecond, DoubleInterpolator.Instance);

        Assert.Equal(2.0, a.ValueAt(TimeSpan.FromSeconds(-5)));   // before start → From
        Assert.Equal(8.0, a.ValueAt(TimeSpan.FromSeconds(5)));    // after end → To
    }

    [Fact]
    public void AppliesEasing_ToProgressNotValue()
    {
        // QuadIn at t=0.5 is 0.25, so the value is 25% of the way from 0 to 100.
        var a = new Animation<double>(0.0, 100.0, OneSecond, DoubleInterpolator.Instance, Easings.QuadIn);
        Assert.Equal(25.0, a.ValueAt(TimeSpan.FromSeconds(0.5)), 6);
    }

    [Fact]
    public void ZeroDuration_SnapsToEnd()
    {
        var a = new Animation<double>(1.0, 9.0, TimeSpan.Zero, DoubleInterpolator.Instance);
        Assert.Equal(9.0, a.ValueAt(TimeSpan.Zero));
        Assert.Equal(9.0, a.ValueAt(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void NegativeDuration_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Animation<double>(0, 1, TimeSpan.FromSeconds(-1), DoubleInterpolator.Instance));
    }

    [Fact]
    public void NullInterpolator_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Animation<double>(0, 1, OneSecond, null!));
    }

    [Fact]
    public void ExposesFromToDurationAndEasing()
    {
        var a = new Animation<double>(3.0, 7.0, OneSecond, DoubleInterpolator.Instance, Easings.CubicOut);
        Assert.Equal(3.0, a.From);
        Assert.Equal(7.0, a.To);
        Assert.Equal(OneSecond, a.Duration);
        Assert.Same(Easings.CubicOut, a.Easing);
    }

    [Fact]
    public void DefaultEasing_IsLinear()
    {
        var a = new Animation<double>(0, 1, OneSecond, DoubleInterpolator.Instance);
        Assert.Same(Easings.Linear, a.Easing);
    }
}
