using Cursorial.Core.Output;

namespace Cursorial.Rendering;

/// <summary>
/// A single grid cell of the rendering buffer: the grapheme cluster occupying the cell, the
/// kind of cell (single-width, wide left half, wide right-half continuation), and the visual
/// <see cref="Style"/> applied to it.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="default"/>-constructed cell is a single-width blank with default styling —
/// the natural "empty" of a freshly-allocated buffer. To explicitly represent the right half of
/// a wide glyph, use <see cref="WideContinuation"/>; a single grapheme that occupies two cells
/// is stored as a <see cref="CellKind.WideLeft"/> cell at the left position and a
/// <see cref="CellKind.WideContinuation"/> at the position immediately right.
/// </para>
/// <para>
/// <b>Grapheme storage.</b> The <see cref="Grapheme"/> is the user-visible glyph — for most
/// cells a single character, for emoji / CJK / accented letters one cluster of UTF-16 code
/// units. <see langword="null"/> or empty represents a blank cell. The renderer always emits
/// <em>something</em> for non-skip cells: an empty grapheme prints as a space so the
/// background color paints. A wide-continuation cell carries no glyph of its own; the renderer
/// skips it during emission because the terminal already advanced the cursor past it when the
/// preceding wide-left cell was emitted.
/// </para>
/// </remarks>
/// <param name="Grapheme">
/// The grapheme cluster occupying the cell, as UTF-16. <see langword="null"/> or empty means a
/// blank cell (rendered as a space).
/// </param>
/// <param name="Kind">Single-width, wide left half, or wide right-half continuation.</param>
/// <param name="Style">Foreground / background / attribute / underline state applied to the cell.</param>
public readonly record struct Cell(string? Grapheme, CellKind Kind, Style Style)
{
    /// <summary>A single-width blank cell with default styling — the <c>default(Cell)</c> value.</summary>
    public static Cell Blank => default;

    /// <summary>
    /// The right-half placeholder of a wide-left glyph. The renderer skips this cell during
    /// emission; the terminal's cursor advance from the wide-left glyph covers its position.
    /// Mutating the buffer cell at this position should be paired with mutating the
    /// preceding wide-left so the buffer remains internally consistent — <see cref="CellBuffer.Set"/>
    /// handles that automatically.
    /// </summary>
    public static Cell WideContinuation { get; } = new(null, CellKind.WideContinuation, default);

    /// <summary>Number of terminal cells this entry occupies: 1 for single, 2 for wide-left, 0 for continuation.</summary>
    public int Width => Kind switch
    {
        CellKind.WideLeft => 2,
        CellKind.WideContinuation => 0,
        _ => 1,
    };

    /// <summary>True when the cell is a blank single-width cell with default styling.</summary>
    public bool IsBlank =>
        Kind == CellKind.Single
        && Style.IsDefault
        && (Grapheme is null || Grapheme.Length == 0);
}

/// <summary>Classification of a <see cref="Cell"/>'s role in the grid.</summary>
public enum CellKind : byte
{
    /// <summary>A single-cell-width grapheme. Default for <c>default(Cell)</c>.</summary>
    Single = 0,

    /// <summary>The left half of a two-cell-wide grapheme (CJK, emoji, fullwidth forms).</summary>
    WideLeft = 1,

    /// <summary>The right-half placeholder of a wide-left grapheme. Renderer skips this cell.</summary>
    WideContinuation = 2,
}
