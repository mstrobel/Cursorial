using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using Cursorial.UI;
using Cursorial.UI.Data;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>
/// Style matrix §14 — <c>When</c>/<c>DataCondition</c> end-to-end through the styling engine (the P4
/// deliberate-hole close; binding-matrix §13a, B162a–B162h). <c>Style.When</c> arms one
/// <see cref="BindingOperations.Watch"/> per condition at structural match, gates activation on every
/// condition being met (unset ⇒ unmet), reconciles on each delivery through the queued/fixpoint
/// Phase-2 path, and disposes watches at disarm/detach (ledger B16).
/// </summary>
public class Section14_When
{
    /// <summary>The §0.2 <c>R(sel){…}</c> shorthand plus a <c>When</c> conjunction.</summary>
    private static Style RWhen(string selector, (UIProperty Property, object? Value)[] setters, params DataCondition[] conditions)
    {
        var style = R(selector, setters);
        foreach (var condition in conditions)
            style.When.Add(condition);
        return style;
    }

    [Fact]
    public void B162a_WhenUnmet_RuleArmedButInactive()
    {
        using var tree = ShowTree(show: false);
        var vm = new WhenVm { IsDirty = false };
        tree.A.DataContext = vm;
        tree.App.Styles.Add(RWhen("Widget", [(Widget.P, 5)], new DataCondition(new Binding("IsDirty"), true)));
        tree.Host.ShowRoot(tree.Root);

        // Structurally matched (armed) but the condition is unmet (false != true) ⇒ inactive.
        var matched = Assert.Single(StyleDiagnostics.MatchedRules(tree.A));
        Assert.False(matched.IsActive);
        Assert.Equal(0, tree.A.GetValue(Widget.P));
    }

    [Fact]
    public void B162b_WhenMet_RuleActivatesSynchronously()
    {
        using var tree = ShowTree(show: false);
        var vm = new WhenVm { IsDirty = false };
        tree.A.DataContext = vm;
        tree.App.Styles.Add(RWhen("Widget", [(Widget.P, 5)], new DataCondition(new Binding("IsDirty"), true)));
        tree.Host.ShowRoot(tree.Root);
        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);

        vm.IsDirty = true; // the watch delivers synchronously — no frame pump (invariant 1)

        Assert.True(Assert.Single(StyleDiagnostics.MatchedRules(tree.A)).IsActive);
        Assert.Equal(5, tree.A.GetValue(Widget.P));
        probe.AssertSingleNotify(0, 5, BindingPriority.StyleTrigger); // When-guarded ⇒ conditional slot (§0.3)
    }

    [Fact]
    public void B162c_WhenUnmetAgain_RuleDeactivates_StorePromotesBack()
    {
        using var tree = ShowTree(show: false);
        var vm = new WhenVm { IsDirty = true };
        tree.A.DataContext = vm;
        tree.App.Styles.Add(RWhen("Widget", [(Widget.P, 5)], new DataCondition(new Binding("IsDirty"), true)));
        tree.Host.ShowRoot(tree.Root);
        Assert.Equal(5, tree.A.GetValue(Widget.P)); // active at arm

        var probe = StyleProbe<int>.Attach(tree.A, Widget.P);
        vm.IsDirty = false; // unmet ⇒ deactivate; the store promotes the default back (invariant 4)

        Assert.False(Assert.Single(StyleDiagnostics.MatchedRules(tree.A)).IsActive);
        probe.AssertSingleNotify(5, 0, BindingPriority.Default); // store promotes to the default (S127 mechanics)
    }

    [Fact]
    public void B162d_NonMatchingElement_NoWatchArmed()
    {
        using var tree = ShowTree(show: false);
        var vm = new WhenVm { IsDirty = true };
        tree.A.Classes.Add("primary");
        tree.A.DataContext = vm;
        tree.B.DataContext = vm; // same VM, but b never matches ".primary"
        tree.App.Styles.Add(RWhen(".primary", [(Widget.P, 5)], new DataCondition(new Binding("IsDirty"), true)));
        tree.Host.ShowRoot(tree.Root);

        // a matches and (condition met) activates; b never structurally matches ⇒ no armed frame, no watch.
        Assert.Equal(5, tree.A.GetValue(Widget.P));
        Assert.Empty(StyleDiagnostics.MatchedRules(tree.B));
        Assert.Equal(0, tree.B.GetValue(Widget.P));
    }

    [Fact]
    public void B162e_UnresolvedPath_Unmet_ThenResolves_Activates()
    {
        using var tree = ShowTree(show: false);
        var vm = new WhenVm { Sub = null };
        tree.A.DataContext = vm;
        tree.App.Styles.Add(RWhen("Widget", [(Widget.P, 5)], new DataCondition(new Binding("Sub.City"), "NYC")));
        tree.Host.ShowRoot(tree.Root);

        // Unresolved path (Sub == null) ⇒ unmet ⇒ inactive.
        Assert.False(Assert.Single(StyleDiagnostics.MatchedRules(tree.A)).IsActive);
        Assert.Equal(0, tree.A.GetValue(Widget.P));

        // The chain resolves to "NYC" ⇒ the watch re-delivers ⇒ the rule activates.
        vm.Sub = new WhenAddr { City = "NYC" };
        Assert.True(Assert.Single(StyleDiagnostics.MatchedRules(tree.A)).IsActive);
        Assert.Equal(5, tree.A.GetValue(Widget.P));
    }

    [Fact]
    public void B162f_WhenGuardedRule_BeatsUnguarded_BySpecificity()
    {
        using var tree = ShowTree(show: false);
        var vm = new WhenVm { IsDirty = true };
        tree.A.DataContext = vm;

        // Same subject (Widget), same layer; the guarded rule adds 1 classLike (SD5) ⇒ higher sort key.
        tree.App.Styles.Add(R("Widget", (Widget.P, 9)));                                            // unguarded
        tree.App.Styles.Add(RWhen("Widget", [(Widget.P, 5)], new DataCondition(new Binding("IsDirty"), true))); // guarded
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(5, tree.A.GetValue(Widget.P)); // guarded wins while its condition holds

        vm.IsDirty = false; // guarded deactivates ⇒ the unguarded rule's value surfaces
        Assert.Equal(9, tree.A.GetValue(Widget.P));
    }

    [Fact]
    public void B162g_TwoConditions_Conjunction_BothMustHold()
    {
        using var tree = ShowTree(show: false);
        var vm = new WhenVm { IsDirty = false, Age = 0 };
        tree.A.DataContext = vm;
        tree.App.Styles.Add(RWhen(
            "Widget", [(Widget.P, 5)],
            new DataCondition(new Binding("IsDirty"), true),
            new DataCondition(new Binding("Age"), v => v is int n && n > 5)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(0, tree.A.GetValue(Widget.P)); // neither met

        vm.IsDirty = true; // one met, one unmet ⇒ still inactive (no Or)
        Assert.Equal(0, tree.A.GetValue(Widget.P));

        vm.Age = 9; // both met ⇒ active
        Assert.Equal(5, tree.A.GetValue(Widget.P));

        vm.IsDirty = false; // one fails ⇒ deactivates
        Assert.Equal(0, tree.A.GetValue(Widget.P));
    }

    [Fact]
    public void B162h_Detach_RetractsFrame_AndDisposesWatches()
    {
#if DEBUG
        BindingLeakTracker.ResetForTests();
#endif
        using var tree = ShowTree(show: false);
        var vm = new WhenVm { IsDirty = true };
        tree.A.DataContext = vm;
        tree.A.Classes.Add("only"); // the rule matches only `a` ⇒ exactly one When watch in the tree
        tree.App.Styles.Add(RWhen(".only", [(Widget.P, 5)], new DataCondition(new Binding("IsDirty"), true)));
        tree.Host.ShowRoot(tree.Root);
        Assert.Equal(5, tree.A.GetValue(Widget.P)); // active

        var subscribers = vm.SubscriberCount;
        Assert.True(subscribers > 0); // the When watch subscribed to the VM

        // Permanent detach: OnElementDetached retracts the frame (store promotes) and disposes the
        // When watch; TearDown is the registry backstop.
        tree.PaneA.Children.Remove(tree.A);
        tree.A.TearDown();

        Assert.Empty(StyleDiagnostics.MatchedRules(tree.A));
        Assert.Equal(0, tree.A.GetValue(Widget.P));
        Assert.Equal(0, vm.SubscriberCount); // the INPC subscription is gone — no leak

#if DEBUG
        // The DEBUG leak tracker reports no leak for the disposed When watch (its target is alive).
        Assert.DoesNotContain(BindingLeakTracker.Sweep(), l => l.Path == "IsDirty");
        BindingLeakTracker.ResetForTests();
#endif

        // A post-detach VM change neither re-activates nor throws.
        var record = Record.Exception(() => vm.IsDirty = false);
        Assert.Null(record);
        Assert.Equal(0, tree.A.GetValue(Widget.P));
    }

    /// <summary>An INPC viewmodel that exposes its live subscriber count (the leak probe for B162h).</summary>
    [SuppressMessage("ReSharper", "UnusedMember.Local")]
    private sealed class WhenVm : INotifyPropertyChanged
    {
        private PropertyChangedEventHandler? _handlers;
        private bool _isDirty;
        private int _age;
        private WhenAddr? _sub;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => _handlers += value;
            remove => _handlers -= value;
        }

        public int SubscriberCount => _handlers?.GetInvocationList().Length ?? 0;

        public bool IsDirty
        {
            get => _isDirty;
            set => Set(ref _isDirty, value);
        }

        public int Age
        {
            get => _age;
            set => Set(ref _age, value);
        }

        public WhenAddr? Sub
        {
            get => _sub;
            set => Set(ref _sub, value);
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            _handlers?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>The INPC sub-object for the dotted-path When row (B162e).</summary>
    private sealed class WhenAddr : INotifyPropertyChanged
    {
        private string? _city;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string? City
        {
            get => _city;
            set
            {
                if (_city == value)
                    return;
                _city = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(City)));
            }
        }
    }

    [Fact] // a COMPILED descriptor arms a When condition through the same Watch contract: the widened
    // DataCondition accepts it, values deliver through the shared core's watch callback, and the
    // UIProperty-carried step observes the property system (a pure SetValue flips the rule live).
    public void When_CompiledDescriptor_ArmsAndFlipsLive()
    {
        using var tree = ShowTree();
        tree.App.Styles.Add(RWhen("Widget", [(Widget.P, 5)],
            new DataCondition(
                Binding.Compiled(static (Widget w) => w.GetValue(Widget.Q),
                                 relativeSource: RelativeSource.Self),
                value: 9)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(0, tree.A.GetValue(Widget.P)); // unmet: Q is 0, not 9

        tree.A.SetValue(Widget.Q, 9); // a pure property-system write
        Assert.True(tree.Host.RunUntilIdle());
        Assert.Equal(5, tree.A.GetValue(Widget.P)); // the compiled watch delivered — the rule activated

        tree.A.SetValue(Widget.Q, 0);
        Assert.True(tree.Host.RunUntilIdle());
        Assert.Equal(0, tree.A.GetValue(Widget.P)); // and deactivated
    }

    [Fact] // a COMPILED descriptor arms a When condition through the same Watch contract: the widened
    // DataCondition accepts it, values deliver through the shared core's watch callback, and the
    // UIProperty-carried step observes the property system (a pure SetValue flips the rule live).
    public void When_CompiledDescriptor_FromBuilder_ArmsAndFlipsLive()
    {
        using var tree = ShowTree();
        tree.App.Styles.Add(RWhen("Widget", [(Widget.P, 5)],
                                  new DataCondition(
                                      CompiledBinding.Build(static (Widget w) => w.GetValue(Widget.Q),
                                                            relativeSource: RelativeSource.Self)
                                                     .Step(Widget.Q)
                                                     .Build(),
                                      value: 9)));
        tree.Host.ShowRoot(tree.Root);

        Assert.Equal(0, tree.A.GetValue(Widget.P)); // unmet: Q is 0, not 9

        tree.A.SetValue(Widget.Q, 9); // a pure property-system write
        Assert.True(tree.Host.RunUntilIdle());
        Assert.Equal(5, tree.A.GetValue(Widget.P)); // the compiled watch delivered — the rule activated

        tree.A.SetValue(Widget.Q, 0);
        Assert.True(tree.Host.RunUntilIdle());
        Assert.Equal(0, tree.A.GetValue(Widget.P)); // and deactivated
    }
}
