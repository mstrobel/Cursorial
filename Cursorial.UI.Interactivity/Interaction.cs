using Cursorial.UI;

namespace Cursorial.UI.Interactivity;

/// <summary>
/// The attach points (design doc §2): <see cref="BehaviorsProperty"/> and <see cref="TriggersProperty"/> —
/// attached-property collections in the S8 <c>Transition.Transitions</c> shape, so the XAML lanes lower them
/// with no new node kind (§7). The getters are get-or-create (WPF parity), so
/// <c>&lt;Interaction.Triggers&gt;</c> collection content fills an existing collection.
/// </summary>
public sealed class Interaction
{
    private Interaction()
    {
    }

    /// <summary>The attached behaviors of a host element.</summary>
    public static readonly AttachedProperty<BehaviorCollection?> BehaviorsProperty =
        UIProperty.RegisterAttached<Interaction, UIElement, BehaviorCollection?>(
            "Behaviors", changed: OnBehaviorsChanged);

    /// <summary>The attached triggers of a host element.</summary>
    public static readonly AttachedProperty<TriggerCollection?> TriggersProperty =
        UIProperty.RegisterAttached<Interaction, UIElement, TriggerCollection?>(
            "Triggers", changed: OnTriggersChanged);

    /// <summary>Gets (creating and associating on first access) the element's behavior collection.</summary>
    public static BehaviorCollection GetBehaviors(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (element.GetValue(BehaviorsProperty) is { } existing)
            return existing;

        var collection = new BehaviorCollection();
        element.SetValue(BehaviorsProperty, collection); // the changed callback hosts it
        return collection;
    }

    /// <summary>Sets the element's behavior collection (re-hosting it; the old collection detaches).</summary>
    public static void SetBehaviors(UIElement element, BehaviorCollection? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(BehaviorsProperty, value);
    }

    /// <summary>Gets (creating and associating on first access) the element's trigger collection.</summary>
    public static TriggerCollection GetTriggers(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (element.GetValue(TriggersProperty) is { } existing)
            return existing;

        var collection = new TriggerCollection();
        element.SetValue(TriggersProperty, collection);
        return collection;
    }

    /// <summary>Sets the element's trigger collection (re-hosting it; the old collection detaches).</summary>
    public static void SetTriggers(UIElement element, TriggerCollection? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TriggersProperty, value);
    }

    private static void OnBehaviorsChanged(UIObject sender, BehaviorCollection? oldValue, BehaviorCollection? newValue)
    {
        oldValue?.Unhost();
        if (newValue is not null && sender is UIElement element)
            newValue.HostTo(element);
    }

    private static void OnTriggersChanged(UIObject sender, TriggerCollection? oldValue, TriggerCollection? newValue)
    {
        oldValue?.Unhost();
        if (newValue is not null && sender is UIElement element)
            newValue.HostTo(element);
    }
}
