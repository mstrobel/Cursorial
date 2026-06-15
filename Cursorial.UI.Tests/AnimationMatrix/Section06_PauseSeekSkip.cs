using Cursorial.Animation;
using Cursorial.UI;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

namespace Cursorial.Tests.UI.AnimationMatrix;

// Animation-matrix §6 (Pause / Resume / Seek / SkipToEnd — the A2 handle ops; Stop is in §2) — N49–N59.
// N60 (StoryboardHandle.SkipToEnd all-finite validation) rides the deferred storyboard-timeline ops.
public sealed class Section06_PauseSeekSkip
{
    [Fact] // N49: Pause from Running ⇒ Paused, the value holds, drops out of the idle gate
    public void Pause_FromRunning_Holds()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 100.0, Ms(99)));
        host.AdvanceTime(Ms(33));
        var atPause = a.V;

        handle.Pause();
        Assert.Equal(AnimationState.Paused, handle.State);
        Assert.False(host.Scheduler().HasActiveAnimations); // Paused excluded (AD11)

        host.AdvanceTime(Ms(200));
        Assert.Equal(atPause, a.V); // value holds while paused
    }

    [Fact] // N50: Pause from Delayed is legal — it pauses the delay window
    public void Pause_FromDelayed_Legal()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(2.0, 12.0, Ms(100)),
            new AnimationStartOptions(BeginTime: Ms(100)));
        Assert.Equal(AnimationState.Delayed, handle.State);

        handle.Pause();
        Assert.Equal(AnimationState.Paused, handle.State);
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N51: Resume restores the state and shifts StartTime by the pause span — no time jump
    public void Resume_ContinuesWithoutJump()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 99.0, Ms(99)));
        host.AdvanceTime(Ms(33)); // ~33
        handle.Pause();
        host.AdvanceTime(Ms(300)); // paused — no progress

        handle.Resume();
        Assert.Equal(AnimationState.Running, handle.State);
        host.AdvanceTime(Ms(33)); // elapsed continues from ~33 → ~66, NOT jumped to the end
        Assert.InRange(a.V, 55.0, 77.0);

        host.AdvanceTime(Ms(99));
        Assert.Equal(99.0, a.V); // runs to its end
    }

    [Fact] // N52: Seek jumps to ValueAt(offset), clamped; works while Paused
    public void Seek_JumpsClamped_WorksPaused()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 100.0, Ms(100)));

        handle.Seek(Ms(50));
        Assert.Equal(50.0, a.V);

        handle.Seek(Ms(500)); // clamps to Duration
        Assert.Equal(100.0, a.V);

        handle.Seek(Ms(-10)); // clamps to 0
        Assert.Equal(0.0, a.V);

        handle.Pause();
        handle.Seek(Ms(25)); // works while paused
        Assert.Equal(25.0, a.V);
        Assert.Equal(AnimationState.Paused, handle.State);
    }

    [Fact] // N53: Seek(t ≥ 0) on a Delayed instance starts it
    public void Seek_Delayed_Starts()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 100.0, Ms(100)),
            new AnimationStartOptions(BeginTime: Ms(100)));
        Assert.Equal(AnimationState.Delayed, handle.State);

        handle.Seek(Ms(25));
        Assert.Equal(AnimationState.Running, handle.State);
        Assert.Equal(25.0, a.V);
    }

    [Fact] // N54: Seek(t < Duration) on a Holding instance re-enters Running WITHOUT re-raising Completed
    public void Seek_Holding_ReEntersRunning_NoRecomplete()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 100.0, Ms(100)));
        handle.Completed += _ => completed++;
        host.AdvanceTime(Ms(150));
        Assert.Equal(AnimationState.Holding, handle.State);
        Assert.Equal(1, completed);

        handle.Seek(Ms(40));
        Assert.Equal(AnimationState.Running, handle.State);
        Assert.Equal(40.0, a.V);
        host.AdvanceTime(Ms(200));
        Assert.Equal(1, completed); // never re-raised (AD3)
    }

    [Fact] // N56: SkipToEnd snaps a finite animation to its end value and completes (one pass)
    public void SkipToEnd_Finite_SnapsAndCompletes()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 100.0, Ms(100)));
        handle.Completed += _ => completed++;

        handle.SkipToEnd();
        Assert.Equal(100.0, a.V);
        Assert.Equal(AnimationState.Holding, handle.State);

        host.RunFrame(); // the enqueued completion drains
        Assert.Equal(1, completed);
    }

    [Fact] // N57: SkipToEnd on a Delayed instance attaches, writes the end value, and completes
    public void SkipToEnd_Delayed_SnapsAndCompletes()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(3.0, 9.0, Ms(100)),
            new AnimationStartOptions(BeginTime: Ms(100)));
        handle.Completed += _ => completed++;

        handle.SkipToEnd();
        Assert.Equal(9.0, a.V);
        host.RunFrame();
        Assert.Equal(1, completed);
    }

    [Fact] // N58: SkipToEnd on Holding/Completed/Stopped is a no-op
    public void SkipToEnd_AlreadyEnded_NoOp()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        // Holding
        var holding = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        host.AdvanceTime(Ms(150));
        var completed = 0;
        holding.Completed += _ => completed++;
        holding.SkipToEnd();
        host.RunFrame();
        Assert.Equal(0, completed); // already completed before the subscribe; no new completion
        Assert.Equal(AnimationState.Holding, holding.State);

        // Stopped
        var stopped = a.BeginAnimation(Animatable.WProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        stopped.Stop();
        stopped.SkipToEnd(); // must not throw
        Assert.Equal(AnimationState.Stopped, stopped.State);
    }

    [Fact] // N59: SkipToEnd on a perpetual animation throws
    public void SkipToEnd_Perpetual_Throws()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)).Loop());
        Assert.Throws<InvalidOperationException>(handle.SkipToEnd);
    }
}
