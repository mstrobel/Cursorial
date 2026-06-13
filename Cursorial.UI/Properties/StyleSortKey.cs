// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The packed within-slot ordering key for the single <see cref="BindingPriority.Style"/> slot. The
/// store treats it as an <em>opaque comparable</em>: frames sort by it and larger keys are
/// <b>stronger</b> (win arbitration); among equal keys the later-added frame wins. The styling
/// engine (Fork B) owns construction via <see cref="Create"/> and the packing layout
/// (<c>[layer:3][names:8][classLike:10][types:8][scopeDepth:8][order:27]</c>, design doc §3.4 —
/// layer beats specificity); the property engine never inspects the fields.
/// </summary>
/// <param name="Packed">The packed key bits; ordering is unsigned numeric.</param>
public readonly record struct StyleSortKey(ulong Packed) : IComparable<StyleSortKey>
{
    // Bit layout (style matrix SD5, bit-exact — most-significant first):
    //   [63..61] layer  [60..53] names  [52..43] classLike  [42..35] types
    //   [34..27] scopeDepth  [26..0] order
    private const int LayerShift = 61;
    private const int NamesShift = 53;
    private const int ClassLikeShift = 43;
    private const int TypesShift = 35;
    private const int ScopeDepthShift = 27;

    private const ulong NamesMax = 0xFF;
    private const ulong ClassLikeMax = 0x3FF;
    private const ulong TypesMax = 0xFF;
    private const ulong ScopeDepthMax = 0xFF;
    private const ulong OrderMax = (1UL << 27) - 1;

    /// <summary>
    /// Packs a key from its fields (style matrix SD5). Each count <b>saturates</b> at its field
    /// maximum (255 / 1023 / 255 / 255 / 2²⁷−1) — an overflowing count never carries into the
    /// neighboring field. Specificity counts are taken over the full flattened selector;
    /// <paramref name="order"/> is the rule's declaration index within its scope (DFS);
    /// <paramref name="scopeDepth"/> is non-zero only for <see cref="StyleLayer.Scoped"/> rules
    /// (SD6 — deeper scopes win).
    /// </summary>
    internal static StyleSortKey Create(StyleLayer layer, int names, int classLike, int types, int scopeDepth, int order)
        => new(
            ((ulong)layer & 0x7) << LayerShift
            | Saturate(names, NamesMax) << NamesShift
            | Saturate(classLike, ClassLikeMax) << ClassLikeShift
            | Saturate(types, TypesMax) << TypesShift
            | Saturate(scopeDepth, ScopeDepthMax) << ScopeDepthShift
            | Saturate(order, OrderMax));

    /// <summary>The channel layer (the top three bits — layer beats specificity, doc §3.4).</summary>
    internal StyleLayer Layer => (StyleLayer)(Packed >> LayerShift);

    /// <summary>The <c>#name</c> specificity count (saturated at 255).</summary>
    internal int Names => (int)((Packed >> NamesShift) & NamesMax);

    /// <summary>The class-like specificity count — classes + pseudo-classes (+ <c>When</c> conditions at P4); saturated at 1023.</summary>
    internal int ClassLike => (int)((Packed >> ClassLikeShift) & ClassLikeMax);

    /// <summary>The type specificity count (bare types and <c>:is()</c> alike; saturated at 255).</summary>
    internal int Types => (int)((Packed >> TypesShift) & TypesMax);

    /// <summary>The scope owner's styling-parent depth for Scoped rules; 0 for every other layer (SD6).</summary>
    internal int ScopeDepth => (int)((Packed >> ScopeDepthShift) & ScopeDepthMax);

    /// <summary>The rule's declaration index within its scope (DFS over the attached <c>Styles</c>; saturated at 2²⁷−1).</summary>
    internal int Order => (int)(Packed & OrderMax);

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

    private static ulong Saturate(int value, ulong max)
        => value <= 0 ? 0UL : Math.Min((ulong)value, max);
}
