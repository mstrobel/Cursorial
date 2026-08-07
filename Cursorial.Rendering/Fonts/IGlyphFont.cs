using Cursorial.Output;
using Cursorial.Rendering.Media;

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
    /// <para>
    /// <b>The background's PRESENCE picks the mode</b> — there is no flag and no second overload,
    /// because there is nothing a flag could say that the delta does not already:
    /// <list type="table">
    ///   <item><term>background absent</term><description><b>STAMP.</b> Only the cells the face inks
    ///   are written; the gaps keep whatever was underneath, grapheme and style alike. That is what
    ///   lets a <see cref="FigletFont"/> headline sit over existing content and show it through the
    ///   holes in its glyphs.</description></item>
    ///   <item><term>background present</term><description><b>BOX.</b> The glyph's whole
    ///   <see cref="Measure"/> box is filled with that background first, then the ink goes down on
    ///   top — so the caller gets an opaque block without pre-filling it, and gets one for
    ///   <see cref="Cursorial.Media.Color.Default"/> too.</description></item>
    /// </list>
    /// The pair is exactly what a <see cref="CellStyle"/> could not spell: its only word for "no
    /// opinion about the background" is <see cref="Cursorial.Media.Color.Default"/>, which is also its word for "the
    /// terminal's own background", so whichever way a face chose to read that sentinel, one of the
    /// two modes was unreachable.
    /// </para>
    /// <para>
    /// <b>Why a delta here at all — and why that is not an exception to the ownership rule.</b> The
    /// rule this codebase follows is that an operation which OWNS the cells it writes takes a whole
    /// <see cref="CellStyle"/> (nothing is underneath for an absent channel to mean), while one that
    /// MODIFIES cells somebody else wrote takes a delta — <c>DrawingContext.TintCells</c>, the
    /// selection highlight. A paint always owns its INK cells: it writes them outright, and the
    /// channels the delta declines to state fall through to what those cells already held, as any
    /// delta's do. What the delta buys is a fact about the OTHER cells — whether this paint also owns
    /// the GAPS between the strokes. A whole style has nowhere to put that answer; the presence of
    /// one channel is precisely the room it needs, which is why the delta is the right shape here.
    /// </para>
    /// <para>
    /// Fonts must respect the buffer's active blending mode by routing all cell writes through
    /// <see cref="CellBuffer.Set"/> rather than the raw indexer. Coordinates beyond the buffer's
    /// extent are silently clipped — implementations should not throw on out-of-range targets,
    /// they should paint what fits.
    /// </para>
    /// </remarks>
    Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in PartialStyle style);

    /// <summary>
    /// Paint <paramref name="text"/> with <paramref name="baseStyle"/>, adjusted per painted cell by
    /// <paramref name="delta"/> resolved against <paramref name="bounds"/> — so a caller can color the glyphs
    /// with a position-dependent source (a gradient across a headline) without pre-resolving it. The default
    /// resolves once at the anchor and paints a single folded style; fonts that render cell-by-cell (e.g.
    /// <see cref="FigletFont"/>) override this to resolve each painted cell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a template and not a callback.</b> This overload used to take a <c>GlyphStyleProvider</c>
    /// delegate, which existed only because <see cref="IBrush"/> lived in the drawing layer and
    /// <c>Cursorial.Rendering</c> could not name it — so the caller wrapped brush sampling in a closure and
    /// the font sampled blind. <see cref="IBrush"/> is in Rendering now, and
    /// <see cref="StyleDeltaTemplate.Resolve(int,int,in Rect,in Rect)"/> IS that callback's signature plus
    /// the content and fill bounds it was closing over. Passing the value form also makes
    /// <see cref="StyleDeltaTemplate.IsUniform"/> readable, which a callback could never expose: the common
    /// case (a solid color, or no color at all) resolves once instead of once per cell.
    /// </para>
    /// <para>
    /// <b>The base style is REQUIRED, not defaulted.</b> A <see cref="StyleDeltaTemplate"/> is a delta, so
    /// without a base there is nothing for the channels it declines to state to fall through to — the font
    /// would silently paint them from <c>default</c>, which is the loss this signature exists to prevent.
    /// </para>
    /// <para>
    /// <b>The bounds are REQUIRED too, and are not the painted footprint.</b> They are the brush's coordinate
    /// space, which belongs to the SCOPE the brush was declared at — a paragraph, a block, the whole document,
    /// or an inline run's wrap-invariant reading-order strip — and is routinely larger than, or differently
    /// shaped from, the cells this call paints. Defaulting them to the footprint would restart every gradient
    /// at each run boundary, silently demoting a document-scoped brush to a run-scoped one. A caller that
    /// genuinely wants the footprint says so with <c>new Rect(column, row, Measure(text))</c>.
    /// </para>
    /// <para>
    /// Implementations fold with <see cref="PartialStyle.ApplyTo"/> and must resolve PER SAMPLE unless
    /// <see cref="StyleDeltaTemplate.IsUniform"/> says the answer cannot vary.
    /// </para>
    /// <para>
    /// The default folds to a whole <see cref="CellStyle"/> and hands it to the flat overload, which
    /// takes a delta — so this is where the two vocabularies meet, and the meeting costs one bit. A
    /// whole style cannot say "no opinion about the background", so the adapter reads the only signal
    /// it has and says so out loud: a <see cref="Cursorial.Media.Color.Default"/> background stamps, anything else
    /// boxes. A caller that needs the distinction states it, by calling the flat overload with a
    /// delta of its own.
    /// </para>
    /// </remarks>
    Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in CellStyle baseStyle,
               in StyleDeltaTemplate delta, in Rect bounds)
    {
        var folded = delta.Resolve(column, row, bounds).ApplyTo(baseStyle);

        return Paint(buffer, column, row, text,
                     folded.Background.IsDefault ? PartialStyle.FromInk(folded) : PartialStyle.From(folded));
    }

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
