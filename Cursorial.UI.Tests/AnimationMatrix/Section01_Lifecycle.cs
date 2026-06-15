using Cursorial.Animation;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.AnimationMatrix;

// Animation-matrix §§1–8/15 (A0 scheduler core) — N7–N34, N40, N55, N67(perpetual), N96–N101.
public sealed class Section01_Lifecycle
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private static (UITestHost Host, StackPanel Root, Animatable A) Shown()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 10) });
        var a = new Animatable();
        var root = new StackPanel();
        root.Children.Add(a);
        host.ShowRoot(root);
        host.RunUntilIdle();
        return (host, root, a);
    }

    [Fact] // N13: Begin returns a Running handle, first sample written; the From shows immediately (self-sample)
    public void Begin_RunsAndSelfSamples()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(2.0, 12.0, Ms(100)));

        Assert.Equal(AnimationState.Running, handle.State);
        Assert.Equal(2.0, a.V); // From, written synchronously at Begin (AD2)
        Assert.True(host.Application.AnimationScheduler.HasActiveAnimations);
    }

    [Fact] // N14: reaching Duration writes the end value, Holds (default Fill), and raises Completed once
    public void Finite_HoldEnd_CompletesOnce()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;

        host.AdvanceTime(Ms(60));
        Assert.Equal(AnimationState.Running, handle.State);
        Assert.InRange(a.V, 0.0, 10.0); // mid-flight

        host.AdvanceTime(Ms(120)); // well past the end
        Assert.Equal(AnimationState.Holding, handle.State);
        Assert.Equal(10.0, a.V);     // final write == ValueAt(Duration)
        Assert.Equal(1, completed);
        Assert.False(host.Application.AnimationScheduler.HasActiveAnimations); // Holding doesn't pin idle (N10)

        host.AdvanceTime(Ms(200));
        Assert.Equal(1, completed);  // never twice (AD3)
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

    [Fact] // N16/N55: Stop() retracts immediately — base resurfaces, state Stopped, NO Completed
    public void Stop_RetractsWithoutCompleting()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;
        host.AdvanceTime(Ms(40));

        handle.Stop();
        Assert.Equal(AnimationState.Stopped, handle.State);
        Assert.Equal(0.0, a.V);   // base resurfaced
        host.AdvanceTime(Ms(200));
        Assert.Equal(0, completed); // never on Stop
        Assert.False(host.Application.AnimationScheduler.HasActiveAnimations);
    }

    [Fact] // N19: a zero-duration animation reports To at elapsed 0 and completes
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

    [Fact] // N26/N46: a perpetual animation never completes and keeps pinning the idle gate
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
        Assert.True(host.Application.AnimationScheduler.HasActiveAnimations);
    }

    [Fact] // N20/N40: a second Begin on the same (target, property) retires the first — no Completed — and runs
    public void Handoff_RetiresPriorWithoutCompleting()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var firstCompleted = 0;
        var first = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        first.Completed += _ => firstCompleted++;
        host.AdvanceTime(Ms(40));

        var second = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(100.0, 200.0, Ms(100)));
        Assert.Equal(AnimationState.Stopped, first.State);   // retired
        Assert.Equal(AnimationState.Running, second.State);
        Assert.Equal(100.0, a.V);                            // the new animation's From (explicit) shows

        host.AdvanceTime(Ms(200));
        Assert.Equal(0, firstCompleted);                     // the retired one never completes (AD3)
    }

    [Fact] // N96: detaching the target stops its animation silently and drops it from the idle gate
    public void Detach_StopsSilently()
    {
        var (host, root, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;
        host.AdvanceTime(Ms(40));

        root.Children.Remove(a);   // detach
        host.RunUntilIdle();

        Assert.Equal(AnimationState.Stopped, handle.State);
        Assert.Equal(0, completed);
        Assert.False(host.Application.AnimationScheduler.HasActiveAnimations);
    }

    [Fact] // N98/N99: Shutdown retracts everything (no Completed) and is then inert (Begin throws)
    public void Shutdown_RetractsThenInert()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var completed = 0;
        var handle = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        handle.Completed += _ => completed++;

        host.Application.AnimationScheduler.Shutdown();
        Assert.Equal(0, completed);
        Assert.False(host.Application.AnimationScheduler.HasActiveAnimations);
        Assert.Throws<InvalidOperationException>(
            () => a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 1.0, Ms(10))));
    }

    private sealed class Animatable : UIElement
    {
        public static readonly StyledProperty<double> VProperty = UIProperty.Register<Animatable, double>(nameof(V));

        public double V => GetValue(VProperty);
    }
}
