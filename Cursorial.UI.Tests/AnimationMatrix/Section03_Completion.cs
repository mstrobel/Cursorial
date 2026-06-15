using Cursorial.Animation;
using Cursorial.UI;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

namespace Cursorial.Tests.UI.AnimationMatrix;

// Animation-matrix §3 (completion semantics) — N29–N34.
public sealed class Section03_Completion
{
    [Fact] // N29: Completed raises AFTER the WHOLE sampling pass — a sibling sampled later is already final
    public void Completed_RaisesAfterWholeSamplingPass()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        // V is registered first (sampled at index 0), W second (index 1) — both complete on the same frame.
        // If completion were delivered inline during V's Sample (before W is sampled), a.W would still be
        // mid-flight when V's handler runs; post-pass delivery (AD3) guarantees W is already at its end value.
        var observedW = double.NaN;
        var v = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(50)));
        a.BeginAnimation(Animatable.WProperty, new DoubleAnimation(0.0, 20.0, Ms(50)));
        v.Completed += _ => observedW = a.W;

        host.AdvanceTime(Ms(64)); // both cross 50ms on the same frame
        Assert.Equal(20.0, observedW); // W's frame-N final value was applied before V's completion callback
    }

    [Fact] // N30: two instances completing in the same frame each raise Completed once (drain ordering is N29's job)
    public void TwoCompletingSameFrame_BothRaise()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var vCompleted = 0;
        var wCompleted = 0;
        var v = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(50)));
        var w = a.BeginAnimation(Animatable.WProperty, new DoubleAnimation(0.0, 20.0, Ms(50)));
        v.Completed += _ => vCompleted++;
        w.Completed += _ => wCompleted++;

        host.AdvanceTime(Ms(64)); // both cross 50ms on the same frame
        Assert.Equal(1, vCompleted);
        Assert.Equal(1, wCompleted);
        Assert.Equal(10.0, a.V);
        Assert.Equal(20.0, a.W);
    }

    [Fact] // N31: a Completed handler that Begins a new (zero-duration) animation gets same-frame delivery
    public void CompletedHandler_BeginsNew_SameFrameDelivery()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var wCompleted = 0;
        var v = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(50)));
        v.Completed += _ =>
        {
            var w = a.BeginAnimation(Animatable.WProperty, new DoubleAnimation(0.0, 7.0, TimeSpan.Zero));
            w.Completed += _ => wCompleted++;
        };

        host.AdvanceTime(Ms(64)); // V completes; its handler begins a zero-duration W that self-completes
        Assert.Equal(7.0, a.W);
        Assert.Equal(1, wCompleted); // drained index-over-live-Count, same frame (AD7)
    }

    [Fact] // N32: Stop() before Duration ⇒ no Completed, ever — even after the nominal end time passes
    public void Stop_BeforeDuration_NeverCompletes()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;

        host.AdvanceTime(Ms(40));
        handle.Stop();
        host.AdvanceTime(Ms(200)); // past the nominal end
        Assert.Equal(0, completed);
    }

    [Fact] // N33: Shutdown of a Holding instance retracts it (base resurfaces) and raises NO further Completed
    public void Shutdown_Holding_RetractsNoFurtherCompleted()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;
        host.AdvanceTime(Ms(150));
        Assert.Equal(AnimationState.Holding, handle.State);
        Assert.Equal(1, completed); // the natural HoldEnd completion

        host.Scheduler().Shutdown();
        Assert.Equal(0.0, a.V);     // held value retracted, base resurfaces (§9.6)
        Assert.Equal(1, completed);  // Shutdown raises none
    }

    [Fact] // N34: detach before Duration ⇒ retracted, no Completed
    public void Detach_BeforeDuration_NeverCompletes()
    {
        var (host, root, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;
        host.AdvanceTime(Ms(40));

        root.Children.Remove(a);
        host.RunUntilIdle();

        Assert.Equal(AnimationState.Stopped, handle.State);
        Assert.Equal(0, completed);
    }
}
