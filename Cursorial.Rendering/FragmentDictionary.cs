using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Cursorial.Rendering;

/// <summary>
/// A lightweight, read-only shim over the fragments managed by a <see cref="CellBuffer"/>.
/// </summary>
/// <remarks>
/// If this dictionary was accessed through a <see cref="CellBufferView"/>, any coordinates shall
/// be view-local. The translation to and from parent buffer coordinates is handled automatically.
/// </remarks>
public readonly ref struct FragmentDictionary : IReadOnlyDictionary<(int Column, int Row), CellBuffer.FragmentEntry>, IEquatable<FragmentDictionary>
{
    // Shared backing store for the "no underlying buffer" case — keeps Empty allocation-free
    // (and equal-by-reference across uses) while letting the rest of the dictionary forward to a
    // non-null dictionary uniformly.
    private static readonly Dictionary<(int Column, int Row), CellBuffer.FragmentEntry> s_emptyFragments = new();

    private readonly Dictionary<(int Column, int Row), CellBuffer.FragmentEntry> _fragments;
    private readonly Rect _viewport;

    internal FragmentDictionary(Dictionary<(int Column, int Row), CellBuffer.FragmentEntry> fragments,
                                scoped in Rect viewport)
    {
        ArgumentNullException.ThrowIfNull(fragments);

        _fragments = fragments;
        _viewport = viewport;
    }

    /// <summary>An empty fragment dictionary. Returned by view operations that don't have a backing buffer (e.g., <c>default(CellBufferView).Fragments</c>).</summary>
    public static FragmentDictionary Empty => new(s_emptyFragments, default);

    public IEnumerator<KeyValuePair<(int Column, int Row), CellBuffer.FragmentEntry>> GetEnumerator()
        => _fragments.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    public int Count => _fragments.Count;

    public bool ContainsKey((int Column, int Row) key)
        => _fragments.ContainsKey(TranslateToBuffer(key, _viewport));

    public bool TryGetValue((int Column, int Row) key, out CellBuffer.FragmentEntry value)
        => _fragments.TryGetValue(TranslateToBuffer(key, _viewport), out value);

    public CellBuffer.FragmentEntry this[(int Column, int Row) key]
        => _fragments[TranslateToBuffer(key, _viewport)];

    public IEnumerable<(int Column, int Row)> Keys
    {
        get
        {
            var viewport = _viewport;
            return _fragments.Keys.Select(key => TranslateFromBuffer(key, viewport));
        }
    }

    public IEnumerable<CellBuffer.FragmentEntry> Values => _fragments.Values;
    
    private static (int Column, int Row) TranslateToBuffer(in (int Column, int Row) position, in Rect viewport) => new(viewport.Column + position.Column, viewport.Row + position.Row);
    private static (int Column, int Row) TranslateFromBuffer(in (int Column, int Row) position, in Rect viewport) => new(position.Column - viewport.Column, position.Row - viewport.Row);
    
    public bool Equals(FragmentDictionary other)
        => _fragments.Equals(other._fragments) && _viewport.Equals(other._viewport);

    public override int GetHashCode()
        => HashCode.Combine(_fragments, _viewport);

    public static bool operator ==(FragmentDictionary left, FragmentDictionary right) => left.Equals(right);

    public static bool operator !=(FragmentDictionary left, FragmentDictionary right) => !left.Equals(right);
    
    public override string ToString() => $"FragmentDictionary(Count={Count})";

    public override bool Equals([NotNullWhen(true)] object? obj) => false; // ref struct cannot be boxed
}