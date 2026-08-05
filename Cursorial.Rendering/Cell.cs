using Cursorial.Output;

namespace Cursorial.Rendering;

/// <summary>
/// A single grid cell of the rendering buffer: the grapheme cluster occupying the cell, the
/// kind of cell (single-width, wide left half, wide right-half continuation), and the visual
/// <see cref="Style"/> applied to it.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Style.Default">default</see>-constructed cell is a single-width blank with default
/// styling — the natural "empty" of a freshly allocated buffer. To explicitly represent the right
/// half of a wide glyph, use <see cref="WideContinuation"/>; a single grapheme that occupies two
/// cells is stored as a <see cref="CellKind.WideLeft"/> cell at the left position and a
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
/// <param name="Kind">Single-width, wide left-half, or wide right-half continuation.</param>
/// <param name="Style">Foreground / background / attribute / underline state applied to the cell.</param>
public readonly record struct Cell(string? Grapheme, CellKind Kind, Style Style)
{
    /// <summary>A single-width blank cell with default styling — the <c>default(Cell)</c> value.</summary>
    public static Cell Blank => default;

    /// <summary>
    /// A single-width durable (non-occludable) whitespace cell with default styling and a
    /// grapheme equal to <see cref="CellBuffer.DurableEmptyGrapheme"/>.
    /// </summary>
    public static Cell DurableEmpty => Blank with { Grapheme = CellBuffer.DurableEmptyGrapheme };

    /// <summary>
    /// The right-half placeholder of a wide-left glyph. The renderer skips this cell during
    /// emission, but the cell's <see cref="Style"/> background is still painted: when the
    /// terminal renders the wide glyph at the <see cref="CellKind.WideLeft"/> position, it
    /// paints both cell columns with the wide-left's SGR background as a single operation.
    /// The renderer doesn't (and can't usefully) emit anything separate for the right half
    /// — moving the cursor to the right-half column and writing there is undefined behavior
    /// for most terminals.
    /// </summary>
    /// <remarks>
    /// <b>No style.</b> A continuation stored by <see cref="CellBuffer"/> carries <see cref="Kind"/>
    /// and nothing else — terminals don't render different backgrounds on the two halves of a wide
    /// glyph, so a style here could only ever duplicate the wide-left's, and keeping that duplicate
    /// in sync is what every wide-glyph regression in this buffer has failed at. Code that needs the
    /// colors painting a continuation's column reads them off the wide-left one column left; the
    /// buffer does the same when pair hygiene has to blank an orphaned continuation. Writing a styled
    /// continuation through the buffer's indexer is still allowed (a cell-by-cell region copy hands
    /// back what it read), but the style is not read back by anything that paints.
    /// </remarks>
    public static Cell WideContinuation { get; } = new(null, CellKind.WideContinuation, default);

    /// <summary>Number of terminal cells this entry occupies: 1 for single, 2 for wide-left, 0 for continuation.</summary>
    public int Width => Kind switch
                        {
                            CellKind.WideLeft         => 2,
                            CellKind.WideContinuation => 0,
                            _                         => 1
                        };

    /// <summary>True when the cell is a blank single-width cell with default styling.</summary>
    public bool IsBlank => Kind == CellKind.Single &&
                           Style.IsDefault &&
                           string.IsNullOrEmpty(Grapheme);
}

/// <summary>Classification of a <see cref="Cell"/>'s role in the grid.</summary>
public enum CellKind : byte
{
    /// <summary>A single-cell-width grapheme. Default for <c>default(Cell)</c>.</summary>
    Single = 0,

    /// <summary>The left half of a two-cell-wide grapheme (CJK, emoji, fullwidth forms).</summary>
    WideLeft = 1,

    /// <summary>The right-half placeholder of a wide-left grapheme. Renderer skips this cell.</summary>
    WideContinuation = 2
}