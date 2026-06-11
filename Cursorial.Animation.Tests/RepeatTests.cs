using Cursorial.Animation;

// ReSharper disable RedundantTypeArgumentsOfMethod

namespace Cursorial.Tests.Animation;

public class RepeatTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // 0→10 over 1s, linear.
    private static Animation<double> Base() =>
        new(0.0, 10.0, S(1), DoubleInterpolator.Instance);

    [Fact]
    public void Repeat_MultipliesDuration_AndSawtooths()
    {
        var a = Base().Repeat(3);

        Assert.Equal(S(3), a.Duration);
        Assert.Equal(5.0, a.ValueAt(S(0.5)));   // first pass
        Assert.Equal(5.0, a.ValueAt(S(1.5)));   // second pass, same phase
        Assert.Equal(0.0, a.ValueAt(S(1.0)));   // iteration boundary wraps back to the start
        Assert.Equal(10.0, a.ValueAt(S(3)));    // forward run ends at the inner end
    }

    [Fact]
    public void PingPong1_PlaysForwardThenBackOnce()
    {
        var a = Base().PingPong(1);

        Assert.Equal(S(2), a.Duration);
        Assert.Equal(5.0, a.ValueAt(S(0.5)));    // forward half
        Assert.Equal(10.0, a.ValueAt(S(1.0)));   // peak at the turn
        Assert.Equal(5.0, a.ValueAt(S(1.5)));    // backward half
        Assert.Equal(0.0, a.ValueAt(S(2.0)));    // ends back at the start
    }

    [Fact]
    public void PingPong_RepeatsTheReversingCycle()
    {
        var a = Base().PingPong(2);

        Assert.Equal(S(4), a.Duration);
        Assert.Equal(10.0, a.ValueAt(S(1.0)));   // first cycle peak
        Assert.Equal(0.0, a.ValueAt(S(2.0)));    // back to start, second cycle begins
        Assert.Equal(10.0, a.ValueAt(S(3.0)));   // second cycle peak
        Assert.Equal(0.0, a.ValueAt(S(4.0)));    // ends back at the start
    }

    [Fact]
    public void Loop_IsPerpetual_AndSawtoothWraps()
    {
        var a = Base().Loop();

        Assert.Equal(TimeSpan.MaxValue, a.Duration);
        Assert.True(((RepeatAnimation<double>) a).IsPerpetual);
        Assert.Equal(5.0, a.ValueAt(S(0.5)));     // first pass
        Assert.Equal(0.0, a.ValueAt(S(1.0)));     // iteration boundary wraps to start
        Assert.Equal(5.0, a.ValueAt(S(1.5)));     // wrapped into the second pass
        Assert.Equal(5.0, a.ValueAt(S(1_000.5))); // still wrapping, far in the future
    }

    [Fact]
    public void AutoReverse_IsPerpetual_AndBouncesForever()
    {
        var a = Base().AutoReverse();

        Assert.Equal(TimeSpan.MaxValue, a.Duration);
        var r = Assert.IsType<RepeatAnimation<double>>(a);
        Assert.True(r.IsPerpetual);
        Assert.True(r.AutoReverse);
        Assert.Equal(5.0, a.ValueAt(S(0.5)));    // forward
        Assert.Equal(10.0, a.ValueAt(S(1.0)));   // turn
        Assert.Equal(5.0, a.ValueAt(S(1.5)));    // back
        Assert.Equal(0.0, a.ValueAt(S(2.0)));    // start of the next bounce
        Assert.Equal(10.0, a.ValueAt(S(3.0)));   // keeps bouncing — no end
        Assert.Equal(10.0, a.ValueAt(S(1_001.0)));
    }

    [Fact]
    public void ClampsOutOfRange()
    {
        var a = Base().Repeat(2);
        Assert.Equal(0.0, a.ValueAt(S(-1)));
        Assert.Equal(10.0, a.ValueAt(S(100)));   // past end, forward run → inner end
    }

    [Fact]
    public void CountBelowOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Base().Repeat(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepeatAnimation<double>(Base(), -1));
    }

    [Fact]
    public void NullInner_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RepeatAnimation<double>(null!, 2));
    }

    [Fact]
    public void ExposesCountAndAutoReverse()
    {
        var r = (RepeatAnimation<double>) Base().PingPong(4);
        Assert.Equal<int?>(4, r.Count);
        Assert.False(r.IsPerpetual);
        Assert.True(r.AutoReverse);

        var loop = (RepeatAnimation<double>) Base().Loop();
        Assert.Null(loop.Count);
        Assert.True(loop.IsPerpetual);
        Assert.False(loop.AutoReverse);
    }
}
