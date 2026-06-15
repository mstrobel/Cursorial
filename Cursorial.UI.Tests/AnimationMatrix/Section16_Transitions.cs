using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

namespace Cursorial.Tests.UI.AnimationMatrix;

// Animation-matrix §16 (Transitions — implicit animations over the winning-base observer) — N141–N153.
public sealed class Section16_Transitions
{
    private static (UITestHost Host, StackPanel Root, Animatable Element) Show()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 10) });
        var element = new Animatable();
        var root = new StackPanel();
        root.Children.Add(element);
        host.ShowRoot(root);
        host.RunUntilIdle();
        return (host, root, element);
    }

    private static void ArmDouble(Animatable element, int ms = 100) =>
        Transition.SetTransitions(element, new TransitionCollection { new DoubleTransition(Animatable.VProperty) { Duration = Ms(ms) } });

    [Fact] // N141 (the pinned oracle): a Style flip on a live armed element fades FROM the old value, settling at the new
    public void StyleFlip_TransitionsFromOldValue()
    {
        var (host, _, element) = Show();
        using var _ = host;
        ArmDouble(element); // armed on a settled element ⇒ live at once

        host.Application.Styles.Add(new Style(".hi").Set(Animatable.VProperty, 10.0));
        element.Classes.Add("hi"); // V's base 0→10 ⇒ transition ignites synchronously

        Assert.Equal(0.0, element.V);          // starts at the OLD base, not snapped to 10
        host.AdvanceTime(Ms(50));
        Assert.InRange(element.V, 0.1, 9.9);   // mid-fade
        host.AdvanceTime(Ms(100));
        Assert.Equal(10.0, element.V);         // settled at the new base
    }

    [Fact] // N142: transitions armed before the first arrange are parked — the initial style application does NOT fade
    public void InitialApplication_Parked_NoTransition()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 10) });
        using var _ = host;
        var element = new Animatable();
        var root = new StackPanel();
        root.Children.Add(element);

        // Arm + style + class ALL before show, so the initial application happens while parked.
        ArmDouble(element);
        element.Classes.Add("hi");
        host.Application.Styles.Add(new Style(".hi").Set(Animatable.VProperty, 10.0));

        host.ShowRoot(root); // synchronous attach + initial styling (V base→10, parked ⇒ swallowed)
        Assert.Equal(10.0, element.V);                       // snapped to the styled base
        Assert.False(host.Scheduler().HasActiveAnimations);  // NO transition ran for the initial application
    }

    [Fact] // N143: a transition completes — Fill.Stop retracts and the base value shows
    public void Transition_Completes_BaseShows()
    {
        var (host, _, element) = Show();
        using var _ = host;
        ArmDouble(element);

        host.Application.Styles.Add(new Style(".hi").Set(Animatable.VProperty, 10.0));
        element.Classes.Add("hi");
        host.AdvanceTime(Ms(200));

        Assert.Equal(10.0, element.V);                       // the base (Style 10) shows
        Assert.False(host.Scheduler().HasActiveAnimations);  // the run finished (Fill.Stop)
    }

    [Fact] // N144: a base change mid-fade hands off — From = the live interpolated value (smooth reverse)
    public void MidFlightReverse_SnapshotsLiveValue()
    {
        var (host, _, element) = Show();
        using var _ = host;
        ArmDouble(element);
        host.Application.Styles.Add(new Style(".hi").Set(Animatable.VProperty, 10.0));

        element.Classes.Add("hi"); // fade 0→10
        host.AdvanceTime(Ms(50));
        var mid = element.V;
        Assert.InRange(mid, 0.1, 9.9);

        element.Classes.Remove("hi"); // base back to 0 ⇒ reverse handoff from the live value
        Assert.InRange(element.V, 0.1, 9.9); // From = the live ~mid value (not snapped to 0 or 10)
        host.AdvanceTime(Ms(150));
        Assert.Equal(0.0, element.V);  // settles back at the base 0
    }

    [Fact] // N145: AnimationsEnabled == false ⇒ no transition starts (the base snaps)
    public void AnimationsDisabled_NoTransition()
    {
        var (host, _, element) = Show();
        using var _ = host;
        host.Scheduler().AnimationsEnabled = false;
        ArmDouble(element);

        host.Application.Styles.Add(new Style(".hi").Set(Animatable.VProperty, 10.0));
        element.Classes.Add("hi");

        Assert.Equal(10.0, element.V);                       // snapped, no fade
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N147: a Style flip shadowed by a LocalValue does NOT transition (the effective base is unchanged)
    public void StyleFlip_ShadowedByLocalValue_NoTransition()
    {
        var (host, _, element) = Show();
        using var _ = host;
        element.SetV(5.0); // LocalValue — outranks Style
        ArmDouble(element);

        host.Application.Styles.Add(new Style(".hi").Set(Animatable.VProperty, 10.0));
        element.Classes.Add("hi"); // Style base moves to 10 but LocalValue 5 still wins ⇒ no winning-base change

        Assert.Equal(5.0, element.V);                        // unchanged
        Assert.False(host.Scheduler().HasActiveAnimations);  // no transition
    }

    [Fact] // N148: a LocalValue write that moves the effective base transitions
    public void LocalValueWrite_Transitions()
    {
        var (host, _, element) = Show();
        using var _ = host;
        ArmDouble(element);

        element.SetV(10.0); // effective base 0→10 (LocalValue, sub-Animation) ⇒ transition
        Assert.Equal(0.0, element.V); // fades from the old base
        host.AdvanceTime(Ms(150));
        Assert.Equal(10.0, element.V);
    }

    [Fact] // N149: replacing the Transitions collection on a live element keeps it live (no re-park)
    public void ReplaceCollection_StaysLive()
    {
        var (host, _, element) = Show();
        using var _ = host;
        ArmDouble(element, ms: 50);

        // Replace with a fresh collection (new instance) — the element already passed first arrange.
        Transition.SetTransitions(element, new TransitionCollection { new DoubleTransition(Animatable.VProperty) { Duration = Ms(100) } });

        host.Application.Styles.Add(new Style(".hi").Set(Animatable.VProperty, 10.0));
        element.Classes.Add("hi");
        Assert.Equal(0.0, element.V);          // transitions immediately (stayed live across the replace)
        host.AdvanceTime(Ms(150));
        Assert.Equal(10.0, element.V);
    }

    [Fact] // N151: detaching mid-transition retracts silently; re-attach re-parks (its re-application doesn't transition)
    public void Detach_Retracts_ReattachReparks()
    {
        var (host, root, element) = Show();
        using var _ = host;
        ArmDouble(element);
        host.Application.Styles.Add(new Style(".hi").Set(Animatable.VProperty, 10.0));
        element.Classes.Add("hi");
        host.AdvanceTime(Ms(33)); // mid-fade

        root.Children.Remove(element); // detach mid-transition
        host.RunUntilIdle();
        Assert.False(host.Scheduler().HasActiveAnimations); // the in-flight run retracted

        root.Children.Add(element);    // re-attach — its re-application (V base→10) must NOT transition (re-parked)
        host.RunUntilIdle();
        Assert.Equal(10.0, element.V);
        Assert.False(host.Scheduler().HasActiveAnimations);
    }
}
