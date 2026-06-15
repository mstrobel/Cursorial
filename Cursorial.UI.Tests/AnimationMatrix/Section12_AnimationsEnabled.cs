using Cursorial.Animation;
using Cursorial.UI;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

namespace Cursorial.Tests.UI.AnimationMatrix;

// Animation-matrix §12 (AnimationsEnabled / reduced motion full flip semantics) — N131–N140.
public sealed class Section12_AnimationsEnabled
{
    [Fact] // N131: Begin a finite (HoldEnd) while disabled ⇒ snaps to end synchronously; Completed enqueued, not raised
    public void DisabledBegin_Finite_SnapsToEnd()
    {
        var (host, _, a) = Shown();
        using var _ = host;
        host.Scheduler().AnimationsEnabled = false;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;

        Assert.Equal(10.0, a.V);                       // end value written synchronously
        Assert.Equal(AnimationState.Holding, handle.State);
        Assert.Equal(0, completed);                    // NOT raised from Begin (AD15)

        host.RunFrame();
        Assert.Equal(1, completed);                    // raised on the next pass
    }

    [Fact] // N132: Begin a finite Fill.Stop while disabled ⇒ end written then retracted; Completed enqueued
    public void DisabledBegin_FillStop_Retracts()
    {
        var (host, _, a) = Shown();
        using var _ = host;
        host.Scheduler().AnimationsEnabled = false;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)),
            new AnimationStartOptions(Fill: FillBehavior.Stop));
        handle.Completed += _ => completed++;

        Assert.Equal(0.0, a.V);                        // base resurfaced
        Assert.Equal(AnimationState.Completed, handle.State);
        host.RunFrame();
        Assert.Equal(1, completed);
    }

    [Fact] // N133: Begin a perpetual while disabled ⇒ no handle, born Stopped, base shows, no Completed
    public void DisabledBegin_Perpetual_BornStopped()
    {
        var (host, _, a) = Shown();
        using var _ = host;
        host.Scheduler().AnimationsEnabled = false;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)).Loop());
        handle.Completed += _ => completed++;

        Assert.Equal(AnimationState.Stopped, handle.State);
        Assert.Equal(0.0, a.V);                        // base shows
        Assert.False(host.Scheduler().HasActiveAnimations);
        host.AdvanceTime(Ms(200));
        Assert.Equal(0, completed);
    }

    [Fact] // N134: flip true→false with a Running instance ⇒ snaps to end at the next Tick; Completed after the pass
    public void Flip_Running_SnapsToEnd()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;
        host.AdvanceTime(Ms(33));
        Assert.Equal(AnimationState.Running, handle.State);

        host.Scheduler().AnimationsEnabled = false;
        host.RunFrame(); // the flip collapses motion this Tick
        Assert.Equal(10.0, a.V);
        Assert.Equal(AnimationState.Holding, handle.State);
        Assert.Equal(1, completed);
    }

    [Fact] // N135: flip true→false snaps a Delayed instance to its end too
    public void Flip_Delayed_SnapsToEnd()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(2.0, 12.0, Ms(100)),
            new AnimationStartOptions(BeginTime: Ms(500)));
        Assert.Equal(AnimationState.Delayed, handle.State);

        host.Scheduler().AnimationsEnabled = false;
        host.RunFrame();
        Assert.Equal(12.0, a.V);
        Assert.Equal(AnimationState.Holding, handle.State);
    }

    [Fact] // N136: flip true→false snaps a Paused instance to its end too
    public void Flip_Paused_SnapsToEnd()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        host.AdvanceTime(Ms(33));
        handle.Pause();
        Assert.Equal(AnimationState.Paused, handle.State);

        host.Scheduler().AnimationsEnabled = false;
        host.RunFrame();
        Assert.Equal(10.0, a.V);
        Assert.Equal(AnimationState.Holding, handle.State);
    }

    [Fact] // N137: flip true→false retracts a perpetual instance (base resurfaces); no Completed
    public void Flip_Perpetual_Retracts()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(3.0, 10.0, Ms(100)).Loop());
        handle.Completed += _ => completed++;
        host.AdvanceTime(Ms(33));

        host.Scheduler().AnimationsEnabled = false;
        host.RunFrame();
        Assert.Equal(AnimationState.Stopped, handle.State);
        Assert.Equal(0.0, a.V);                        // base resurfaced
        Assert.Equal(0, completed);
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N138: flip true→false leaves a Holding instance unaffected
    public void Flip_Holding_Unaffected()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;
        host.AdvanceTime(Ms(150));
        Assert.Equal(AnimationState.Holding, handle.State);
        Assert.Equal(1, completed);

        host.Scheduler().AnimationsEnabled = false;
        host.RunFrame();
        Assert.Equal(AnimationState.Holding, handle.State); // untouched
        Assert.Equal(10.0, a.V);
        Assert.Equal(1, completed);
    }

    [Fact] // N139: false→true is prospective — a new animation runs normally
    public void Flip_BackToTrue_Prospective()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        host.Scheduler().AnimationsEnabled = false;
        host.RunFrame();
        host.Scheduler().AnimationsEnabled = true;

        a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 100.0, Ms(100)));
        host.AdvanceTime(Ms(33));
        Assert.InRange(a.V, 1.0, 99.0); // runs gradually, not snapped
    }

    [Fact] // N140: a disabled Begin's Completed raises exactly once on the next pass, never synchronously
    public void DisabledBegin_CompletedOnNextPassOnce()
    {
        var (host, _, a) = Shown();
        using var _ = host;
        host.Scheduler().AnimationsEnabled = false;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;
        Assert.Equal(0, completed); // not synchronous

        host.RunFrame();
        Assert.Equal(1, completed);
        host.AdvanceTime(Ms(200));
        Assert.Equal(1, completed); // never twice
    }
}
