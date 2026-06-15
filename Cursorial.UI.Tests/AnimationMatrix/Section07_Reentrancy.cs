using Cursorial.Animation;
using Cursorial.UI;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

namespace Cursorial.Tests.UI.AnimationMatrix;

// Animation-matrix §7 (reentrancy) — N61–N66.
public sealed class Section07_Reentrancy
{
    [Fact] // N61: a Stop() from the final write's own change notification ⇒ Sample re-reads State, skips completion
    public void ReentrantStopFromWrite_SkipsCompletion()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        AnimationHandle? handle = null;
        var completed = 0;
        a.VChanged = v =>
        {
            if (handle is not null && v == 10.0) // only the final write produces exactly the end value
                handle.Stop();
        };
        handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;

        host.AdvanceTime(Ms(150));
        Assert.Equal(AnimationState.Stopped, handle.State); // the reentrant Stop won
        Assert.Equal(0, completed);                          // completion branch skipped after the write (AD7)
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N62: a Begin issued from inside another instance's sample callback appends + self-samples (no corruption)
    public void BeginFromSampleCallback_AppendsAndSelfSamples()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var started = false;
        a.VChanged = v =>
        {
            if (!started && v > 5.0) // fires during a Tick sample, mid-flight (not the Begin self-sample)
            {
                started = true;
                a.BeginAnimation(Animatable.WProperty, new DoubleAnimation(100.0, 200.0, Ms(100)));
            }
        };
        a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));

        host.AdvanceTime(Ms(64)); // V crosses 5 → W begins from inside V's sample callback
        Assert.True(started);
        Assert.InRange(a.W, 100.0, 200.0); // W self-sampled from its From — no skipped/double sample
        Assert.True(host.Scheduler().HasActiveAnimations);

        host.AdvanceTime(Ms(200)); // both run to their ends with no corruption
        Assert.Equal(10.0, a.V);
        Assert.Equal(200.0, a.W);
    }

    [Fact] // N63: a Completed handler that stops a sibling keeps the registry consistent (flag-then-sweep)
    public void CompletedHandler_StopsSibling()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var vCompleted = 0;
        var wCompleted = 0;
        var v = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(50)));
        var w = a.BeginAnimation(Animatable.WProperty, new DoubleAnimation(0.0, 20.0, Ms(500)));
        v.Completed += _ => { vCompleted++; w.Stop(); };
        w.Completed += _ => wCompleted++;

        host.AdvanceTime(Ms(64)); // V completes → its handler stops the still-running W
        Assert.Equal(1, vCompleted);
        Assert.Equal(AnimationState.Stopped, w.State);
        Assert.Equal(0, wCompleted);
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N64: Stop() twice is idempotent — the second is a no-op
    public void StopTwice_Idempotent()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;
        host.AdvanceTime(Ms(40));

        handle.Stop();
        handle.Stop(); // must not throw or double-effect
        Assert.Equal(AnimationState.Stopped, handle.State);
        Assert.Equal(0, completed);
    }

    [Fact] // N65: a Completed handler that disposes its own handle ⇒ no double-raise, no exception
    public void CompletedHandler_StopsSelf_NoDoubleRaise()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ =>
        {
            completed++;
            handle.Stop(); // retract from Holding mid-delivery
        };

        host.AdvanceTime(Ms(150));
        Assert.Equal(1, completed);                          // raised exactly once (the raised flag set first)
        Assert.Equal(AnimationState.Stopped, handle.State);   // the in-handler Stop retracted the held value
        Assert.Equal(0.0, a.V);                              // base resurfaced
    }

    [Fact] // N66: a throwing Completed handler — state was updated BEFORE the callback, scheduler stays consistent
    public void ThrowingCallback_StateUpdatedFirst()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        Exception? captured = null;
        host.Application.DispatcherUnhandledException += (_, e) =>
        {
            captured = e.Exception;
            e.Handled = true; // keep the loop alive so we can inspect post-state
        };

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ =>
        {
            completed++;
            throw new InvalidOperationException("boom");
        };

        host.AdvanceTime(Ms(150));
        Assert.IsType<InvalidOperationException>(captured); // the throw reached S6's funnel
        Assert.Equal(1, completed);
        Assert.Equal(AnimationState.Holding, handle.State); // state set before the callback ran (AD7)
        Assert.Equal(10.0, a.V);                            // final value written before the callback
    }
}
