using Cursorial.Output;
using Cursorial.Output.Capabilities;

namespace Cursorial.Rendering.Content;

/// <summary>
/// The unifying abstraction for renderable content. An <see cref="IContent"/> decides at paint
/// time whether to flow through the cell-grid path (<see cref="Fonts.IGlyphFont"/> writes
/// cells via <see cref="CellBuffer.Set"/>) or the out-of-band path
/// (<see cref="Fragments.IBufferFragment"/> attached to the buffer for the renderer to emit as
/// a protocol payload). Capability-aware fallback also lives here — a single <c>IContent</c>
/// can try OSC 66, fall back to a Figlet font, then a Braille font, then a placeholder, and
/// the consumer never sees the chain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists alongside <c>IGlyphFont</c> and <c>IBufferFragment</c>.</b> Those are
/// the two concrete delivery mechanisms — cell glyphs vs. protocol fragments. Most content
/// types in practice want a chain across both (sized text → figlet, image → braille downscale),
/// and the consumer's call site shouldn't have to encode "did the terminal honor this protocol"
/// branching. <c>IContent</c> is where that orchestration lives.
/// </para>
/// </remarks>
public interface IContent
{
    /// <summary>
    /// Paint this content into the buffer at the anchor cell, using
    /// <paramref name="capabilities"/> to pick a rendering path. Returns the cell footprint
    /// actually occupied. Implementations may write cells directly, attach a fragment to the
    /// buffer's sidecar, or do both — the contract is just "after this call, the visible
    /// region matches the content."
    /// </summary>
    /// <param name="buffer">Target cell buffer.</param>
    /// <param name="column">Anchor column (0-based, left of the painted region).</param>
    /// <param name="row">Anchor row (0-based, top of the painted region).</param>
    /// <param name="style">Style applied to the rendered content. Fragments use this as their SGR backdrop; fonts pass it to <see cref="CellBuffer.Set"/>.</param>
    /// <param name="capabilities">Realized terminal capabilities — drives which rendering path the content chooses.</param>
    Size Paint(CellBuffer buffer, int column, int row, in Style style, OutputCapabilities capabilities);
}