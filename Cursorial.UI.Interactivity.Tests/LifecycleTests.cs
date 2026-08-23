using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Interactivity;

namespace Cursorial.Tests.UI.Interactivity;

/// <summary>
/// P0 lifecycle (design doc §3/§11): behaviors/triggers attach when the host enters an attached tree,
/// detach when it leaves (or is removed from the collection), and re-attach on re-entry; a trigger carries
/// its actions; <c>Fire</c> runs enabled actions with the host as sender.
/// </summary>
public sealed class LifecycleTests
{
    // ── fixtures ─────────────────────────────────────────────────────────────

    private sealed class CountingBehavior : Behavior<UIElement>
    {
        public int Attached, Detached;
        public object? SeenHost;

        protected override void OnAttached()
        {
            Attached++;
            SeenHost = AssociatedObject;
        }

        protected override void OnDetaching() => Detached++;
    }

    private sealed class RecordingAction : TriggerAction
    {
        public readonly List<(object? Sender, object? Parameter)> Invocations = [];

        protected override void Invoke(object? sender, object? parameter) => Invocations.Add((sender, parameter));
    }

    private sealed class ManualTrigger : TriggerBase
    {
        public void FireNow(object? parameter) => Fire(parameter);
    }

    private sealed class ElementOnlyBehavior : Behavior<StackPanel>;

    private static UIHeadlessHost NewHost() =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 10) });

    // ── behaviors ────────────────────────────────────────────────────────────

    [Fact] // populated BEFORE the host enters a tree → attaches when the tree attaches (not before)
    public void Behavior_AttachesWhenHostEntersTree()
    {
        var button = new Button();
        var behavior = new CountingBehavior();
        Interaction.GetBehaviors(button).Add(behavior);

        Assert.Equal(0, behavior.Attached);          // detached element — not live yet
        Assert.Null(behavior.AssociatedObject);

        using var host = NewHost();
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        Assert.Equal(1, behavior.Attached);          // went live with the tree
        Assert.Same(button, behavior.SeenHost);
        Assert.Same(button, behavior.AssociatedObject);
    }

    [Fact] // added AFTER the host is live → attaches immediately
    public void Behavior_AddedWhileLive_AttachesImmediately()
    {
        using var host = NewHost();
        var button = new Button();
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var behavior = new CountingBehavior();
        Interaction.GetBehaviors(button).Add(behavior);

        Assert.Equal(1, behavior.Attached);
        Assert.Same(button, behavior.AssociatedObject);
    }

    [Fact] // removal from a live collection detaches; host tree-detach detaches; re-entry re-attaches
    public void Behavior_DetachesOnRemove_AndOnTreeDetach_ReattachesOnReentry()
    {
        using var host = NewHost();
        var button = new Button();
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var behaviors = Interaction.GetBehaviors(button);
        var removed = new CountingBehavior();
        behaviors.Add(removed);
        Assert.Equal(1, removed.Attached);

        behaviors.Remove(removed);                   // remove-while-live → immediate detach
        Assert.Equal(1, removed.Detached);
        Assert.Null(removed.AssociatedObject);

        var riding = new CountingBehavior();
        behaviors.Add(riding);
        Assert.Equal(1, riding.Attached);

        root.Children.Remove(button);                // host leaves the tree → detach
        host.RunUntilIdle();
        Assert.Equal(1, riding.Detached);
        Assert.Null(riding.AssociatedObject);

        root.Children.Add(button);                   // re-entry → re-attach (§3 parked-and-re-armed)
        host.RunUntilIdle();
        Assert.Equal(2, riding.Attached);
        Assert.Same(button, riding.AssociatedObject);
    }

    [Fact] // Behavior<T> host-type mismatch throws at attach, before any state changes
    public void TypedBehavior_WrongHost_Throws()
    {
        using var host = NewHost();
        var button = new Button();                    // ElementOnlyBehavior requires a StackPanel
        var root = new StackPanel();
        root.Children.Add(button);

        Interaction.GetBehaviors(button).Add(new ElementOnlyBehavior());

        Assert.Throws<InvalidOperationException>(() =>
        {
            host.ShowRoot(root);
            host.RunUntilIdle();
        });
    }

    // ── triggers + actions ───────────────────────────────────────────────────

    [Fact] // a trigger attaches its actions to the same host, and detaches them with itself
    public void Trigger_CarriesItsActions()
    {
        using var host = NewHost();
        var button = new Button();
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var trigger = new ManualTrigger();
        var action = new RecordingAction();
        trigger.Actions.Add(action);
        Interaction.GetTriggers(button).Add(trigger);

        Assert.Same(button, trigger.AssociatedObject);
        Assert.Same(button, action.AssociatedObject); // the action rode the trigger's attach

        Interaction.GetTriggers(button).Remove(trigger);
        Assert.Null(trigger.AssociatedObject);
        Assert.Null(action.AssociatedObject);         // …and its detach
    }

    [Fact] // Fire runs the actions in order with sender = the trigger's host + the firing parameter
    public void Fire_RunsActionsWithHostAndParameter()
    {
        using var host = NewHost();
        var button = new Button();
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var trigger = new ManualTrigger();
        var first = new RecordingAction();
        var second = new RecordingAction();
        trigger.Actions.Add(first);
        trigger.Actions.Add(second);
        Interaction.GetTriggers(button).Add(trigger);

        var payload = new object();
        trigger.FireNow(payload);

        var invocation = Assert.Single(first.Invocations);
        Assert.Same(button, invocation.Sender);
        Assert.Same(payload, invocation.Parameter);
        Assert.Single(second.Invocations);
    }

    [Fact] // IsEnabled=false skips the action (Execute gates; Invoke never runs)
    public void DisabledAction_IsSkipped()
    {
        var trigger = new ManualTrigger();
        var disabled = new RecordingAction { IsEnabled = false };
        var enabled = new RecordingAction();
        trigger.Actions.Add(disabled);
        trigger.Actions.Add(enabled);
        trigger.Attach(new Button());                // direct programmatic attach (no tree needed to fire)

        trigger.FireNow(null);

        Assert.Empty(disabled.Invocations);
        Assert.Single(enabled.Invocations);
    }

    [Fact] // an action added to a LIVE trigger attaches immediately; removed → detaches
    public void Action_AddRemove_WhileTriggerLive()
    {
        var button = new Button();
        var trigger = new ManualTrigger();
        trigger.Attach(button);

        var action = new RecordingAction();
        trigger.Actions.Add(action);
        Assert.Same(button, action.AssociatedObject);

        trigger.Actions.Remove(action);
        Assert.Null(action.AssociatedObject);
    }

    [Fact] // re-hosting: SetBehaviors(null) detaches the old collection's items
    public void SetBehaviorsNull_DetachesItems()
    {
        using var host = NewHost();
        var button = new Button();
        var root = new StackPanel();
        root.Children.Add(button);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var behavior = new CountingBehavior();
        var collection = Interaction.GetBehaviors(button);
        collection.Add(behavior);
        Assert.Equal(1, behavior.Attached);

        Interaction.SetBehaviors(button, null);
        Assert.Equal(1, behavior.Detached);
        Assert.Null(behavior.AssociatedObject);
    }
}
