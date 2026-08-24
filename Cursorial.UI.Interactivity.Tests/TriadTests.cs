using System.Windows.Input;

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Interactivity;

namespace Cursorial.Tests.UI.Interactivity;

/// <summary>
/// P1 — the MVP triad (design doc §11): <c>EventTrigger</c> (the S3 routed-event hook) +
/// <c>InvokeCommandAction</c> + <c>ChangePropertyAction</c>, end-to-end through real event raises.
/// </summary>
public sealed class TriadTests
{
    private sealed class StubCommand : ICommand
    {
        public readonly List<object?> Executions = [];
        public bool CanExecuteResult = true;
        public int CanExecuteCalls;

        public bool CanExecute(object? parameter)
        {
            CanExecuteCalls++;
            return CanExecuteResult;
        }

        public void Execute(object? parameter) => Executions.Add(parameter);

        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    private static UIHeadlessHost NewHost() =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });

    private static (UIHeadlessHost Host, Button Button) LiveButton()
    {
        var host = NewHost();
        var button = new Button();
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();
        return (host, button);
    }

    // ── EventTrigger ─────────────────────────────────────────────────────────

    [Fact] // EventName resolves via the {Name}Event field convention; a real Click raise fires the actions
    public void EventTrigger_Click_FiresActions()
    {
        var (host, button) = LiveButton();
        using var _ = host;

        var trigger = new EventTrigger("Click");
        var action = new RecordingAction();
        trigger.Actions.Add(action);
        Interaction.GetTriggers(button).Add(trigger);

        button.RaiseEvent(new ClickEventArgs(ButtonBase.ClickEvent, button));

        var invocation = Assert.Single(action.Invocations);
        Assert.Same(button, invocation.Sender);                     // sender = the trigger's host
        Assert.IsType<ClickEventArgs>(invocation.Parameter);        // payload = the routed args
    }

    [Fact] // the typed RoutedEvent path (reflection-free) hooks identically
    public void EventTrigger_TypedRoutedEvent_Fires()
    {
        var (host, button) = LiveButton();
        using var _ = host;

        var trigger = new EventTrigger(ButtonBase.ClickEvent);
        var action = new RecordingAction();
        trigger.Actions.Add(action);
        Interaction.GetTriggers(button).Add(trigger);

        button.RaiseEvent(new ClickEventArgs(ButtonBase.ClickEvent, button));
        Assert.Single(action.Invocations);
    }

    [Fact] // detach unhooks — a raise after removal reaches nothing
    public void EventTrigger_DetachUnhooks()
    {
        var (host, button) = LiveButton();
        using var _ = host;

        var trigger = new EventTrigger("Click");
        var action = new RecordingAction();
        trigger.Actions.Add(action);
        var triggers = Interaction.GetTriggers(button);
        triggers.Add(trigger);
        triggers.Remove(trigger);

        button.RaiseEvent(new ClickEventArgs(ButtonBase.ClickEvent, button));
        Assert.Empty(action.Invocations);
    }

    [Fact] // an unresolvable EventName throws LOUDLY at attach — never a silent no-op trigger
    public void EventTrigger_UnknownEventName_ThrowsAtAttach()
    {
        var (host, button) = LiveButton();
        using var _ = host;

        var trigger = new EventTrigger("NoSuchEvent");
        var ex = Assert.Throws<InvalidOperationException>(() => Interaction.GetTriggers(button).Add(trigger));
        Assert.Contains("NoSuchEvent", ex.Message);
    }

    [Fact] // SourceObject retargets the hook: the trigger lives on one element, listens on another
    public void EventTrigger_SourceObject_Retargets()
    {
        using var host = NewHost();
        var listenerHost = new Button();
        var source = new Button();
        var root = new StackPanel();
        root.Children.Add(listenerHost);
        root.Children.Add(source);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var trigger = new EventTrigger("Click") { SourceObject = source };
        var action = new RecordingAction();
        trigger.Actions.Add(action);
        Interaction.GetTriggers(listenerHost).Add(trigger);

        source.RaiseEvent(new ClickEventArgs(ButtonBase.ClickEvent, source));

        var invocation = Assert.Single(action.Invocations);
        Assert.Same(listenerHost, invocation.Sender); // sender stays the trigger's HOST, not the source
    }

    // ── InvokeCommandAction ──────────────────────────────────────────────────

    [Fact] // the §7 shape end-to-end: Click → InvokeCommandAction executes the command with the event args
    public void InvokeCommand_ExecutesWithEventArgs()
    {
        var (host, button) = LiveButton();
        using var _ = host;

        var command = new StubCommand();
        var trigger = new EventTrigger("Click");
        trigger.Actions.Add(new InvokeCommandAction { Command = command });
        Interaction.GetTriggers(button).Add(trigger);

        button.RaiseEvent(new ClickEventArgs(ButtonBase.ClickEvent, button));

        Assert.IsType<ClickEventArgs>(Assert.Single(command.Executions)); // payload flows as the parameter
    }

    [Fact] // an explicit CommandParameter wins over the firing payload
    public void InvokeCommand_ExplicitParameterWins()
    {
        var (host, button) = LiveButton();
        using var _ = host;

        var command = new StubCommand();
        var marker = new object();
        var trigger = new EventTrigger("Click");
        trigger.Actions.Add(new InvokeCommandAction { Command = command, CommandParameter = marker });
        Interaction.GetTriggers(button).Add(trigger);

        button.RaiseEvent(new ClickEventArgs(ButtonBase.ClickEvent, button));

        Assert.Same(marker, Assert.Single(command.Executions));
    }

    [Fact] // CanExecute false gates Execute (checked at fire time)
    public void InvokeCommand_CanExecuteGates()
    {
        var command = new StubCommand { CanExecuteResult = false };
        var action = new InvokeCommandAction { Command = command };
        action.Execute(sender: null, parameter: null);

        Assert.Equal(1, command.CanExecuteCalls);
        Assert.Empty(command.Executions);
    }

    // ── ChangePropertyAction ─────────────────────────────────────────────────

    [Fact] // a registered UIProperty sets through the property system (LocalValue by default)
    public void ChangeProperty_UIProperty_SetValue()
    {
        var button = new Button();
        var action = new ChangePropertyAction { PropertyName = "Width", Value = 42 };

        action.Execute(sender: button, parameter: null);

        Assert.Equal(42, button.Width);
        Assert.Equal(42, button.ReadLocalValue(UIElement.WidthProperty)); // a real LocalValue write
    }

    [Fact] // SetCurrentValue mode: the effective value changes WITHOUT seizing LocalValue provenance
    public void ChangeProperty_UIProperty_SetCurrentValue()
    {
        var button = new Button();
        var action = new ChangePropertyAction { PropertyName = "Width", Value = 42, Mode = ChangePropertyMode.SetCurrentValue };

        action.Execute(sender: button, parameter: null);

        Assert.Equal(42, button.Width);
        Assert.Same(UIProperty.UnsetValue, button.ReadLocalValue(UIElement.WidthProperty)); // invisible to ReadLocalValue (PD27)
    }

    [Fact] // a plain CLR property sets reflectively; TargetObject overrides the sender
    public void ChangeProperty_ClrProperty_WithTargetObject()
    {
        var sender = new Button();
        var target = new ClrBag();
        var action = new ChangePropertyAction { TargetObject = target, PropertyName = "Payload", Value = "hello" };

        action.Execute(sender: sender, parameter: null);

        Assert.Equal("hello", target.Payload);
    }

    [Fact] // an unresolvable property throws loudly — never a silent no-op
    public void ChangeProperty_UnknownProperty_Throws()
    {
        var action = new ChangePropertyAction { PropertyName = "Nope", Value = 1 };
        var ex = Assert.Throws<InvalidOperationException>(() => action.Execute(sender: new Button(), parameter: null));
        Assert.Contains("Nope", ex.Message);
    }

    private sealed class ClrBag
    {
        public string? Payload { get; set; }
    }

    private sealed class RecordingAction : TriggerAction
    {
        public readonly List<(object? Sender, object? Parameter)> Invocations = [];

        protected override void Invoke(object? sender, object? parameter) => Invocations.Add((sender, parameter));
    }
}
