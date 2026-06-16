using Cursorial.Animation;
using Cursorial.UI;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.AnimationMatrix;

// Animation-matrix §1 (frame clock + driver + idle gate) — N1, N3, N7–N12.
// N4 (begin-from-sample count snapshot) is pinned in §7 (reentrancy); N5/N6 (TickNewlyStarted edge
// ignition) land with storyboards (A1).
public sealed class Section01_FrameClockDriver
{
    [Fact] // N1: BeginFrame freezes the clock — every read between BeginFrame calls sees one timestamp
    public void BeginFrame_FreezesClock()
    {
        var scheduler = new AnimationScheduler();

        scheduler.BeginFrame(new FrameTime(1, Ms(100), Ms(16)));
        Assert.Equal(Ms(100), scheduler.Clock.Now);
        Assert.Equal(Ms(100), scheduler.Clock.Now); // still frozen — no mid-frame re-sample

        scheduler.BeginFrame(new FrameTime(2, Ms(116), Ms(16)));
        Assert.Equal(Ms(116), scheduler.Clock.Now);
    }

    [Fact] // N3: a Running instance is sampled exactly once per Tick (one ValueAt beyond the Begin self-sample)
    public void RunningInstance_SampledOncePerTick()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var counting = new CountingAnimation(new DoubleAnimation(0.0, 10.0, Ms(1000)));
        a.BeginAnimation(Animatable.VProperty, counting);
        Assert.Equal(1, counting.SampleCount); // the synchronous self-sample at Begin (AD2)

        host.RunFrame();
        Assert.Equal(2, counting.SampleCount); // exactly one more per frame

        host.RunFrame();
        Assert.Equal(3, counting.SampleCount);
    }

    [Fact] // N7: no instances, no timers ⇒ the idle gate is open (S6 may sleep)
    public void Idle_NothingActive_False()
    {
        var (host, _, _) = Shown();
        using var _ = host;
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N8: one Running instance pins the idle gate
    public void Idle_Running_True()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        Assert.True(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N9: a Delayed instance (BeginTime in the future) pins the gate so S6 can't sleep through it
    public void Idle_Delayed_True()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)),
            new AnimationStartOptions(BeginTime: Ms(500)));
        Assert.Equal(AnimationState.Delayed, handle.State);
        Assert.True(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N10: a Holding instance (finite, HoldEnd, completed) costs nothing — does not pin the gate
    public void Idle_Holding_False()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        host.AdvanceTime(Ms(150));
        Assert.Equal(AnimationState.Holding, handle.State);
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N11: once finished, Completed (Fill.Stop) and Stopped instances are evicted ⇒ no longer pin the gate
    //       (they are swept from the registry the moment they finish; Paused — still resident — is A2)
    public void Idle_CompletedAndStopped_False()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)),
            new AnimationStartOptions(Fill: FillBehavior.Stop));
        host.AdvanceTime(Ms(150));
        Assert.Equal(AnimationState.Completed, completed.State);
        Assert.False(host.Scheduler().HasActiveAnimations);

        var stopped = a.BeginAnimation(Animatable.WProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        stopped.Stop();
        Assert.Equal(AnimationState.Stopped, stopped.State);
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N12: a running UITimer pins the gate even with no animation instances (§9.8)
    public void Idle_RunningTimer_True()
    {
        var (host, _, _) = Shown();
        using var _ = host;

        using var timer = UITimer.Start(Ms(500), () => { });
        Assert.True(host.Scheduler().HasActiveAnimations);
    }
}
