using System.Collections;

namespace Cursorial.UI;

/// <summary>
/// The owner-wired children collection (design doc §5.4): every add/remove sets the child's visual
/// <b>and</b> logical parent on the owner (index = paint order), throws on null / duplicate /
/// attached-elsewhere children, and invalidates the owner's measure (the cached z-order array
/// invalidation joins at T3). <c>ItemsPresenter</c>-style visual-only adoption uses
/// <c>UIElement.AddVisualChildOnly</c> instead (punch 43).
/// Lives in <c>Cursorial.UI</c> beside <see cref="UIElement"/> — a deliberate deviation from WPF,
/// which places its counterpart in <c>System.Windows.Controls</c> (design doc §1.3).
/// </summary>
public sealed class UIElementCollection : IList<UIElement>, IReadOnlyList<UIElement>
{
    private readonly UIElement _owner;
    private readonly List<UIElement> _items = [];

    /// <summary>Creates the collection wired to <paramref name="owner"/> (the visual and logical parent of every item).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is null.</exception>
    public UIElementCollection(UIElement owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    /// <inheritdoc cref="IReadOnlyCollection{T}.Count"/>
    public int Count => _items.Count;

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <inheritdoc cref="IList{T}.this"/>
    public UIElement this[int index]
    {
        get => _items[index];
        set
        {
            var existing = _items[index];
            if (ReferenceEquals(existing, value))
                return;

            _owner.VerifyAccess();
            ValidateAdoptable(value); // BEFORE RemoveAt — a failing Insert must not have dropped the old item

            RemoveAt(index);
            Insert(index, value);
        }
    }

    /// <summary>Appends <paramref name="item"/>, adopting it visually and logically.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="item"/> already has a parent (including being in this collection already).</exception>
    public void Add(UIElement item) => Insert(_items.Count, item);

    /// <summary>Inserts <paramref name="item"/> at <paramref name="index"/> (paint order), adopting it visually and logically.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="item"/> already has a parent, or adopting it would create a cycle.</exception>
    public void Insert(int index, UIElement item)
    {
        _owner.VerifyAccess();
        ValidateAdoptable(item);

        _items.Insert(index, item);
        _owner.AdoptChild(item, index);
        _owner.InvalidateMeasure();
    }

    /// <summary>
    /// Validates that <paramref name="item"/> is adoptable by the owner <b>before any state
    /// mutates</b>: adoption touches the items list, the logical/visual child lists, and the
    /// inheritance wiring in sequence, and a mid-adoption throw (e.g. the inheritance-cycle guard)
    /// would leave them inconsistent and the collection permanently wedged. This pre-runs every
    /// guard the adoption path can trip.
    /// </summary>
    private void ValidateAdoptable(UIElement item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.VisualParent is not null || item.LogicalParent is not null)
        {
            throw new InvalidOperationException(
                ReferenceEquals(item.VisualParent ?? item.LogicalParent, _owner)
                    ? "The element is already in this collection; an element cannot be added twice."
                    : "The element is already attached to another parent; remove it there first.");
        }

        // Pre-run the cycle guards (AddVisualChild's visual-chain walk; SetInheritanceParent's
        // inheritance-chain walk) so adoption cannot throw after mutation has begun.
        for (var ancestor = _owner; ancestor is not null; ancestor = ancestor.VisualParent)
        {
            if (ReferenceEquals(ancestor, item))
                throw new InvalidOperationException("Adding this element would create a cycle in the visual tree.");
        }

        for (var node = (UIObject?)_owner; node is not null; node = node.GetInheritanceParent())
        {
            if (ReferenceEquals(node, item))
            {
                throw new InvalidOperationException(
                    "Adding this element would create a cycle: it is an inheritance ancestor of this collection's owner.");
            }
        }
    }

    /// <summary>Removes <paramref name="item"/>, detaching it visually and logically.</summary>
    /// <remarks>
    /// Removal detaches but does <b>not</b> run the permanent-discard sweep — call
    /// <see cref="UIElement.TearDown"/> on the removed subtree when it will not be reattached
    /// (it releases the value store's external hooks, and S2 binding subscriptions once the
    /// binding engine lands at P4). Detach + reattach without teardown is fully supported.
    /// </remarks>
    public bool Remove(UIElement item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _owner.VerifyAccess();

        var index = _items.IndexOf(item);
        if (index < 0)
            return false;

        RemoveAt(index);
        return true;
    }

    /// <inheritdoc/>
    /// <remarks><inheritdoc cref="Remove" path="/remarks"/></remarks>
    public void RemoveAt(int index)
    {
        _owner.VerifyAccess();

        var item = _items[index];
        _items.RemoveAt(index);
        _owner.DisownChild(item);
        _owner.InvalidateMeasure();
    }

    /// <summary>Moves the item at <paramref name="oldIndex"/> to <paramref name="newIndex"/>, preserving its attach state.</summary>
    public void Move(int oldIndex, int newIndex)
    {
        _owner.VerifyAccess();

        var item = _items[oldIndex];
        _items.RemoveAt(oldIndex);
        _items.Insert(Math.Clamp(newIndex, 0, _items.Count), item);
        _owner.MoveVisualChild(item, newIndex);
        _owner.InvalidateMeasure();
    }

    /// <inheritdoc/>
    /// <remarks><inheritdoc cref="Remove" path="/remarks"/></remarks>
    public void Clear()
    {
        _owner.VerifyAccess();

        // DisownChild detaches tree relationships only — it never touches _items, so this loop is
        // NOT mutating the list it iterates; the single _items.Clear() below empties it. (Backward
        // so detach walks run last-to-first, matching per-item RemoveAt order.)
        for (var i = _items.Count - 1; i >= 0; i--)
            _owner.DisownChild(_items[i]);

        _items.Clear();
        _owner.InvalidateMeasure();
    }

    /// <inheritdoc/>
    public bool Contains(UIElement item) => _items.Contains(item);

    /// <inheritdoc/>
    public int IndexOf(UIElement item) => _items.IndexOf(item);

    /// <inheritdoc/>
    public void CopyTo(UIElement[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    /// <summary>An allocation-free struct enumerator over the items.</summary>
    public List<UIElement>.Enumerator GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator<UIElement> IEnumerable<UIElement>.GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}
