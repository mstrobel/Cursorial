using System.ComponentModel;

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Interactivity;

using Binding = Cursorial.UI.Data.Binding;

namespace Cursorial.Tests.UI.Interactivity;

/// <summary>Regressions for the module's adversarial-audit findings (one test per confirmed fix).</summary>
public sealed class AuditRegressionTests
{
    private static UIHeadlessHost NewHost() =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });

    private sealed class CountingBehavior : Behavior<UIElement>
    {
        public int Attached, Detached;

        protected override void OnAttached() => Attached++;

        protected override void OnDetaching() => Detached++;
    }

    // ── collection hosting (exactly-one-host + no cross-kill) ────────────────

    [Fact] // audit: assigning a hosted collection to a SECOND element silently stole it — now a loud throw
    public void Collection_SecondHost_Throws()
    {
        using var host = NewHost();
        var b1 = new Button();
        var b2 = new Button();
        var root = new StackPanel();
        root.Children.Add(b1);
        root.Children.Add(b2);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var coll = Interaction.GetBehaviors(b1);
        coll.Add(new CountingBehavior());

        var ex = Assert.Throws<InvalidOperationException>(() => Interaction.SetBehaviors(b2, coll));
        Assert.Contains("exactly one", ex.Message);
    }

    [Fact] // audit: clearing a STALE slot unhosted a collection live elsewhere — now guarded by the host check.
    // The stale slot arises from the failed steal itself: the store sets the value BEFORE HostTo throws.
    public void Collection_StaleSlotClear_DoesNotCrossKill()
    {
        using var host = NewHost();
        var b1 = new Button();
        var b2 = new Button();
        var root = new StackPanel();
        root.Children.Add(b1);
        root.Children.Add(b2);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var behavior = new CountingBehavior();
        var coll = Interaction.GetBehaviors(b1);
        coll.Add(behavior);
        Assert.Equal(1, behavior.Attached);

        Assert.Throws<InvalidOperationException>(() => Interaction.SetBehaviors(b2, coll)); // the failed steal…
        Interaction.SetBehaviors(b2, null); // …left b2's slot stale; clearing it must NOT touch b1's association

        Assert.Same(b1, coll.Host);               // the live association survived
        Assert.Equal(0, behavior.Detached);       // no cross-kill
    }

    // ── teardown sweep (the InputBindings leg for Interactivity) ─────────────

    private sealed class Vm : INotifyPropertyChanged
    {
        public string? Name { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public int SubscriberCount => PropertyChanged?.GetInvocationList().Length ?? 0;
    }

    [Fact] // audit: a Source-anchored binding on an ACTION survived TearDown, pinning the graph via the VM's
    // INPC list — the collection's ITearDownParticipant now sweeps item bindings (incl. trigger actions)
    public void TearDown_SweepsActionBindings()
    {
        using var host = NewHost();
        var vm = new Vm();
        var button = new Button();
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var action = new InvokeCommandAction();
        var trigger = new EventTrigger("Click");
        trigger.Actions.Add(action);
        Interaction.GetTriggers(button).Add(trigger);
        BindingOperations.SetBinding(action, InvokeCommandAction.CommandParameterProperty,
            new Binding("Name") { Source = vm });

        Assert.Equal(1, vm.SubscriberCount); // the action's binding subscribed

        root.Children.Remove(button);
        host.RunUntilIdle();
        button.TearDown();

        Assert.Equal(0, vm.SubscriberCount); // swept — the VM no longer pins the graph
    }

    // ── snapshot walks + re-entrancy ─────────────────────────────────────────

    private sealed class RemovingBehavior : Behavior<UIElement>
    {
        public BehaviorCollection? Owner;
        public Behavior? Remove;

        protected override void OnDetaching() => Owner?.Remove(Remove!);
    }

    [Fact] // audit: an OnDetaching removing an EARLIER item shifted a trailing sibling out of the walk —
    // it stayed attached forever; the snapshot walk detaches every item
    public void DetachAll_MutatingOnDetaching_DetachesAllSiblings()
    {
        using var host = NewHost();
        var button = new Button();
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var coll = Interaction.GetBehaviors(button);
        var a = new CountingBehavior();
        var mutator = new RemovingBehavior { Owner = coll, Remove = a };
        var c = new CountingBehavior();
        coll.Add(a);
        coll.Add(mutator);
        coll.Add(c);
        mutator.Remove = a;

        root.Children.Remove(button);
        host.RunUntilIdle();

        Assert.Equal(1, c.Detached);              // the trailing sibling detached (was silently skipped)
        Assert.Null(c.AssociatedObject);
    }

    private sealed class SelfRemovingBehavior : Behavior<UIElement>
    {
        public BehaviorCollection? Owner;
        public int Detachings;

        protected override void OnDetaching()
        {
            Detachings++;
            Owner?.Remove(this); // the one-shot-behavior pattern — must NOT recurse/double-detach
        }
    }

    [Fact] // audit: self-removal inside OnDetaching recursed unboundedly — the _detaching guard bounds it to ONE
    public void SelfRemoval_InOnDetaching_RunsOnce()
    {
        using var host = NewHost();
        var button = new Button();
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var coll = Interaction.GetBehaviors(button);
        var b = new SelfRemovingBehavior { Owner = coll };
        coll.Add(b);

        root.Children.Remove(button);
        host.RunUntilIdle();

        Assert.Equal(1, b.Detachings);            // exactly one logical detach
        Assert.Empty(coll);                       // and the removal took
    }

    // ── attach rollback ──────────────────────────────────────────────────────

    [Fact] // audit: a throwing OnAttached (bad EventName) left a half-attached zombie — now rolled back
    public void ThrowingOnAttached_RollsBack()
    {
        var trigger = new EventTrigger("NoSuchEvent");
        Assert.Throws<InvalidOperationException>(() => trigger.Attach(new Button()));
        Assert.Null(trigger.AssociatedObject);    // no zombie — a later valid attach starts clean
    }

    // ── DataTrigger string-value coercion ────────────────────────────────────

    private sealed class BoolVm : INotifyPropertyChanged
    {
        private bool _isDirty;

        public bool IsDirty
        {
            get => _isDirty;
            set { _isDirty = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class RecordingAction : TriggerAction
    {
        public readonly List<object?> Payloads = [];

        protected override void Invoke(object? sender, object? parameter) => Payloads.Add(parameter);
    }

    [Fact] // audit: XAML Value="True" (string) never matched a bound bool — silent never-fire; now coerced (WPF parity)
    public void DataTrigger_StringValue_MatchesTypedDelivery()
    {
        using var host = NewHost();
        var vm = new BoolVm();
        var button = new Button { DataContext = vm };
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var trigger = new DataTrigger { Binding = new Binding("IsDirty"), Value = "True" }; // the XAML string form
        var action = new RecordingAction();
        trigger.Actions.Add(action);
        Interaction.GetTriggers(button).Add(trigger);

        vm.IsDirty = true;
        host.RunUntilIdle();

        Assert.Single(action.Payloads);           // "True" coerced to bool and matched
    }

    // ── InvokeCommandAction null parameter ───────────────────────────────────

    private sealed class StubCommand : System.Windows.Input.ICommand
    {
        public readonly List<object?> Executions = [];

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Executions.Add(parameter);

        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    [Fact] // audit: an EXPLICIT null CommandParameter fell through to the event args — now null passes as null
    public void InvokeCommand_ExplicitNullParameter_PassesNull()
    {
        var command = new StubCommand();
        var action = new InvokeCommandAction { Command = command, CommandParameter = null };

        action.Execute(sender: null, parameter: new object());

        Assert.Null(Assert.Single(command.Executions));
    }

    // ── ChangePropertyAction SetCurrentValue validation ──────────────────────

    [Fact] // audit: a wrong-typed Value surfaced as a bare TargetInvocation/InvalidCast — now contextual
    public void ChangeProperty_SetCurrentValue_WrongType_ContextualError()
    {
        var action = new ChangePropertyAction { PropertyName = "Opacity", Value = "0.5", Mode = ChangePropertyMode.SetCurrentValue };
        var ex = Assert.Throws<InvalidOperationException>(() => action.Execute(new Button(), null));
        Assert.Contains("Opacity", ex.Message);
        Assert.Contains("not assignable", ex.Message);
    }

    // ── ViewRegistration shared-sink arbitration ─────────────────────────────

    private sealed class SinkVm : IResourceRootSink
    {
        public UIElement? Root;

        public void SetResourceRoot(UIElement? root) => Root = root;
    }

    [Fact] // audit: detaching a STALE view nulled a live view's registration on a shared VM — now ownership-guarded
    public void ViewRegistration_StaleDetach_DoesNotClobberLiveView()
    {
        using var host = NewHost();
        var vm = new SinkVm();
        var viewA = new StackPanel { DataContext = vm };
        var viewB = new StackPanel { DataContext = vm };
        Interaction.GetBehaviors(viewA).Add(new ViewRegistration());
        Interaction.GetBehaviors(viewB).Add(new ViewRegistration());
        var root = new StackPanel();
        root.Children.Add(viewA);
        root.Children.Add(viewB);
        host.ShowRoot(root);
        host.RunUntilIdle();

        Assert.Same(viewB, vm.Root);              // last-wins (documented)

        root.Children.Remove(viewA);              // the STALE view leaves
        host.RunUntilIdle();

        Assert.Same(viewB, vm.Root);              // the live registration survives (was clobbered to null)
    }
}
