using Cursorial.Animation;
using Cursorial.UI;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.AnimationMatrix;

// Animation-matrix §4 (handoff — SnapshotAndReplace) — N20, N37, N39, N40.
// N35/N36/N38/N41 (presented-value From snapshot, transition non-ignition) ride the A1 track factory /
// A3 transitions — in A0 every BeginAnimation takes a fully-built animation with an explicit From.
public sealed class Section04_Handoff
{
    [Fact] // N20: a second Begin on the same (element, property) retires the first — no Completed — and runs
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
        Assert.Equal(100.0, a.V);                            // the new animation's From shows

        host.AdvanceTime(Ms(200));
        Assert.Equal(0, firstCompleted);                     // the retired one never completes (AD3)
    }

    [Fact] // N37 (AD16): a delayed replacement retracts the old handle at Begin AND immediately occupies the
           // property at its own start value (ValueAt(0) = From) through the delay — not the base
    public void Handoff_Delayed_RetractsOldImmediately()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var first = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        host.AdvanceTime(Ms(40));
        Assert.InRange(a.V, 0.0, 10.0); // the first animation is presenting a value

        var second = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(100.0, 200.0, Ms(100)),
            new AnimationStartOptions(BeginTime: Ms(48)));
        Assert.Equal(AnimationState.Stopped, first.State);
        Assert.Equal(AnimationState.Delayed, second.State);
        Assert.Equal(100.0, a.V); // old retracted at Begin; the replacement holds its From through the delay (≠ base 0.0)

        host.AdvanceTime(Ms(48)); // lands a frame exactly on the replacement's BeginTime edge ⇒ elapsed == 0
        Assert.Equal(AnimationState.Running, second.State);
        Assert.Equal(100.0, a.V); // the replacement's From, sampled at elapsed 0
    }

    [Fact] // N39: an explicit From on the replacement wins (no presented-value snapshot in A0)
    public void Handoff_ExplicitFromWins()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        host.AdvanceTime(Ms(40)); // presented value is ~4, not 0

        a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(100.0, 200.0, Ms(100)));
        Assert.Equal(100.0, a.V); // the explicit From, not a snapshot of the presented ~4
    }

    [Fact] // N40: retarget twice in one frame — only the last instance is live; intermediates retire silently
    public void Handoff_RapidRetarget_OnlyLastLive()
    {
        var (host, _, a) = Shown();
        using var _ = host;

        var firstCompleted = 0;
        var secondCompleted = 0;
        var first = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(0.0, 10.0, Ms(100)));
        first.Completed += _ => firstCompleted++;
        var second = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(20.0, 30.0, Ms(100)));
        second.Completed += _ => secondCompleted++;
        var third = a.BeginAnimation(Animatable.VProperty, new DoubleAnimation(40.0, 50.0, Ms(100)));

        Assert.Equal(AnimationState.Stopped, first.State);
        Assert.Equal(AnimationState.Stopped, second.State);
        Assert.Equal(AnimationState.Running, third.State);
        Assert.Equal(40.0, a.V);

        host.AdvanceTime(Ms(200));
        Assert.Equal(0, firstCompleted);
        Assert.Equal(0, secondCompleted);
    }
}
