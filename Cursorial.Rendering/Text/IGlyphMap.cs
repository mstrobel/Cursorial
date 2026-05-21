namespace Cursorial.Rendering.Text;

/// <summary>
/// A per-grapheme substitution function applied during rendering of a <see cref="TextRun"/>.
/// Preserves cell-stream semantics: input is a single grapheme cluster; output is one or more
/// grapheme clusters that the formatter measures and paints exactly as ordinary text. Suitable
/// for stylistic transforms like fullwidth Latin, mathematical double-struck, small-caps, and
/// custom icon-font lookups.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure function contract.</b> Implementations must be deterministic and side-effect-free.
/// The formatter caches measurements derived from a map's output and may call <see cref="Map"/>
/// repeatedly during a single format pass.
/// </para>
/// <para>
/// <b>Not for multi-cell glyph fonts.</b> When the output footprint exceeds the cell-stream
/// model — e.g., FIGlet faces that expand each character into an NxM block — use
/// <see cref="FigletBlock"/> at the block level instead. The grapheme-map contract intentionally
/// keeps the formatter's wrap and measure logic identical to the identity case; multi-cell
/// expansion would force the wrap loop to reason about variable line heights.
/// </para>
/// <para>
/// <b>Unknown input.</b> A map asked for a grapheme it doesn't have should return the input
/// unchanged. Composable: callers can stack maps without one map's gaps silently turning into
/// the empty string.
/// </para>
/// </remarks>
public interface IGlyphMap
{
    /// <summary>
    /// Map a single grapheme cluster to its rendered form. The result may be the original
    /// input (identity / unknown), a single replacement grapheme (the typical case), or a short
    /// sequence of graphemes. Returning <see cref="string.Empty"/> elides the grapheme entirely.
    /// </summary>
    string Map(ReadOnlySpan<char> grapheme);
}
