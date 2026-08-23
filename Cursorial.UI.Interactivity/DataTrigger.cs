using Cursorial.UI;
using Cursorial.UI.Data;

namespace Cursorial.UI.Interactivity;

/// <summary>
/// Fires its ACTIONS when a bound condition becomes true (design doc §4) — the imperative complement to
/// <c>Style.When</c> (same substrate: a binding descriptor armed through
/// <see cref="BindingOperations.Watch"/>; different effect: <c>Style.When</c> installs retractable value
/// frames, a <c>DataTrigger</c> runs actions). Semantics mirror <c>DataCondition</c>: the condition is
/// met when the delivered value <see cref="object.Equals(object?, object?)"/> <see cref="Value"/>
/// (<see cref="Negate"/> inverts); an <c>UnsetValue</c>/unresolved delivery is UNMET, fail-closed, even
/// negated. Actions fire on the unmet→met EDGE (an initially-met arm fires once), with the delivered
/// value as the payload. The watch lives for the attached lifetime and is disposed at detach.
/// </summary>
public class DataTrigger : TriggerBase<UIElement>
{
    private IBindingWatch? _watch;
    private bool _met;

    /// <summary>The condition's binding DESCRIPTOR (anchored on the host element at attach).</summary>
    public BindingBase? Binding { get; set; }

    /// <summary>The value the delivery is compared against (<c>Equals</c>; null compares to null).</summary>
    public object? Value { get; set; }

    /// <summary>Inverts the comparison (an unresolved delivery stays UNMET — fail-closed, as in <c>DataCondition</c>).</summary>
    public bool Negate { get; set; }

    /// <inheritdoc/>
    protected override void OnAttached()
    {
        var binding = Binding
            ?? throw new InvalidOperationException("DataTrigger requires a Binding.");

        _met = false;
        _watch = BindingOperations.Watch(AssociatedObject!, binding, OnDelivery);
    }

    /// <inheritdoc/>
    protected override void OnDetaching()
    {
        _watch?.Dispose();
        _watch = null;
        _met = false;
    }

    private void OnDelivery(object? delivered)
    {
        // UnsetValue ⇒ unmet, fail-closed even negated (the DataCondition rule — an unknown never fires).
        bool met;
        if (ReferenceEquals(delivered, UIProperty.UnsetValue))
            met = false;
        else
            met = Equals(delivered, Value) ^ Negate;

        var fire = met && !_met;
        _met = met;
        if (fire)
            Fire(delivered);
    }
}
