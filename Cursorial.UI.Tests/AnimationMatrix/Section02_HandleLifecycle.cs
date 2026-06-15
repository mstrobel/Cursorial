using Cursorial.Animation;
using Cursorial.UI;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

namespace Cursorial.Tests.UI.AnimationMatrix;

// Animation-matrix §2 (BeginAnimation + handle lifecycle) — N13–N22, N28.
// N20 (handoff) is in §4; N24/N25 (detach) in §15; N26 (perpetual) in §5; N23 (re-seek) / N27
// (Repeat-overflow at construction) land with A2 / the mechanism tests respectively.
public sealed class Section02_HandleLifecycle
{
    [Fact] // N13: Begin returns a Running handle with Target/Property set; the first sample is written synchronously
    public void Begin_RunsAndSelfSamples()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(2.0, 12.0, Ms(100)));

        Assert.Equal(AnimationState.Running, handle.State);
        Assert.Same(a, handle.Target);
        Assert.Same(Animatable.VProperty, handle.Property);
        Assert.Equal(2.0, a.V);            // From, written synchronously at Begin (AD2)
        Assert.True(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N14: reaching Duration writes the end value, Holds (default Fill), and raises Completed exactly once
    public void Finite_HoldEnd_CompletesOnce()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;

        host.AdvanceTime(Ms(48));
        Assert.Equal(AnimationState.Running, handle.State);
        Assert.InRange(a.V, 0.0, 10.0); // mid-flight

        host.AdvanceTime(Ms(120)); // well past the end
        Assert.Equal(AnimationState.Holding, handle.State);
        Assert.Equal(10.0, a.V);    // final write == ValueAt(Duration)
        Assert.Equal(1, completed);

        host.AdvanceTime(Ms(200));
        Assert.Equal(1, completed);  // a Holding instance is never re-sampled ⇒ never re-raises (AD3)
    }

    [Fact] // N15: Fill.Stop retracts at the end — the base resurfaces — Completed still fires once
    public void Finite_FillStop_RetractsAndCompletes()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)),
            new AnimationStartOptions(Fill: FillBehavior.Stop));
        handle.Completed += _ => completed++;

        host.AdvanceTime(Ms(150));
        Assert.Equal(AnimationState.Completed, handle.State);
        Assert.Equal(0.0, a.V);   // base resurfaced (default 0), not the end value
        Assert.Equal(1, completed);
    }

    [Fact] // N16: Stop() on a Holding instance retracts (base resurfaces); Stopped; no Completed
    public void Stop_FromHolding_RetractsWithoutCompleting()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;
        host.AdvanceTime(Ms(150));
        Assert.Equal(AnimationState.Holding, handle.State);
        Assert.Equal(1, completed); // the natural HoldEnd completion already fired

        handle.Stop();
        Assert.Equal(AnimationState.Stopped, handle.State);
        Assert.Equal(0.0, a.V);     // base resurfaced
        Assert.Equal(1, completed);  // Stop never raises Completed (AD3)
    }

    [Fact] // N55: Stop() on a Running instance retracts immediately — base resurfaces, Stopped, no Completed
    public void Stop_FromRunning_RetractsWithoutCompleting()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;
        host.AdvanceTime(Ms(40));

        handle.Stop();
        Assert.Equal(AnimationState.Stopped, handle.State);
        Assert.Equal(0.0, a.V);     // base resurfaced
        host.AdvanceTime(Ms(200));
        Assert.Equal(0, completed); // never on Stop
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N17: within the BeginTime window the instance is Delayed and the property is untouched
    public void Delayed_PropertyUntouched()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(2.0, 12.0, Ms(100)),
            new AnimationStartOptions(BeginTime: Ms(48)));

        host.AdvanceTime(Ms(40)); // two frames (33ms, 40ms at the 33ms cadence), both inside [Start, Start+48ms)
        Assert.Equal(AnimationState.Delayed, handle.State);
        Assert.Equal(0.0, a.V);   // never written — the base default shows
    }

    [Fact] // N18: crossing Start+BeginTime attaches and starts Running; the value leaves From
    public void Delayed_CrossingBeginTime_Runs()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(2.0, 12.0, Ms(100)),
            new AnimationStartOptions(BeginTime: Ms(48)));

        host.AdvanceTime(Ms(48)); // lands a frame exactly on the BeginTime edge ⇒ elapsed == 0
        Assert.Equal(AnimationState.Running, handle.State);
        Assert.Equal(2.0, a.V); // first sample at elapsed 0 ⇒ exactly From (a StartTime off-by-one would miss)
    }

    [Fact] // N19: a zero-duration animation reports To at elapsed 0 and completes in one pass
    public void ZeroDuration_SetsToAndCompletes()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(3.0, 9.0, TimeSpan.Zero));
        handle.Completed += _ => completed++;
        host.RunFrame(); // the completion drains on the sampling pass

        Assert.Equal(9.0, a.V);
        Assert.Equal(AnimationState.Holding, handle.State);
        Assert.Equal(1, completed);
    }

    [Fact] // N21: two properties on one element animate as independent instances with independent completion
    public void TwoProperties_Independent()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var vCompleted = 0;
        var wCompleted = 0;
        var v = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        var w = a.BeginAnimation(Animatable.WProperty, new DoubleAnimation(100.0, 200.0, Ms(200)));
        v.Completed += _ => vCompleted++;
        w.Completed += _ => wCompleted++;
        Assert.NotSame(v, w);

        host.AdvanceTime(Ms(120)); // past V's end, before W's
        Assert.Equal(AnimationState.Holding, v.State);
        Assert.Equal(AnimationState.Running, w.State);
        Assert.Equal(10.0, a.V);
        Assert.InRange(a.W, 100.0, 200.0);
        Assert.Equal(1, vCompleted);
        Assert.Equal(0, wCompleted);

        host.AdvanceTime(Ms(120)); // past W's end
        Assert.Equal(200.0, a.W);
        Assert.Equal(1, wCompleted);
    }

    [Fact] // N22: Target/Property/State reflect the live instance mid-flight
    public void HandleReflectsLiveInstance()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        host.AdvanceTime(Ms(40));

        Assert.Same(a, handle.Target);
        Assert.Same(Animatable.VProperty, handle.Property);
        Assert.Equal(AnimationState.Running, handle.State);
    }

    [Fact] // N28: StopAnimation with nothing running on the property is a no-op
    public void StopAnimation_NothingRunning_NoOp()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        a.StopAnimation(Animatable.VProperty); // must not throw
        Assert.False(host.Scheduler().HasActiveAnimations);
    }
}
