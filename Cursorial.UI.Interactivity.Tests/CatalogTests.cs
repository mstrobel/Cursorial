using System.ComponentModel;

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Interactivity;

namespace Cursorial.Tests.UI.Interactivity;

/// <summary>
/// P2 — the action catalog + <c>DataTrigger</c> (design doc §4/§5/§11): <c>CallMethodAction</c>,
/// <c>BeginStoryboardAction</c>/<c>ControlStoryboardAction</c>, <c>SetFocusAction</c>, and the
/// data-condition trigger over the <c>BindingOperations.Watch</c> substrate.
/// </summary>
public sealed class CatalogTests
{
    private static UIHeadlessHost NewHost() =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });

    // ── CallMethodAction ─────────────────────────────────────────────────────

    public sealed class MethodBag
    {
        public int PlainCalls;
        public readonly List<(object? Sender, object? Parameter)> ContextCalls = [];

        public void Plain() => PlainCalls++;

        public void WithContext(object? sender, object? parameter) => ContextCalls.Add((sender, parameter));
    }

    [Fact] // the (sender, parameter) overload is preferred and receives the firing context
    public void CallMethod_PrefersContextOverload()
    {
        var bag = new MethodBag();
        var action = new CallMethodAction { TargetObject = bag, MethodName = "WithContext" };
        var sender = new Button();
        var payload = new object();

        action.Execute(sender, payload);

        var call = Assert.Single(bag.ContextCalls);
        Assert.Same(sender, call.Sender);
        Assert.Same(payload, call.Parameter);
    }

    [Fact] // a parameterless method invokes when no context overload exists
    public void CallMethod_Parameterless()
    {
        var bag = new MethodBag();
        var action = new CallMethodAction { TargetObject = bag, MethodName = "Plain" };

        action.Execute(sender: null, parameter: null);

        Assert.Equal(1, bag.PlainCalls);
    }

    [Fact] // an unresolvable method throws loudly
    public void CallMethod_UnknownMethod_Throws()
    {
        var action = new CallMethodAction { TargetObject = new MethodBag(), MethodName = "Nope" };
        var ex = Assert.Throws<InvalidOperationException>(() => action.Execute(null, null));
        Assert.Contains("Nope", ex.Message);
    }

    // ── storyboard actions ───────────────────────────────────────────────────

    [Fact] // Begin runs the storyboard on the host scope and retains the handle; ControlStoryboardAction stops it
    public void BeginAndControlStoryboard_EndToEnd()
    {
        using var host = NewHost();
        var button = new Button();
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var storyboard = new Storyboard
        {
            Children =
            {
                new DoubleTrack
                {
                    TargetProperty = UIElement.OpacityProperty,
                    To = 0.25,
                    Duration = TimeSpan.FromMilliseconds(200),
                },
            },
        };

        var begin = new BeginStoryboardAction { Storyboard = storyboard };
        Assert.Null(begin.LastHandle);

        begin.Execute(sender: button, parameter: null);
        Assert.NotNull(begin.LastHandle); // the run began on the host scope

        var control = new ControlStoryboardAction { BeginAction = begin, Operation = ControlStoryboardOperation.SkipToEnd };
        control.Execute(sender: button, parameter: null);
        host.RunFrame();

        Assert.Equal(0.25, button.Opacity, 3); // skipped to the final value

        var stop = new ControlStoryboardAction { BeginAction = begin, Operation = ControlStoryboardOperation.Stop };
        stop.Execute(sender: button, parameter: null); // stop after completion — a benign no-op, never a throw
    }

    [Fact] // a Begin without a Storyboard, and a Control with neither pair nor storyboard, throw loudly
    public void StoryboardActions_Misconfiguration_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new BeginStoryboardAction().Execute(new Button(), null));
        Assert.Throws<InvalidOperationException>(() => new ControlStoryboardAction().Execute(new Button(), null));
    }

    [Fact] // a ControlStoryboardAction before anything began is a benign no-op (the instance may just not exist yet)
    public void ControlStoryboard_BeforeBegin_IsNoOp()
    {
        var control = new ControlStoryboardAction { BeginAction = new BeginStoryboardAction(), Operation = ControlStoryboardOperation.Pause };
        control.Execute(sender: new Button(), parameter: null); // no throw
    }

    // ── SetFocusAction ───────────────────────────────────────────────────────

    [Fact] // moves keyboard focus through the S3 FocusManager
    public void SetFocus_MovesFocus()
    {
        using var host = NewHost();
        var first = new Button();
        var second = new Button();
        var root = new StackPanel();
        root.Children.Add(first);
        root.Children.Add(second);
        host.ShowRoot(root);
        host.RunUntilIdle();

        Assert.True(first.IsFocused); // activation auto-focused the first tab stop

        new SetFocusAction { Target = second }.Execute(sender: first, parameter: null);

        Assert.True(second.IsFocused);
        Assert.False(first.IsFocused);
    }

    // ── DataTrigger ──────────────────────────────────────────────────────────

    private sealed class Vm : INotifyPropertyChanged
    {
        private string? _status;

        public string? Status
        {
            get => _status;
            set
            {
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class RecordingAction : TriggerAction
    {
        public readonly List<object?> Payloads = [];

        protected override void Invoke(object? sender, object? parameter) => Payloads.Add(parameter);
    }

    [Fact] // fires on the unmet→met EDGE (not on every delivery), payload = the delivered value
    public void DataTrigger_FiresOnMetEdge()
    {
        using var host = NewHost();
        var vm = new Vm { Status = "idle" };
        var button = new Button { DataContext = vm };
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var trigger = new DataTrigger { Binding = new Cursorial.UI.Data.Binding("Status"), Value = "armed" };
        var action = new RecordingAction();
        trigger.Actions.Add(action);
        Interaction.GetTriggers(button).Add(trigger);

        Assert.Empty(action.Payloads);       // initially unmet — no fire

        vm.Status = "armed";
        host.RunUntilIdle();
        Assert.Equal("armed", Assert.Single(action.Payloads)); // the unmet→met edge fired once

        vm.Status = "armed"; // re-delivery of the same met value — no NEW edge
        host.RunUntilIdle();
        Assert.Single(action.Payloads);

        vm.Status = "idle";  // met→unmet — no fire
        host.RunUntilIdle();
        Assert.Single(action.Payloads);

        vm.Status = "armed"; // a second unmet→met edge
        host.RunUntilIdle();
        Assert.Equal(2, action.Payloads.Count);
    }

    [Fact] // an initially-MET condition fires once at arm
    public void DataTrigger_InitiallyMet_FiresOnce()
    {
        using var host = NewHost();
        var vm = new Vm { Status = "armed" };
        var button = new Button { DataContext = vm };
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var trigger = new DataTrigger { Binding = new Cursorial.UI.Data.Binding("Status"), Value = "armed" };
        var action = new RecordingAction();
        trigger.Actions.Add(action);
        Interaction.GetTriggers(button).Add(trigger);
        host.RunUntilIdle();

        Assert.Single(action.Payloads);
    }

    [Fact] // detach disposes the watch — a later flip reaches nothing
    public void DataTrigger_DetachDisposesWatch()
    {
        using var host = NewHost();
        var vm = new Vm { Status = "idle" };
        var button = new Button { DataContext = vm };
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var trigger = new DataTrigger { Binding = new Cursorial.UI.Data.Binding("Status"), Value = "armed" };
        var action = new RecordingAction();
        trigger.Actions.Add(action);
        var triggers = Interaction.GetTriggers(button);
        triggers.Add(trigger);
        triggers.Remove(trigger);

        vm.Status = "armed";
        host.RunUntilIdle();
        Assert.Empty(action.Payloads);
    }

    [Fact] // Negate inverts; a trigger with no Binding throws loudly at attach
    public void DataTrigger_NegateAndMisconfiguration()
    {
        using var host = NewHost();
        var vm = new Vm { Status = "idle" };
        var button = new Button { DataContext = vm };
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var trigger = new DataTrigger { Binding = new Cursorial.UI.Data.Binding("Status"), Value = "armed", Negate = true };
        var action = new RecordingAction();
        trigger.Actions.Add(action);
        Interaction.GetTriggers(button).Add(trigger);
        host.RunUntilIdle();

        Assert.Single(action.Payloads); // "idle" != "armed", negated ⇒ MET at arm

        Assert.Throws<InvalidOperationException>(() =>
            Interaction.GetTriggers(button).Add(new DataTrigger())); // no Binding — loud
    }
}
