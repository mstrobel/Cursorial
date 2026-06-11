using System.Collections;

namespace Cursorial.UI.Controls;

/// <summary>
/// The owner-wired definition collection backing <see cref="Grid.ColumnDefinitions"/> /
/// <see cref="Grid.RowDefinitions"/> (spec §2.3): every mutation invalidates the owner's measure,
/// and adding sets the definition's owner backpointer — a definition belongs to at most one
/// collection, so re-adding one elsewhere (or twice) throws (matrix L159 ②).
/// </summary>
public abstract class GridDefinitionCollection<T> : IList<T>, IReadOnlyList<T>
    where T : GridDefinition
{
    private readonly Grid _owner;
    private readonly List<T> _items = [];

    private protected GridDefinitionCollection(Grid owner) => _owner = owner;

    /// <inheritdoc cref="IReadOnlyCollection{T}.Count" />
    public int Count => _items.Count;

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <inheritdoc cref="IList{T}.this"/>
    public T this[int index]
    {
        get => _items[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _owner.VerifyAccess();

            var existing = _items[index];
            if (ReferenceEquals(existing, value))
                return;

            ThrowIfOwned(value);
            existing.Owner = null;
            _items[index] = value;
            value.Owner = _owner;
            _owner.InvalidateMeasure();
        }
    }

    /// <summary>Appends <paramref name="item"/>, wiring its owner backpointer.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="item"/> already belongs to a collection.</exception>
    public void Add(T item) => Insert(_items.Count, item);

    /// <summary>Inserts <paramref name="item"/> at <paramref name="index"/>, wiring its owner backpointer.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="item"/> already belongs to a collection.</exception>
    public void Insert(int index, T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _owner.VerifyAccess();
        ThrowIfOwned(item);

        _items.Insert(index, item);
        item.Owner = _owner;
        _owner.InvalidateMeasure();
    }

    /// <summary>Removes <paramref name="item"/>, clearing its owner backpointer.</summary>
    public bool Remove(T item)
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
    public void RemoveAt(int index)
    {
        _owner.VerifyAccess();

        var item = _items[index];
        _items.RemoveAt(index);
        item.Owner = null;
        _owner.InvalidateMeasure();
    }

    /// <inheritdoc/>
    public void Clear()
    {
        _owner.VerifyAccess();

        for (var i = 0; i < _items.Count; i++)
            _items[i].Owner = null;

        _items.Clear();
        _owner.InvalidateMeasure();
    }

    /// <inheritdoc/>
    public bool Contains(T item) => _items.Contains(item);

    /// <inheritdoc/>
    public int IndexOf(T item) => _items.IndexOf(item);

    /// <inheritdoc/>
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    /// <summary>An allocation-free struct enumerator over the definitions.</summary>
    public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    private static void ThrowIfOwned(T item)
    {
        if (item.Owner is not null)
        {
            throw new InvalidOperationException(
                "The definition already belongs to a definition collection; a definition belongs to " +
                "at most one collection (remove it there first).");
        }
    }
}

/// <summary>The <see cref="Grid.ColumnDefinitions"/> collection (owner-wired; see <see cref="GridDefinitionCollection{T}"/>).</summary>
public sealed class ColumnDefinitionCollection : GridDefinitionCollection<ColumnDefinition>
{
    internal ColumnDefinitionCollection(Grid owner) : base(owner)
    {
    }
}

/// <summary>The <see cref="Grid.RowDefinitions"/> collection (owner-wired; see <see cref="GridDefinitionCollection{T}"/>).</summary>
public sealed class RowDefinitionCollection : GridDefinitionCollection<RowDefinition>
{
    internal RowDefinitionCollection(Grid owner) : base(owner)
    {
    }
}
