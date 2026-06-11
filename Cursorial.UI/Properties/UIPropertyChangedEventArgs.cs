// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The copied-value change carrier delivered to the virtual <c>UIObject.OnPropertyChanged</c>
/// channel, passed by <c>in</c>. Old/new values are snapshotted into a pooled typed holder before
/// dispatch, so reentrant writes during delivery cannot corrupt the args (design doc §2.1; matrix
/// M252), and reading them via <see cref="GetOldValue{T}"/> / <see cref="GetNewValue{T}"/> never
/// boxes.
/// </summary>
/// <remarks>
/// The args are valid only for the duration of the synchronous notification call — the underlying
/// holder returns to a pool afterwards. Handlers that need the values later must copy them out
/// (the same buffer-lifetime contract the input layer's sink callbacks follow).
/// </remarks>
public readonly struct UIPropertyChangedEventArgs
{
    private readonly ValueChangeCarrier _carrier;

    internal UIPropertyChangedEventArgs(UIProperty property, BindingPriority priority, ValueChangeCarrier carrier)
    {
        Property = property;
        Priority = priority;
        _carrier = carrier;
    }

    /// <summary>The property whose effective value changed.</summary>
    public UIProperty Property { get; }

    /// <summary>
    /// The lane of the new effective value (promotions report the promoted lane) — except
    /// <c>SetCurrentValue</c>, which reports the <em>replaced</em> lane (ledger A11).
    /// </summary>
    public BindingPriority Priority { get; }

    /// <summary>
    /// The pre-change value. Throws <see cref="InvalidCastException"/> when <typeparamref name="T"/>
    /// is not the property's value type (matrix M30).
    /// </summary>
    public T GetOldValue<T>() => ((ValueChangeCarrier<T>)_carrier).OldValue;

    /// <summary>
    /// The post-change value. Throws <see cref="InvalidCastException"/> when <typeparamref name="T"/>
    /// is not the property's value type (matrix M30).
    /// </summary>
    public T GetNewValue<T>() => ((ValueChangeCarrier<T>)_carrier).NewValue;
}
