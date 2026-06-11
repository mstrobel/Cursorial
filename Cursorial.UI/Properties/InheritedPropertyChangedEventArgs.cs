// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The second small change carrier (ledger A3), delivered to
/// <c>UIObject.OnInheritedPropertyChanged</c> on entry-less descendants when an inheriting
/// property's effective value changes on an ancestor (eager-notify, design doc §2.3). The element
/// tree overrides that virtual to run the same <c>PropertyEffects</c> dispatch it runs for ordinary
/// changes (§5.5) — one root write fans out to inheriting descendants without creating store
/// entries.
/// </summary>
/// <remarks>
/// Like <see cref="UIPropertyChangedEventArgs"/>, the args carry copied values in a pooled typed
/// holder and are valid only for the duration of the synchronous notification call — copy values
/// out to retain them.
/// </remarks>
public readonly struct InheritedPropertyChangedEventArgs
{
    private readonly ValueChangeCarrier _carrier;

    internal InheritedPropertyChangedEventArgs(UIProperty property, BindingPriority priority, ValueChangeCarrier carrier)
    {
        Property = property;
        Priority = priority;
        _carrier = carrier;
    }

    /// <summary>The inheriting property whose effective value changed on this node.</summary>
    public UIProperty Property { get; }

    /// <summary>
    /// The new effective lane at this node: <see cref="BindingPriority.Inherited"/> while an
    /// ancestor still contributes, <see cref="BindingPriority.Default"/> after the last
    /// contribution withdrew.
    /// </summary>
    public BindingPriority Priority { get; }

    /// <summary>
    /// The pre-change inherited value. Throws <see cref="InvalidCastException"/> when
    /// <typeparamref name="T"/> is not the property's value type.
    /// </summary>
    public T GetOldValue<T>() => ((ValueChangeCarrier<T>)_carrier).OldValue;

    /// <summary>
    /// The post-change inherited value. Throws <see cref="InvalidCastException"/> when
    /// <typeparamref name="T"/> is not the property's value type.
    /// </summary>
    public T GetNewValue<T>() => ((ValueChangeCarrier<T>)_carrier).NewValue;
}
