using System.Buffers;
using Cursorial.Output.Capabilities;

namespace Cursorial.Rendering.Fragments;

/// <summary>
/// Out-of-band content registered against a <see cref="CellBuffer"/> by anchor cell, emitted by
/// the <see cref="FrameRenderer"/> after the regular cell-grid pass as a single protocol payload.
/// </summary>
/// <remarks>
/// <para>
/// Fragments are the second of the two pluggable-content layers — they bypass the cell grid's
/// glyph model and emit terminal-specific escape sequences (OSC 66 sized text, Kitty graphics
/// protocol, iTerm2 inline images, Sixel). Anything that resolves to plain cells should be an
/// <see cref="Fonts.IGlyphFont"/> instead.
/// </para>
/// <para>
/// <b>Anchor and layering.</b> A fragment is registered at one anchor cell via
/// <see cref="CellBuffer.AddFragment"/>. The buffer doesn't touch the cell grid — fragments
/// are a pure overlay registration. The renderer paints the cell grid first, then emits each
/// fragment's protocol payload on top. Anything the caller painted under the fragment's
/// footprint remains visible through any region the fragment's payload doesn't cover; this
/// matters for fragments that don't fill every cell (transparent backgrounds, partial
/// coverage) and for the unsupported-fragment case where no payload is emitted at all.
/// </para>
/// <para>
/// <b>Capability gating.</b> The renderer calls <see cref="IsSupported"/> for every fragment on
/// every frame and skips emission when the answer is false. Implementations are expected to be
/// stateless and cheap to instantiate. When a fragment is unsupported the cells under it still
/// render normally — callers wanting a richer fallback than "what's under the fragment" should
/// build a higher-level <c>IContent</c> that chooses among multiple fragments at paint time.
/// </para>
/// <para>
/// <b>Cursor and SGR.</b> The renderer brackets every fragment emission with
/// <see cref="VtOutputSequences.SaveCursor"/> / <see cref="VtOutputSequences.RestoreCursor"/>
/// (<c>ESC 7</c> / <c>ESC 8</c>) and re-positions the cursor to the anchor cell before calling
/// <see cref="Emit"/>. Fragments may emit any SGR they need inside the brackets without
/// disturbing the renderer's tracked SGR state for the next frame.
/// </para>
/// </remarks>
public interface IBufferFragment
{
    /// <summary>
    /// The cell footprint the fragment occupies, anchored at its registration cell. Used by
    /// <see cref="CellBuffer.AddFragment"/> to mark covered cells so the renderer's normal
    /// emission pass skips them.
    /// </summary>
    /// <remarks>
    /// Sizes should reflect the cell rectangle the fragment will visually occupy on a supporting
    /// terminal — even though the protocol payload may emit at sub-cell or super-cell granularity.
    /// Over-reporting wastes cells; under-reporting causes normal-pass overdraw that corrupts the
    /// fragment's rendering.
    /// </remarks>
    Size GetSize();

    /// <summary>
    /// Return true when the supplied capabilities allow this fragment to render. The renderer
    /// skips emission when this returns false; the anchor cell is still marked as covered, so
    /// callers wanting a fallback path should check before adding the fragment (or use a higher-
    /// level <c>IContent</c> that orchestrates the choice).
    /// </summary>
    bool IsSupported(OutputCapabilities capabilities);

    /// <summary>
    /// Emit the fragment's bytes to <paramref name="output"/>. The renderer has already positioned
    /// the cursor at the anchor cell (<paramref name="row"/>, <paramref name="column"/>) and has
    /// bracketed this call with cursor save / restore, so implementations don't need to manage
    /// cursor state. SGR state at entry is undefined; emit explicit SGR for everything the
    /// fragment relies on.
    /// </summary>
    /// <param name="row">Anchor row (0-based) — passed for convenience; the cursor is already there.</param>
    /// <param name="column">Anchor column (0-based) — passed for convenience; the cursor is already there.</param>
    /// <param name="output">Destination buffer writer — usually backed by the session's output sink.</param>
    /// <param name="capabilities">Realized terminal capabilities — implementations may read them to adjust emission.</param>
    void Emit(int row, int column, IBufferWriter<byte> output, OutputCapabilities capabilities);
}
