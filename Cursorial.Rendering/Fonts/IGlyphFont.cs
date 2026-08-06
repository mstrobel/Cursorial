using Cursorial.Output;

namespace Cursorial.Rendering.Fonts;

/// <summary>
/// Supplies the <see cref="CellStyle"/> for the glyph cell at (<paramref name="column"/>, <paramref name="row"/>).
/// Lets a font be painted with a position-dependent color source (e.g. a gradient flowing across a FIGlet
/// headline) while the font itself stays unaware of brushes — the provider takes only cell coordinates and a
/// <see cref="CellStyle"/>, never a higher-layer brush type.
/// </summary>
public delegate CellStyle GlyphStyleProvider(int column, int row);

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
    /// A modified <see cref="CellStyle"/> instance that is compatible with the current font.
    /// </returns>
    CellStyle EnsureCompatibleStyle(in CellStyle style);

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
    Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in CellStyle style);

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

    /// <summary>
    /// Whether this face can draw <paramref name="codepoint"/> as visible ink. Callers that get
    /// to CHOOSE which characters to emit — text layout picking a trim indicator, for instance —
    /// ask first, because a face with a limited repertoire draws nothing at all for a codepoint
    /// it lacks (<see cref="FigletFont"/>'s "missing glyph = blank gap" rule), and a trim
    /// indicator that renders as empty cells makes truncated text indistinguishable from
    /// complete text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default answers <see langword="true"/>: it is correct for pass-through faces such as
    /// <see cref="MonospaceFont"/>, which hand the grapheme to the terminal and let the
    /// terminal's own font decide, and it keeps the member source-compatible for external
    /// implementors. Faces backed by a fixed glyph table override it.
    /// </para>
    /// <para>
    /// <b>Decorators must forward.</b> A wrapper (<see cref="ShadowedFont"/>,
    /// <see cref="DecoratedFont"/>) has exactly its inner face's repertoire — inheriting the
    /// optimistic default would have it claim every codepoint while wrapping a face that draws
    /// almost none of them.
    /// </para>
    /// </remarks>
    bool HasGlyph(uint codepoint) => true;

    /// <summary>
    /// Rows from the top of this face's line box down to and <b>including</b> its baseline row —
    /// a <b>COUNT, never a 0-based row index</b>. A face 6 rows tall whose glyph bodies rest on
    /// the 5th row reports <c>6</c> for its height and <c>5</c> here; the baseline's 0-based row
    /// index is <c>Baseline - 1</c> (= 4). Counting, not indexing, is what the FIGfont spec's
    /// header field means ("the number of lines of sub-characters from the baseline of a
    /// FIGcharacter to the top of the tallest FIGcharacter"), and it keeps the invariant
    /// <c>Ascender + Descender == line-box height</c> exact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Text layout stacks runs of DIFFERENT metrics inside one line band
    /// (a one-cell trim indicator painted by the terminal's own font beside a multi-row FIGlet
    /// headline, say). Aligning such runs to the band's bottom row drops the short run into the
    /// tall face's DESCENDER row — under the glyph bodies, not beside them. See
    /// <see cref="Text.VerticalTextAlignment.Baseline"/>.
    /// </para>
    /// <para>
    /// The default describes a single-row face: height 1, baseline 1 (the one row IS the baseline
    /// row), descent 0. That is exactly right for the identity <see cref="MonospaceFont"/> and for
    /// every cell-per-cluster face, and it keeps the member source-compatible for external
    /// implementors — just like <see cref="HasGlyph"/>.
    /// </para>
    /// </remarks>
    int Baseline => 1;

    /// <summary>
    /// Rows the face rises above the baseline, <b>counting the baseline row itself</b> — the same
    /// number as <see cref="Baseline"/>, by identity, and derived from it rather than stored
    /// separately.
    /// </summary>
    /// <remarks>
    /// A FIGlet header supplies only two numbers (height and baseline), so a face has only two
    /// independent vertical facts; storing a third that could disagree would be a bug waiting to
    /// happen. The two NAMES exist because callers ask two different questions — "which row is the
    /// baseline?" (<see cref="Baseline"/>) and "how much room does this face need above the
    /// baseline?" (<see cref="Ascender"/>) — and the answer to both is the same count only because
    /// the baseline row is itself an ink row in a cell grid. Never override this independently;
    /// override <see cref="Baseline"/> and let this follow.
    /// </remarks>
    int Ascender => Baseline;

    /// <summary>
    /// Rows below the baseline row — a COUNT (0 for a face with no descenders), satisfying
    /// <c>Ascender + Descender == the face's line-box height</c>. Derived, like
    /// <see cref="Ascender"/>: a face computes it as <c>height - Baseline</c> rather than storing
    /// it.
    /// </summary>
    int Descender => 0;
}
