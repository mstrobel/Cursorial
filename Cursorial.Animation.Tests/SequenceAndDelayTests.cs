using Cursorial.Animation;

namespace Cursorial.Tests.Animation;

// Animation-matrix §13 (mechanism combinators, MECH) + the perpetual/overflow guards (N67–N70, N82–N88).
public class SequenceAndDelayTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static Animation<double> Lin(double from, double to, double seconds) =>
        new(from, to, S(seconds), DoubleInterpolator.Instance);

    // ───────────────────────────── DelayAnimation ─────────────────────────────

    [Fact] // N82/N83: hold the first frame through the delay, then play; Duration = delay + inner
    public void Delay_HoldsFirstFrame_ThenPlays()
    {
        var a = Lin(0, 10, 1).Delay(S(0.5));

        Assert.Equal(S(1.5), a.Duration);
        Assert.Equal(0.0, a.ValueAt(S(0.25))); // held during the delay
        Assert.Equal(0.0, a.ValueAt(S(0.5)));  // boundary still held
        Assert.Equal(5.0, a.ValueAt(S(1.0)));  // inner at t=0.5
        Assert.Equal(10.0, a.ValueAt(S(1.5))); // inner at its end
        Assert.Equal(10.0, a.ValueAt(S(9.0))); // past the end clamps to the inner's last frame
    }

    [Fact] // N80: clamp before the start
    public void Delay_BeforeStart_HoldsFirstFrame()
        => Assert.Equal(0.0, new DelayAnimation<double>(Lin(0, 10, 1), S(0.5)).ValueAt(S(-2)));

    [Fact] // N68: a perpetual inner stays perpetual — no overflow on delay + MaxValue
    public void Delay_PerpetualInner_StaysPerpetual()
        => Assert.Equal(TimeSpan.MaxValue, Lin(0, 10, 1).Loop().Delay(S(0.5)).Duration);

    [Fact]
    public void Delay_Negative_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new DelayAnimation<double>(Lin(0, 10, 1), S(-1)));

    // ───────────────────────────── SequenceAnimation ─────────────────────────────

    [Fact] // N84: the next child wins at a shared boundary (half-open intervals); Duration = checked sum
    public void Sequence_NextChildWinsAtBoundary()
    {
        var a = Lin(0, 10, 1).Then(Lin(100, 200, 1));

        Assert.Equal(S(2), a.Duration);
        Assert.Equal(5.0, a.ValueAt(S(0.5)));    // first child mid
        Assert.Equal(100.0, a.ValueAt(S(1.0)));  // boundary → the SECOND child's start, not the first's end (10)
        Assert.Equal(150.0, a.ValueAt(S(1.5)));  // second child mid
    }

    [Fact] // N85: a non-final zero-duration child owns an empty interval and is never sampled
    public void Sequence_ZeroDurationNonFinalChild_NeverSampled()
    {
        var marker = Lin(-1, -1, 0);             // a zero-duration child; would yield -1 if ever sampled
        var a = new SequenceAnimation<double>(Lin(0, 10, 1), marker, Lin(100, 200, 1));

        Assert.Equal(S(2), a.Duration);
        for (var t = 0.0; t <= 2.0; t += 0.1)
            Assert.True(a.ValueAt(S(t)) >= 0.0, $"marker value -1 leaked at t={t}");
        Assert.Equal(100.0, a.ValueAt(S(1.0)));  // the boundary lands on the third child, skipping the marker
    }

    [Fact] // N86: at/after the total, hold the last child's last frame
    public void Sequence_ClampsToLastFrame()
    {
        var a = Lin(0, 10, 1).Then(Lin(100, 200, 1));
        Assert.Equal(200.0, a.ValueAt(S(2)));
        Assert.Equal(200.0, a.ValueAt(S(9)));
        Assert.Equal(0.0, a.ValueAt(S(-1)));     // before the start
    }

    [Fact] // N69: a perpetual child is legal only in the last position
    public void Sequence_PerpetualNonFinal_Throws()
        => Assert.Throws<ArgumentException>(() =>
            new SequenceAnimation<double>(Lin(0, 10, 1).Loop(), Lin(100, 200, 1)));

    [Fact]
    public void Sequence_PerpetualLast_IsPerpetual()
        => Assert.Equal(TimeSpan.MaxValue, new SequenceAnimation<double>(Lin(0, 10, 1), Lin(100, 200, 1).Loop()).Duration);

    [Fact]
    public void Sequence_Empty_Throws()
        => Assert.Throws<ArgumentException>(() => new SequenceAnimation<double>());

    [Fact] // N70: finite children whose durations sum past TimeSpan.MaxValue are checked — throws, never wraps
    public void Sequence_FiniteSumOverflow_Throws()
    {
        // Two finite (non-MaxValue, so no perpetual short-circuit) children whose tick sum overflows long.
        var half = new Animation<double>(0.0, 1.0, TimeSpan.FromTicks(long.MaxValue / 2 + 1), DoubleInterpolator.Instance);
        Assert.Throws<OverflowException>(() => new SequenceAnimation<double>(half, half));
    }

    [Fact] // N88: .Then composes equivalently to the constructor
    public void Then_Chains_ThreeSteps()
    {
        var a = Lin(0, 10, 1).Then(Lin(10, 20, 1)).Then(Lin(20, 30, 1));
        Assert.Equal(S(3), a.Duration);
        Assert.Equal(10.0, a.ValueAt(S(1.0)));
        Assert.Equal(20.0, a.ValueAt(S(2.0)));
        Assert.Equal(30.0, a.ValueAt(S(3.0)));
    }
}
