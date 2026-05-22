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
/// <summary>
/// Convenience extension methods on <see cref="IContent"/> for common call shapes.
/// </summary>
public static class ContentExtensions
{
    /// <summary>
    /// Paint <paramref name="content"/> with an implicit bounds rectangle running from
    /// <c>(column, row)</c> to the buffer's far edge — the simple "paint here, use whatever
    /// space is left" call. For layout-aware sizing the <see cref="IContent.Paint(CellBuffer, Rect, in Style, OutputCapabilities)"/>
    /// overload should be called directly with an explicit <see cref="Rect"/>.
    /// </summary>
    public static Rect Paint(this IContent content,
                             in CellBufferView buffer,
                             int column,
                             int row,
                             in Style style,
                             OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (buffer.IsEmpty) return new Rect(column, row, 0, 0);

        var bounds = new Rect(column, row,
                              Math.Max(0, buffer.Columns - column),
                              Math.Max(0, buffer.Rows - row));
        return content.Paint(buffer, bounds, in style, capabilities);
    }
}

public interface IContent
{
    /// <summary>
    /// Compute the cell footprint this content wants given the bounding-box size it would be
    /// allotted. Pure — no side effects, may be called multiple times during a layout pass.
    /// Implementations should produce a desired <see cref="Size"/> that fits naturally in
    /// <paramref name="availableSpace"/>; wrap-aware content (formatted text, etc.) should
    /// perform internal wrapping at <c>availableSpace.Columns</c> and report the resulting
    /// line count. The returned size may legitimately exceed <paramref name="availableSpace"/>
    /// (the parent will then clip or scroll) but doing so signals "this content can't render
    /// faithfully in that envelope."
    /// </summary>
    /// <param name="availableSpace">Maximum cells the content may use. Either dimension may be int.MaxValue when unconstrained.</param>
    /// <param name="capabilities">Realized terminal capabilities — affects which rendering path Measure should account for (e.g., a Kitty image's footprint is different from its glyph placeholder).</param>
    Size Measure(Size availableSpace, OutputCapabilities capabilities);

    /// <summary>
    /// Paint this content into the allocated <paramref name="bounds"/>. Implementations may
    /// write cells directly, attach a fragment to the buffer's sidecar, or do both — the
    /// contract is just "after this call, the painted region matches the content as it fits
    /// inside <paramref name="bounds"/>." Content that would naturally render larger than
    /// <paramref name="bounds"/> is responsible for clipping, scaling, or falling back to a
    /// smaller representation. Returns the rectangle actually painted (may be smaller than
    /// <paramref name="bounds"/> when the content didn't fill).
    /// </summary>
    /// <param name="buffer">Target cell buffer.</param>
    /// <param name="bounds">Allocated rectangle in buffer-cell coordinates.</param>
    /// <param name="style">Style applied to the rendered content. Fragments use this as their SGR backdrop; fonts pass it to <see cref="CellBuffer.Set"/>.</param>
    /// <param name="capabilities">Realized terminal capabilities — drives which rendering path the content chooses.</param>
    Rect Paint(in CellBufferView buffer, in Rect bounds, in Style style, OutputCapabilities capabilities);
}