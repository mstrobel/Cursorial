using Cursorial.Animation;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using static Cursorial.Tests.UI.AnimationMatrix.Anim;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.AnimationMatrix;

// Animation-matrix §10 (storyboards + tracks + imperative ignition) — N102–N121.
public sealed class Section10_Storyboards
{
    // A shown host whose root IS the scope (an Animatable), so a TargetName-less track targets it directly.
    private static (UITestHost Host, Animatable Scope) ShowScope()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 10) });
        var scope = new Animatable();
        var root = new StackPanel();
        root.Children.Add(scope);
        host.ShowRoot(root);
        host.RunUntilIdle();
        return (host, scope);
    }

    private static DoubleTrack Track(StyledProperty<double> property, double to, int ms, double? from = null) =>
        new()
        {
            TargetProperty = property,
            To = to,
            Duration = Ms(ms),
            From = from is { } f ? new Optional<double>(f) : Optional<double>.Unset
        };

    [Fact] // N102: two finite tracks run as independent children; the storyboard completes when the longer finishes
    public void TwoTracks_RunIndependently_CompleteOnLongest()
    {
        var (host, scope) = ShowScope();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(Track(Animatable.VProperty, 10.0, 50));
        sb.Children.Add(Track(Animatable.WProperty, 20.0, 150));
        var completed = 0;
        var handle = sb.Begin(scope);
        handle.Completed += _ => completed++;

        host.AdvanceTime(Ms(80)); // V done, W still running
        Assert.Equal(10.0, scope.V);
        Assert.InRange(scope.W, 0.0, 20.0);
        Assert.Equal(0, completed);

        host.AdvanceTime(Ms(120)); // W done
        Assert.Equal(20.0, scope.W);
        Assert.Equal(1, completed);
        Assert.True(handle.IsCompleted);
    }

    [Fact] // N103: a TargetName track resolves the named element via FindName and targets it
    public void TargetName_ResolvesNamedElement()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 10) });
        using var _ = host;
        var named = new Animatable();
        var root = new StackPanel();
        root.Children.Add(named);
        NameScope.SetNameScope(root, new NameScopeDictionary());
        NameScope.GetNameScope(root)!.Register("target", named);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var sb = new Storyboard();
        sb.Children.Add(new DoubleTrack { TargetName = "target", TargetProperty = Animatable.VProperty, To = 10.0, Duration = Ms(100) });
        sb.Begin(root);

        host.AdvanceTime(Ms(150));
        Assert.Equal(10.0, named.V);
    }

    [Fact] // N104: a TargetName-less track targets the Begin scope itself
    public void NoTargetName_TargetsScope()
    {
        var (host, scope) = ShowScope();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(Track(Animatable.VProperty, 10.0, 100));
        sb.Begin(scope);

        host.AdvanceTime(Ms(150));
        Assert.Equal(10.0, scope.V);
    }

    [Fact] // N105: an unset From snapshots GetValue(property) at track start
    public void FromUnset_SnapshotsAtTrackStart()
    {
        var (host, scope) = ShowScope();
        using var _ = host;
        scope.SetV(5.0); // a non-default base

        var sb = new Storyboard();
        sb.Children.Add(Track(Animatable.VProperty, 15.0, 100)); // From unset ⇒ snapshots 5
        sb.Begin(scope);

        Assert.Equal(5.0, scope.V); // first sample == the snapshot (elapsed 0)
        host.AdvanceTime(Ms(150));
        Assert.Equal(15.0, scope.V);
    }

    [Fact] // N106: an explicit From wins (no snapshot)
    public void ExplicitFrom_Wins()
    {
        var (host, scope) = ShowScope();
        using var _ = host;
        scope.SetV(5.0);

        var sb = new Storyboard();
        sb.Children.Add(Track(Animatable.VProperty, 15.0, 100, from: 100.0)); // explicit From 100
        sb.Begin(scope);

        Assert.Equal(100.0, scope.V); // the explicit From, not the snapshot of 5
    }

    [Fact] // N107: a BeginTime > 0 track is Delayed (property untouched) then runs
    public void BeginTime_DelaysThenRuns()
    {
        var (host, scope) = ShowScope();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(new DoubleTrack { TargetProperty = Animatable.VProperty, From = 2.0, To = 12.0, Duration = Ms(100), BeginTime = Ms(66) });
        sb.Begin(scope);

        host.AdvanceTime(Ms(33)); // inside the stagger window
        Assert.Equal(2.0, scope.V); // holds the track's start value (From) through the stagger (AD16), not the base 0.0

        host.AdvanceTime(Ms(33)); // crosses BeginTime (66ms) exactly
        Assert.Equal(2.0, scope.V); // From, elapsed 0
    }

    [Fact] // Regression: a zero-duration track ordered BEFORE a finite track must NOT complete the group early
    public void ZeroDurationTrackBeforeFinite_DoesNotCompleteEarly()
    {
        var (host, scope) = ShowScope();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(new DoubleTrack { TargetProperty = Animatable.VProperty, From = 0.0, To = 10.0, Duration = TimeSpan.Zero });   // completes at Begin
        sb.Children.Add(new DoubleTrack { TargetProperty = Animatable.WProperty, From = 0.0, To = 100.0, Duration = Ms(200) });        // still mid-flight
        var completed = 0;
        var handle = sb.Begin(scope);
        handle.Completed += _ => completed++;

        host.RunFrame();
        Assert.Equal(10.0, scope.V);        // the zero-duration track reached its end
        Assert.False(handle.IsCompleted);   // but the group must NOT have completed — W is still running
        Assert.Equal(0, completed);

        host.AdvanceTime(Ms(250));          // finish W
        Assert.True(handle.IsCompleted);
        Assert.Equal(1, completed);
    }

    [Fact] // N108: an all-finite storyboard raises Completed once, after the pass
    public void AllFinite_CompletesOnce()
    {
        var (host, scope) = ShowScope();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(Track(Animatable.VProperty, 10.0, 100));
        var completed = 0;
        var handle = sb.Begin(scope);
        handle.Completed += _ => completed++;

        host.AdvanceTime(Ms(150));
        Assert.Equal(1, completed);
        host.AdvanceTime(Ms(150));
        Assert.Equal(1, completed); // never twice
    }

    [Fact] // N109: a perpetual track ⇒ the storyboard never completes and pins the idle gate
    public void PerpetualTrack_NeverCompletes()
    {
        var (host, scope) = ShowScope();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(new DoubleTrack { TargetProperty = Animatable.VProperty, From = 0.0, To = 10.0, Duration = Ms(100), Repeat = RepeatBehavior.Forever });
        var completed = 0;
        var handle = sb.Begin(scope);
        handle.Completed += _ => completed++;

        host.AdvanceTime(Ms(1000));
        Assert.Equal(0, completed);
        Assert.False(handle.IsCompleted);
        Assert.True(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N110: StoryboardHandle.Stop() retracts every child; no Completed
    public void Stop_RetractsChildren_NoCompleted()
    {
        var (host, scope) = ShowScope();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(Track(Animatable.VProperty, 10.0, 100));
        sb.Children.Add(Track(Animatable.WProperty, 20.0, 100));
        var completed = 0;
        var handle = sb.Begin(scope);
        handle.Completed += _ => completed++;
        host.AdvanceTime(Ms(33));

        handle.Stop();
        Assert.Equal(0.0, scope.V); // both bases resurface
        Assert.Equal(0.0, scope.W);
        host.AdvanceTime(Ms(200));
        Assert.Equal(0, completed);
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N111: re-begin the same storyboard on the same scope ⇒ the first instance retires silently
    public void Handoff_SameScope_RetiresFirst()
    {
        var (host, scope) = ShowScope();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(Track(Animatable.VProperty, 10.0, 100));
        var firstCompleted = 0;
        var first = sb.Begin(scope);
        first.Completed += _ => firstCompleted++;
        host.AdvanceTime(Ms(33));

        var second = sb.Begin(scope);
        host.AdvanceTime(Ms(150));
        Assert.Equal(0, firstCompleted);   // the retired first never completes
        Assert.True(second.IsCompleted);
        Assert.Equal(10.0, scope.V);
    }

    [Fact] // N112: two scopes ⇒ independent instances; stopping one leaves the other running
    public void TwoScopes_Independent()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 10) });
        using var _ = host;
        var a = new Animatable();
        var b = new Animatable();
        var root = new StackPanel();
        root.Children.Add(a);
        root.Children.Add(b);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var sb = new Storyboard();
        sb.Children.Add(Track(Animatable.VProperty, 10.0, 100));
        var ha = sb.Begin(a);
        var hb = sb.Begin(b);
        host.AdvanceTime(Ms(33));

        sb.Stop(a);
        Assert.Equal(0.0, a.V); // a retracted
        host.AdvanceTime(Ms(150));
        Assert.Equal(10.0, b.V); // b ran to its end
        Assert.True(hb.IsCompleted);
        Assert.False(ha.IsCompleted);
    }

    [Fact] // N113: detaching the scope retracts every scoped child; no Completed; idle drops (A0's N97 realized)
    public void DetachScope_RetractsAll()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 10) });
        using var _ = host;
        var scope = new Animatable();
        var root = new StackPanel();
        root.Children.Add(scope);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var sb = new Storyboard();
        sb.Children.Add(Track(Animatable.VProperty, 10.0, 100));
        var completed = 0;
        var handle = sb.Begin(scope);
        handle.Completed += _ => completed++;
        host.AdvanceTime(Ms(33));

        root.Children.Remove(scope);
        host.RunUntilIdle();
        Assert.Equal(0, completed);
        Assert.False(handle.IsCompleted);
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N114: a Repeat=Count(3) track runs three cycles then completes at ValueAt(Duration)
    public void RepeatCount_Completes()
    {
        var (host, scope) = ShowScope();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(new DoubleTrack { TargetProperty = Animatable.VProperty, From = 0.0, To = 10.0, Duration = Ms(50), Repeat = RepeatBehavior.Count(3) });
        var completed = 0;
        var handle = sb.Begin(scope);
        handle.Completed += _ => completed++;

        host.AdvanceTime(Ms(200)); // past 150ms total
        Assert.Equal(10.0, scope.V);
        Assert.Equal(1, completed);
    }

    [Fact] // N115: a Source-based track is wrapped by Repeat uniformly
    public void SourceTrack_WrappedByRepeat()
    {
        var (host, scope) = ShowScope();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(new DoubleTrack
        {
            TargetProperty = Animatable.VProperty,
            Source = new DoubleAnimation(0.0, 10.0, Ms(50)),
            Repeat = RepeatBehavior.Count(2)
        });
        var handle = sb.Begin(scope);

        host.AdvanceTime(Ms(150)); // 2 × 50ms = 100ms total
        Assert.Equal(10.0, scope.V);
        Assert.True(handle.IsCompleted);
    }

    [Fact] // N116: a track whose T ≠ the property's value type throws at seal (Begin)
    public void TrackTypeMismatch_ThrowsAtSeal()
    {
        var (host, scope) = ShowScope();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(new Int32Track { TargetProperty = Animatable.VProperty, To = 1, Duration = Ms(100) }); // int track, double property
        Assert.Throws<InvalidOperationException>(() => sb.Begin(scope));
    }

    [Fact] // N117: a Repeat that overflows TimeSpan throws at seal
    public void RepeatOverflow_ThrowsAtSeal()
    {
        var (host, scope) = ShowScope();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(new DoubleTrack
        {
            TargetProperty = Animatable.VProperty,
            To = 10.0,
            Duration = TimeSpan.FromTicks(long.MaxValue / 2),
            Repeat = RepeatBehavior.Count(5)
        });
        Assert.Throws<OverflowException>(() => sb.Begin(scope));
    }

    [Fact] // N118: mutating Children after Begin throws (sealed)
    public void MutateAfterBegin_Throws()
    {
        var (host, scope) = ShowScope();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(Track(Animatable.VProperty, 10.0, 100));
        sb.Begin(scope);

        Assert.Throws<InvalidOperationException>(() => sb.Children.Add(Track(Animatable.WProperty, 5.0, 100)));
    }

    [Fact] // N119: imperative Begin with an unresolvable TargetName throws; no partial group left running
    public void UnresolvableTargetName_Throws_NoPartialGroup()
    {
        var (host, scope) = ShowScope();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(Track(Animatable.VProperty, 10.0, 100));                                  // good (targets scope)
        sb.Children.Add(new DoubleTrack { TargetName = "missing", TargetProperty = Animatable.WProperty, To = 5.0, Duration = Ms(100) });

        Assert.Throws<InvalidOperationException>(() => sb.Begin(scope));
        Assert.Equal(0.0, scope.V); // the good track was rolled back
        Assert.False(host.Scheduler().HasActiveAnimations);
    }

    [Fact] // N120: each track's From resolves independently (one unset-snapshot, one explicit)
    public void MultiTrack_IndependentFrom()
    {
        var (host, scope) = ShowScope();
        using var _ = host;
        scope.SetV(3.0);

        var sb = new Storyboard();
        sb.Children.Add(Track(Animatable.VProperty, 13.0, 100));               // From unset ⇒ snapshots 3
        sb.Children.Add(Track(Animatable.WProperty, 60.0, 100, from: 50.0));   // explicit From 50
        sb.Begin(scope);

        Assert.Equal(3.0, scope.V);
        Assert.Equal(50.0, scope.W);
        host.AdvanceTime(Ms(150));
        Assert.Equal(13.0, scope.V);
        Assert.Equal(60.0, scope.W);
    }

    [Fact] // N121: Stop(scope) with the storyboard not running on that scope is a no-op
    public void Stop_NotRunning_NoOp()
    {
        var (host, scope) = ShowScope();
        using var _ = host;

        var sb = new Storyboard();
        sb.Children.Add(Track(Animatable.VProperty, 10.0, 100));
        sb.Stop(scope); // never begun — must not throw
        Assert.False(host.Scheduler().HasActiveAnimations);
    }
}
