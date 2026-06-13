using System.Collections;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The element's style classes (<c>.class</c> selector targets; design doc §3.2): an interned-string
/// small set whose mutations re-match the owner synchronously at the mutation site (SD12).
/// <c>':'</c>-prefixed names are rejected — interaction pseudo-classes are unreachable from app
/// code (SD11); they flow through <c>SetInteractionState</c> / <c>PseudoClasses</c> only.
/// </summary>
public sealed class ClassSet : IReadOnlyCollection<string>
{
    private readonly UIElement _owner;
    private readonly List<string> _entries = [];

    internal ClassSet(UIElement owner) => _owner = owner;

    /// <summary>The number of classes set.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Adds <paramref name="name"/>; returns whether the set changed (a duplicate add is a no-op
    /// and restyle-free — SD11). The entry is interned.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null/empty or starts with <c>':'</c>.</exception>
    public bool Add(string name)
    {
        name = ValidateAndIntern(name);

        if (_entries.Contains(name))
            return false;

        _entries.Add(name);
        _owner.OnStylingClassesChanged(name);
        return true;
    }

    /// <summary>Removes <paramref name="name"/>; returns whether the set changed.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null/empty or starts with <c>':'</c>.</exception>
    public bool Remove(string name)
    {
        name = ValidateAndIntern(name);

        if (!_entries.Remove(name))
            return false;

        _owner.OnStylingClassesChanged(name);
        return true;
    }

    /// <summary>Whether <paramref name="name"/> is set (ordinal).</summary>
    public bool Contains(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _entries.Contains(name);
    }

    /// <summary>
    /// Replaces the whole set in <b>one</b> restyle pass (doc §3.2 — bulk swap; a rule matched
    /// under both the old and the new set keeps its armed frame, so surviving values never
    /// retract-and-reapply). A no-change replace is restyle-free.
    /// </summary>
    /// <exception cref="ArgumentException">An entry is null/empty or starts with <c>':'</c>.</exception>
    public void Replace(ReadOnlySpan<string> names)
    {
        var replacement = new List<string>(names.Length);

        foreach (var name in names)
        {
            var interned = ValidateAndIntern(name);

            if (!replacement.Contains(interned))
                replacement.Add(interned);
        }

        if (SetEquals(replacement))
            return;

        _entries.Clear();
        _entries.AddRange(replacement);
        _owner.OnStylingClassesChanged(changedClass: null); // bulk swap — one restyle pass (doc §3.2)
    }

    /// <summary>Enumerates the class names in insertion order.</summary>
    public List<string>.Enumerator GetEnumerator() => _entries.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator<string> IEnumerable<string>.GetEnumerator() => _entries.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => _entries.GetEnumerator();

    private bool SetEquals(List<string> other)
    {
        if (_entries.Count != other.Count)
            return false;

        foreach (var entry in other)
        {
            if (!_entries.Contains(entry))
                return false;
        }

        return true;
    }

    private static string ValidateAndIntern(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (name[0] == ':')
        {
            throw new ArgumentException(
                $"Class name '{name}' is invalid: ':'-prefixed names are pseudo-classes and are unreachable " +
                "from Classes (SD11) — interaction state flows through SetInteractionState, control-semantic " +
                "pseudo-classes through PseudoClasses.", nameof(name));
        }

        return string.Intern(name);
    }
}