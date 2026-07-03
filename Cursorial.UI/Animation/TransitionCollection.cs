using System.Collections.ObjectModel;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// The set of <see cref="Transition"/>s attached to an element via <see cref="Transition.TransitionsProperty"/>
/// (design doc §9.5). An ordinary resource object (no deferred content). It <b>seals on arm</b> (when attached
/// and non-null) — mutation afterwards throws; replacing the property re-arms with the new collection.
/// </summary>
public sealed class TransitionCollection : Collection<Transition>
{
    internal bool IsSealed { get; private set; }

    /// <summary>Seals the collection (idempotent) — called by the manager on arm.</summary>
    internal void Seal() => IsSealed = true;

    /// <inheritdoc/>
    protected override void InsertItem(int index, Transition item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThrowIfSealed();
        base.InsertItem(index, item);
    }

    /// <inheritdoc/>
    protected override void SetItem(int index, Transition item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThrowIfSealed();
        base.SetItem(index, item);
    }

    /// <inheritdoc/>
    protected override void RemoveItem(int index)
    {
        ThrowIfSealed();
        base.RemoveItem(index);
    }

    /// <inheritdoc/>
    protected override void ClearItems()
    {
        ThrowIfSealed();
        base.ClearItems();
    }

    private void ThrowIfSealed()
    {
        if (IsSealed)
            throw new InvalidOperationException("The TransitionCollection is sealed (it has been armed on an element); it cannot be mutated.");
    }
}
