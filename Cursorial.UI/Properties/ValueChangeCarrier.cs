namespace Cursorial.UI;

/// <summary>
/// Non-generic base for the pooled value snapshot behind <see cref="UIPropertyChangedEventArgs"/>.
/// The carrier owns <em>copies</em> of the old/new values (snapshotted out of the store entry before
/// dispatch — a carrier over live entries corrupts under reentrancy, design doc §2.1) and is rented
/// from a per-value-type thread-static pool so the steady-state notification path allocates nothing.
/// </summary>
internal abstract class ValueChangeCarrier
{
}

/// <summary>
/// The typed carrier: <see cref="UIPropertyChangedEventArgs.GetOldValue{T}"/> downcasts to this type,
/// which makes a wrong-<typeparamref name="T"/> read throw <see cref="InvalidCastException"/>
/// naturally (matrix M30). Pooled one-deep per thread per <typeparamref name="T"/>; reentrant
/// dispatch rents a second instance, so an outer dispatch's snapshot is never overwritten by a
/// nested one.
/// </summary>
internal sealed class ValueChangeCarrier<T> : ValueChangeCarrier
{
    [ThreadStatic]
    private static ValueChangeCarrier<T>? _cached;

    /// <summary>The snapshotted pre-change value.</summary>
    public T OldValue = default!;

    /// <summary>The snapshotted post-change value.</summary>
    public T NewValue = default!;

    /// <summary>Rents a carrier holding the given snapshot (thread-static one-deep pool).</summary>
    public static ValueChangeCarrier<T> Rent(T oldValue, T newValue)
    {
        var carrier = _cached;
        if (carrier is null)
            carrier = new ValueChangeCarrier<T>();
        else
            _cached = null;

        carrier.OldValue = oldValue;
        carrier.NewValue = newValue;
        return carrier;
    }

    /// <summary>
    /// Returns a carrier to the pool, clearing its slots so it can't root values. Args structs
    /// captured by handlers become invalid after this — change args are valid only for the duration
    /// of the synchronous notification (documented carrier-lifetime contract).
    /// </summary>
    public static void Return(ValueChangeCarrier<T> carrier)
    {
        carrier.OldValue = default!;
        carrier.NewValue = default!;
        _cached = carrier;
    }
}
