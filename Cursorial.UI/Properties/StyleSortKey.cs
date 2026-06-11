namespace Cursorial.UI;

/// <summary>
/// The packed within-slot ordering key for the single <see cref="BindingPriority.Style"/> slot. The
/// store treats it as an <em>opaque comparable</em>: frames sort by it and larger keys are
/// <b>stronger</b> (win arbitration); among equal keys the later-added frame wins. The styling
/// engine (Fork B) owns construction and the packing layout
/// (<c>[layer:3][names:8][classLike:10][types:8][scopeDepth:8][order:27]</c>, design doc §3.4 —
/// layer beats specificity); the property engine never inspects the fields.
/// </summary>
/// <param name="Packed">The packed key bits; ordering is unsigned numeric.</param>
public readonly record struct StyleSortKey(ulong Packed) : IComparable<StyleSortKey>
{
    /// <inheritdoc/>
    public int CompareTo(StyleSortKey other) => Packed.CompareTo(other.Packed);

    /// <summary>Whether <paramref name="left"/> sorts weaker than <paramref name="right"/>.</summary>
    public static bool operator <(StyleSortKey left, StyleSortKey right) => left.Packed < right.Packed;

    /// <summary>Whether <paramref name="left"/> sorts stronger than <paramref name="right"/>.</summary>
    public static bool operator >(StyleSortKey left, StyleSortKey right) => left.Packed > right.Packed;

    /// <summary>Whether <paramref name="left"/> sorts weaker than or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(StyleSortKey left, StyleSortKey right) => left.Packed <= right.Packed;

    /// <summary>Whether <paramref name="left"/> sorts stronger than or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(StyleSortKey left, StyleSortKey right) => left.Packed >= right.Packed;

    /// <inheritdoc/>
    public override string ToString() => $"0x{Packed:X}";
}
