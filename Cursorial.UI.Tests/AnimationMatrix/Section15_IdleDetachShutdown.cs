using Cursorial.Animation;
using Cursorial.UI;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.AnimationMatrix;

// Animation-matrix §15 (idle + detach-stop + Shutdown) — N96, N98, N99, N101.
// N97 (storyboard scoped under a detaching element) lands with storyboards (A1); N100 (zero-allocation
// animated slide) is the A1 integration/benchmark row once a composite-shaped animation target exists.
public sealed class Section15_IdleDetachShutdown
{
    [Fact] // N96: detaching the target stops its animation silently and drops it from the idle gate
    public void Detach_StopsSilently()
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
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N98: Shutdown retracts every live handle (bases resurface), evicts them, raises no Completed
    public void Shutdown_RetractsAll()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var v = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        var w = a.BeginAnimation(Animatable.WProperty, new DoubleAnimation(100.0, 200.0, Ms(100)));
        v.Completed += _ => completed++;
        w.Completed += _ => completed++;
        host.AdvanceTime(Ms(40));

        host.Scheduler().Shutdown();
        Assert.Equal(0, completed);
        Assert.Equal(0.0, a.V);     // base resurfaced
        Assert.Equal(0.0, a.W);
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N99: after Shutdown the scheduler is inert — Begin throws
    public void Begin_AfterShutdown_Throws()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        host.Scheduler().Shutdown();
        Assert.Throws<InvalidOperationException>(
            () => a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 1.0, Ms(10))));
    }

    [Fact] // N101: an idle scheduler flips the gate true on Begin (S6 wakes)
    public void Begin_FlipsIdleGateTrue()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        Assert.False(host.Scheduler().HasActiveAnimations);
        a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        Assert.True(host.Scheduler().HasActiveAnimations);
    }
}
