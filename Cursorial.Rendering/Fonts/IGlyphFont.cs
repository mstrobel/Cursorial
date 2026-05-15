using Cursorial.Output;

namespace Cursorial.Rendering.Fonts;

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
    Size Paint(CellBuffer buffer, int row, int column, ReadOnlySpan<char> text, in Style style);
}
