namespace Cursorial.UI;

/// <summary>
/// One property contribution inside a <see cref="ValueFrame"/> (a style/template setter, or a
/// frame-hosted binding entry). <see cref="HasValue"/> <see langword="false"/> means the entry is
/// currently <em>unset</em> — the store skips it and promotes the next source (ledger A8: entries
/// are valueless until their first value and an <see cref="UIProperty.UnsetValue"/> push returns
/// them to this state; never a null/default clobber).
/// </summary>
public interface IValueEntry
{
    /// <summary>The property this entry contributes to.</summary>
    UIProperty Property { get; }

    /// <summary>Whether the entry currently carries a value; <see langword="false"/> ⇒ skip to the next source.</summary>
    bool HasValue { get; }
}

/// <summary>
/// The typed read surface of a frame entry. Every value-bearing entry for a
/// <c>StyledProperty&lt;T&gt;</c> must implement <see cref="IValueEntry{T}"/> with the property's
/// <typeparamref name="T"/> — resolution reads through this interface with no boxing.
/// </summary>
public interface IValueEntry<T> : IValueEntry
{
    /// <summary>The entry's current value (raw — coercion runs at effective-value computation). Valid only while <see cref="IValueEntry.HasValue"/>.</summary>
    T GetValue();
}
