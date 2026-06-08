using Cursorial.Animation;

namespace Cursorial.Tests.Animation;

public class KeyframeAnimationTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static KeyframeAnimation<double> Ramp() => new(
    [
        new Keyframe<double>(S(0), 0.0),
        new Keyframe<double>(S(1), 10.0),
        new Keyframe<double>(S(2), 30.0),
    ], DoubleInterpolator.Instance);

    [Fact]
    public void Duration_IsLastKeyframeTime()
    {
        Assert.Equal(S(2), Ramp().Duration);
    }

    [Fact]
    public void InterpolatesWithinEachSegment()
    {
        var a = Ramp();
        Assert.Equal(0.0, a.ValueAt(S(0)));
        Assert.Equal(5.0, a.ValueAt(S(0.5)));    // first segment 0→10
        Assert.Equal(10.0, a.ValueAt(S(1)));     // shared keyframe
        Assert.Equal(20.0, a.ValueAt(S(1.5)));   // second segment 10→30
        Assert.Equal(30.0, a.ValueAt(S(2)));
    }

    [Fact]
    public void HoldsBeforeFirstAndAfterLast()
    {
        var a = Ramp();
        Assert.Equal(0.0, a.ValueAt(S(-1)));
        Assert.Equal(30.0, a.ValueAt(S(5)));
    }

    [Fact]
    public void SortsKeyframesByTime()
    {
        var a = new KeyframeAnimation<double>(
        [
            new Keyframe<double>(S(2), 30.0),
            new Keyframe<double>(S(0), 0.0),
            new Keyframe<double>(S(1), 10.0),
        ], DoubleInterpolator.Instance);

        Assert.Equal(5.0, a.ValueAt(S(0.5)));
        Assert.Equal(20.0, a.ValueAt(S(1.5)));
    }

    [Fact]
    public void PerSegmentEasing_ShapesTheIncomingSegment()
    {
        // Segment into the second keyframe uses QuadIn; at the segment midpoint that is 0.25 of 0→10.
        var a = new KeyframeAnimation<double>(
        [
            new Keyframe<double>(S(0), 0.0),
            new Keyframe<double>(S(1), 10.0, Easings.QuadIn),
        ], DoubleInterpolator.Instance);

        Assert.Equal(2.5, a.ValueAt(S(0.5)), 6);
    }

    [Fact]
    public void SingleKeyframe_IsConstant()
    {
        var a = new KeyframeAnimation<double>([new Keyframe<double>(S(1), 42.0)], DoubleInterpolator.Instance);
        Assert.Equal(S(1), a.Duration);
        Assert.Equal(42.0, a.ValueAt(S(0)));
        Assert.Equal(42.0, a.ValueAt(S(1)));
        Assert.Equal(42.0, a.ValueAt(S(5)));
    }

    [Fact]
    public void CoincidentKeyframes_SnapToLaterValue()
    {
        var a = new KeyframeAnimation<double>(
        [
            new Keyframe<double>(S(0), 0.0),
            new Keyframe<double>(S(1), 5.0),
            new Keyframe<double>(S(1), 99.0),   // step at t=1
            new Keyframe<double>(S(2), 99.0),
        ], DoubleInterpolator.Instance);

        Assert.Equal(99.0, a.ValueAt(S(1)));   // the zero-length segment snaps to the later value
    }

    [Fact]
    public void Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new KeyframeAnimation<double>([], DoubleInterpolator.Instance));
    }

    [Fact]
    public void NegativeTime_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new KeyframeAnimation<double>([new Keyframe<double>(S(-1), 0.0)], DoubleInterpolator.Instance));
    }
}
