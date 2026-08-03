using Cursorial.Output;

namespace Cursorial.Rendering.Fonts;

/// <summary>
/// Supplies the <see cref="Style"/> for the glyph cell at (<paramref name="column"/>, <paramref name="row"/>).
/// Lets a font be painted with a position-dependent color source (e.g. a gradient flowing across a FIGlet
/// headline) while the font itself stays unaware of brushes — the provider takes only cell coordinates and a
/// <see cref="Style"/>, never a higher-layer brush type.
/// </summary>
public delegate Style GlyphStyleProvider(int column, int row);

/// <summary>
/// A font that renders text into cells of a <see cref="CellBuffer"/>. Implementations cover
/// plain monospace (the identity — one grapheme per cell), FIGlet-style ASCII glyph fonts that
/// expand each character into a multi-cell pattern, and bitmap-derived fonts such as Braille
/// subdivision.
/// </summary>
/// <remarks>
/// <para>
/// Fonts operate exclusively through the cell-grid model — they call <see cref="CellBuffer.Set"/>
/// (or write through the indexer) and otherwise know nothing about escape sequences, terminal
/// protocols, or capabilities. That keeps the abstraction simple and lets the rendering layer
/// reuse wide-cell handling, the blending stack, diff rendering, and capability-aware
/// quantization for free.
/// </para>
/// <para>
/// Out-of-band content (sized text via OSC 66, images via Kitty graphics, etc.) does not flow
/// through fonts — it attaches to the buffer as an <see cref="Fragments.IBufferFragment"/> and
/// the renderer emits its protocol bytes after the cell pass. Higher-level <c>IContent</c>
/// abstractions tie the two together with capability-aware fallback.
/// </para>
/// </remarks>
public interface IGlyphFont
{
    /// <summary>
    /// The display name to use when describing this font to the user.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Ensures the specified <paramref name="style"/> is compatible with this font. May adjust
    /// properties such as colors or attributes in the style to adhere to font-specific constraints
    /// or limitations.
    /// </summary>
    /// <param name="style">The style to validate and potentially adjust.</param>
    /// <returns>
    /// A modified <see cref="Style"/> instance that is compatible with the current font.
    /// </returns>
    Style EnsureCompatibleStyle(in Style style);

    /// <summary>
    /// Measure the cell footprint of <paramref name="text"/> when painted with this font, without
    /// touching a buffer. Used by layout code to allocate space before painting.
    /// </summary>
    Size Measure(ReadOnlySpan<char> text);

    /// <summary>
    /// Paint <paramref name="text"/> into <paramref name="buffer"/> with its top-left anchored at
    /// (<paramref name="row"/>, <paramref name="column"/>). Returns the actual painted footprint —
    /// usually equal to <see cref="Measure"/>, but implementations may clip when the anchor leaves
    /// insufficient room.
    /// </summary>
    /// <remarks>
    /// Fonts must respect the buffer's active blending mode by routing all cell writes through
    /// <see cref="CellBuffer.Set"/> rather than the raw indexer. Coordinates beyond the buffer's
    /// extent are silently clipped — implementations should not throw on out-of-range targets,
    /// they should paint what fits.
    /// </remarks>
    Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in Style style);

    /// <summary>
    /// Paint <paramref name="text"/> sampling <paramref name="styleProvider"/> per cell, so a caller can color
    /// the glyphs with a position-dependent source (a gradient across a headline) without the font knowing
    /// about brushes. The default samples the provider once at the anchor and paints a single style; fonts that
    /// render cell-by-cell (e.g. <see cref="FigletFont"/>) override this to sample each painted cell.
    /// </summary>
    Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, GlyphStyleProvider styleProvider)
        => Paint(buffer, column, row, text, styleProvider(column, row));

    /// <summary>
    /// The advance metrics text layout consults to wrap and trim text rendered with this font
    /// (see <see cref="GlyphMetrics"/>). The default adapts <see cref="Measure"/> per cluster —
    /// correct for any font, conservative where glyphs kern; fonts with cheaper or exact
    /// per-cluster knowledge override.
    /// </summary>
    GlyphMetrics GetMetrics() => new MeasuredGlyphMetrics(this);
}
