using Cursorial.Animation;
using Cursorial.UI;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.AnimationMatrix;

// Animation-matrix §5 (PingPong / AutoReverse + Repeat, driven through the scheduler) — N44–N47.
// N42/N43 (forward/reverse midpoint values) and N48 (Repeat wrapping an arbitrary source uniformly) are pure
// RepeatAnimation mechanism — Cursorial.Animation.Tests (RepeatTests).
public sealed class Section05_RepeatPingPong
{
    [Fact] // N44: a finite there-and-back (PingPong 1) finishes back at the start value A == ValueAt(Duration)
    public void AutoReverseFinite_FinalWriteIsStart()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty,
            new DoubleAnimation(2.0, 12.0, Ms(50)).PingPong(1)); // Duration = 100ms (forward + back)
        handle.Completed += _ => completed++;

        host.AdvanceTime(Ms(150));
        Assert.Equal(AnimationState.Holding, handle.State);
        Assert.Equal(2.0, a.V);     // ValueAt(Duration) of an auto-reverse cycle is the start (AD5)
        Assert.Equal(1, completed);
    }

    [Fact] // N45: Repeat(3) runs three forward cycles then completes; final == ValueAt(Duration) == To
    public void RepeatCount_CompletesAtForwardEnd()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty,
            new DoubleAnimation(0.0, 10.0, Ms(50)).Repeat(3)); // Duration = 150ms
        handle.Completed += _ => completed++;

        host.AdvanceTime(Ms(80)); // mid second cycle — still running
        Assert.Equal(AnimationState.Running, handle.State);

        host.AdvanceTime(Ms(120)); // past the third cycle
        Assert.Equal(AnimationState.Holding, handle.State);
        Assert.Equal(10.0, a.V);
        Assert.Equal(1, completed);
    }

    [Fact] // N46: Loop() (Repeat forever) is perpetual — never completes and pins the idle gate
    public void Perpetual_NeverCompletes()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)).Loop());
        handle.Completed += _ => completed++;

        host.AdvanceTime(Ms(1000));
        Assert.Equal(AnimationState.Running, handle.State);
        Assert.Equal(0, completed);
        Assert.True(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N47: PingPong(2) — two there-and-back cycles — also finishes at the start value A
    public void AutoReverseRepeat_FinalWriteIsStart()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty,
            new DoubleAnimation(2.0, 12.0, Ms(50)).PingPong(2)); // Duration = 200ms
        handle.Completed += _ => completed++;

        host.AdvanceTime(Ms(260));
        Assert.Equal(AnimationState.Holding, handle.State);
        Assert.Equal(2.0, a.V);     // final == start A (AD5)
        Assert.Equal(1, completed);
    }
}
