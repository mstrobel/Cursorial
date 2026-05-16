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
/// <b>Layer.</b> Each fragment declares whether its payload lives in the cell stream
/// (<see cref="FragmentLayer.Cells"/>) or on a separate display plane
/// (<see cref="FragmentLayer.Overlay"/>). The renderer uses this to decide what happens to the
/// cells under the fragment: Cell-layer fragments skip the foreground glyph in those cells
/// (the fragment's payload owns them) but still paint the cell's background so panels show
/// through; Overlay-layer fragments leave cells untouched. See <see cref="FragmentLayer"/> for
/// the full contract.
/// </para>
/// <para>
/// <b>Diffing.</b> The renderer snapshots registered fragments per render and skips re-emission
/// on the next render when the same instance with the same anchor style is still registered.
/// Callers should reuse fragment instances across frames when content is stable — constructing
/// a new instance per frame defeats the diff. Mutating an instance in place is supported
/// (caller takes responsibility for not changing its size or coverage in ways the buffer would
/// need to know about).
/// </para>
/// <para>
/// <b>Capability gating.</b> The renderer calls <see cref="IsSupported"/> for every fragment on
/// every frame and skips emission when the answer is false. Implementations are expected to be
/// stateless and cheap to instantiate. When a Cell-layer fragment is unsupported the cells
/// under it render normally (no covered-cell treatment) — callers wanting a richer fallback
/// should build a higher-level <c>IContent</c> that chooses among multiple fragments.
/// </para>
/// <para>
/// <b>Cursor and SGR.</b> The renderer brackets every fragment emission (and erase) with
/// DECSC / DECRC and re-positions the cursor to the anchor cell before calling. Fragments may
/// emit any SGR they need inside the brackets without disturbing the renderer's tracked SGR
/// state for the next frame.
/// </para>
/// </remarks>
public interface IBufferFragment
{
    /// <summary>
    /// Classification of the fragment by display-stack layer — see <see cref="FragmentLayer"/>
    /// for the rendering implications. Defaults to <see cref="FragmentLayer.Cells"/> since
    /// every fragment currently shipped is cell-stream-based; future overlay-layer fragments
    /// override to return <see cref="FragmentLayer.Overlay"/>.
    /// </summary>
    FragmentLayer Layer => FragmentLayer.Cells;

    /// <summary>
    /// The cell footprint the fragment occupies, anchored at its registration cell. Used by the
    /// renderer to derive the covered-cells set for Cell-layer fragments (so the cell-emit
    /// pass can skip glyphs in those positions) and to bracket the visual region for layout.
    /// </summary>
    /// <remarks>
    /// Sizes should reflect the cell rectangle the fragment will visually occupy on a supporting
    /// terminal — even though the protocol payload may emit at sub-cell or super-cell granularity.
    /// Over-reporting wastes cells (the renderer skips more cells than the fragment paints);
    /// under-reporting causes normal-pass overdraw that corrupts the fragment's rendering.
    /// </remarks>
    Size GetSize();

    /// <summary>
    /// Return true when the supplied capabilities allow this fragment to render. The renderer
    /// skips both emission and the covered-cells treatment when this returns false — cells
    /// under an unsupported fragment render normally, so callers see the fall-through state
    /// without an explicit branch.
    /// </summary>
    bool IsSupported(OutputCapabilities capabilities);

    /// <summary>
    /// Emit the fragment's bytes to <paramref name="output"/>. The renderer has already positioned
    /// the cursor at the anchor cell and bracketed this call with cursor save / restore, so
    /// implementations don't need to manage cursor state. SGR state at entry is undefined; emit
    /// explicit SGR for everything the fragment relies on.
    /// </summary>
    void Emit(int row, int column, IBufferWriter<byte> output, OutputCapabilities capabilities);

    /// <summary>
    /// Emit the bytes that erase the fragment's contribution to the terminal's display. Called
    /// when the renderer's fragment diff detects this fragment was registered last render but
    /// isn't this render. Cell-layer fragments leave this as the default no-op: cell repainting
    /// in the next cell pass overwrites their payload. Overlay-layer fragments override to emit
    /// the protocol's delete command (Kitty graphics <c>a=d</c>, etc.) so the overlay actually
    /// goes away.
    /// </summary>
    void EmitErase(int row, int column, IBufferWriter<byte> output, OutputCapabilities capabilities) {}
}
