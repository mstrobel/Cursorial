namespace Cursorial.UI;

/// <summary>
/// A direct property: plain field storage on <typeparamref name="TOwner"/> behind getter/setter
/// delegates — the high-frequency internal-state lane (<c>Bounds</c>, <c>IsPressed</c>, scroll
/// offsets) where even sparse-table lookup is unwelcome. No store, no coercion, no styling, no
/// inheritance, no <see cref="PropertyEffects"/> routing, no animation lane (ledger A24 — composite-routed
/// animatable state must be a styled property). Writes go through <c>UIObject.SetAndRaise</c>, which
/// equality-gates and raises the observer + virtual channels only (no metadata <c>Changed</c> channel).
/// </summary>
public sealed class DirectProperty<TOwner, T> : UIProperty
    where TOwner : UIObject
{
    internal DirectProperty(string name, Func<TOwner, T> getter, Action<TOwner, T>? setter, T unsetValue)
        : base(name, typeof(T), typeof(TOwner), inherits: false, isAttached: false, isDirect: true, isReadOnly: setter is null)
    {
        ArgumentNullException.ThrowIfNull(getter);
        Getter = getter;
        Setter = setter;
        UnsetValue = unsetValue;
    }

    /// <summary>Reads the backing field.</summary>
    public Func<TOwner, T> Getter { get; }

    /// <summary>Writes the backing field; <see langword="null"/> for read-only direct properties.</summary>
    public Action<TOwner, T>? Setter { get; }

    /// <summary>
    /// The fallback pushed through <see cref="Setter"/> when a producer yields
    /// <see cref="UIProperty.UnsetValue"/> (the descriptor's fallback contract; deliberately hides
    /// the untyped static sentinel).
    /// </summary>
    public new T UnsetValue { get; }

    internal override object? GetDefaultValueUntyped(Type forType) => UnsetValue;

    internal override object? GetValueUntyped(UIObject host) => Getter((TOwner)host);

    internal override object? GetValueUntyped(UIObject host, BindingPriority maxPriority) => Getter((TOwner)host); // no ladder (A24)

    internal override void SetValueUntyped(UIObject host, object? value)
    {
        if (Setter is not { } setter)
            throw new InvalidOperationException($"Direct property '{this}' is read-only (no setter was registered).");

        // UIProperty.UnsetValue routes the descriptor's registered fallback through the setter (M218).
        setter((TOwner)host, ReferenceEquals(value, UIProperty.UnsetValue) ? UnsetValue : (T)value!);
    }
}
