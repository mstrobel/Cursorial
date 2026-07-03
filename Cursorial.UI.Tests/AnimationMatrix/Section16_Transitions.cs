using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

// ReSharper disable InconsistentNaming

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

    [Fact] // AD16: a transition with a Delay HOLDS the old value through the delay, then fades old→new — it must NOT
           // show the already-changed new base during the delay (the Window-inactive-opacity "translucent, then solid,
           // then fades to translucent" flicker)
    public void StyleFlip_WithDelay_HoldsOldValueThroughDelay()
    {
        var (host, _, element) = Show();
        using var _ = host;
        Transition.SetTransitions(element, new TransitionCollection
        {
            new DoubleTransition(Animatable.VProperty) { Duration = Ms(100), Delay = Ms(66) }
        });

        host.Application.Styles.Add(new Style(".hi").Set(Animatable.VProperty, 10.0));
        element.Classes.Add("hi"); // V's base 0→10 ⇒ transition ignites with BeginTime = Delay (66ms)

        Assert.Equal(0.0, element.V);          // ignites holding the OLD value (0), NOT the already-changed base (10)
        host.AdvanceTime(Ms(33));              // inside the delay window
        Assert.Equal(0.0, element.V);          // still the old value — no snap to the new base, no flicker
        host.AdvanceTime(Ms(33));              // crosses the delay (66ms): the fade starts FROM the old value
        Assert.Equal(0.0, element.V);          // first sample at elapsed 0 ⇒ still the old value
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
        host.AdvanceTime(Ms(33));
        // Discriminates a real reverse (fades DOWN from the live ~mid toward 0) from a wrongly-SKIPPED reverse
        // (which would leave the original 0→10 run alive and still CLIMBING, V > mid).
        Assert.True(element.V < mid, $"reverse must fade down from the live value (mid={mid}, now={element.V})");
        host.AdvanceTime(Ms(200));
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

        // Pin the MECHANISM: the winning-base channel must NOT fire (the effective base never moves, LocalValue
        // wins) — distinguishing "didn't fire" from "fired equal and was skipped by Ignite's From==To guard".
        var baseChanges = 0;
        using var sub = element.AddObserver(Animatable.VProperty,
            new RecordingBaseObserver(() => baseChanges++), new ObserverOptions { IncludeBaseChanges = true });

        host.Application.Styles.Add(new Style(".hi").Set(Animatable.VProperty, 10.0));
        element.Classes.Add("hi"); // Style base moves to 10 but LocalValue 5 still wins ⇒ no winning-base change

        Assert.Equal(5.0, element.V);                        // unchanged
        Assert.Equal(0, baseChanges);                        // OnBaseValueChanged never fired
        Assert.False(host.Scheduler().HasActiveAnimations);  // no transition
    }

    private sealed class RecordingBaseObserver(Action onBaseChange) : IValueObserver<double>
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, double oldValue, double newValue, BindingPriority priority) { }
        public void OnBaseValueChanged(UIObject source, UIProperty property, double oldBaseValue, double newBaseValue, bool isAnimated) => onBaseChange();
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

        // Re-attach, then change the base IMMEDIATELY (before the re-arrange go-live drains). A re-parked manager
        // must swallow this — NOT transition it. (Bug-A regression: the sticky-arranged latch went live at once.)
        root.Children.Add(element);
        element.Classes.Remove("hi"); // base 10→0 right after re-attach
        Assert.False(host.Scheduler().HasActiveAnimations); // PARKED — the re-application did not transition
        host.RunUntilIdle();
        Assert.Equal(0.0, element.V);
    }

    [Fact] // Removing the transition itself mid-flight (element stays attached) stops the live fade — it must not
    //       continue to completion after the transition is retracted (e.g. a Window reactivated mid-fade).
    public void RemoveTransitionMidFlight_StopsTheLiveAnimation()
    {
        var (host, _, element) = Show();
        using var _ = host;
        ArmDouble(element);
        host.Application.Styles.Add(new Style(".hi").Set(Animatable.VProperty, 10.0));
        element.Classes.Add("hi");
        host.AdvanceTime(Ms(33)); // mid-fade
        Assert.True(host.Scheduler().HasActiveAnimations);
        Assert.InRange(element.V, 0.1, 9.9);

        Transition.SetTransitions(element, null); // the transition is removed while the element stays attached
        host.RunUntilIdle();

        Assert.False(host.Scheduler().HasActiveAnimations); // the in-flight fade was STOPPED, not left to complete
        Assert.Equal(10.0, element.V);                      // Fill.Stop retract ⇒ the base (the .hi value) resurfaces
    }

    [Fact] // Bug-B regression: arming while Collapsed must not go live on the Collapsed arrange — the
    //       initial application that arrives when later made Visible must not transition.
    public void ArmedWhileCollapsed_DoesNotTransitionInitialApplication()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 10) });
        using var _ = host;
        var element = new Animatable { Visibility = Visibility.Collapsed };
        ArmDouble(element);
        var root = new StackPanel();
        root.Children.Add(element);
        host.Application.Styles.Add(new Style(".hi").Set(Animatable.VProperty, 10.0));
        host.ShowRoot(root);
        host.RunUntilIdle(); // the element only ever had a Collapsed arrange ⇒ NOT live

        element.Classes.Add("hi"); // supplies V's base while Collapsed + parked ⇒ no transition
        Assert.Equal(10.0, element.V);                       // snapped, not faded
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // Multiple transitions on one element fade their properties independently
    public void MultipleTransitions_Independent()
    {
        var (host, _, element) = Show();
        using var _ = host;
        Transition.SetTransitions(element, new TransitionCollection
        {
            new DoubleTransition(Animatable.VProperty) { Duration = Ms(100) },
            new DoubleTransition(Animatable.WProperty) { Duration = Ms(100) }
        });

        element.SetV(10.0);
        element.SetW(20.0);
        Assert.Equal(0.0, element.V); // both fade from their old bases
        Assert.Equal(0.0, element.W);
        host.AdvanceTime(Ms(150));
        Assert.Equal(10.0, element.V);
        Assert.Equal(20.0, element.W);
    }
}
