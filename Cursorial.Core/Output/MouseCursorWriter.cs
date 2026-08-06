using System.Buffers;

using Cursorial.Media;

namespace Cursorial.Output;

/// <summary>
/// Emits Kitty's OSC 22 pointer-shape sequences — used to ask the terminal emulator which GUI
/// mouse cursor to render while the pointer is over the application. Mirrors CSS's
/// <c>cursor</c> property: hand for clickable elements, I-beam for text inputs, the various
/// edge / corner resize cursors for window-chrome handles, and so on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capability gating</b> belongs to the caller — check
/// <see cref="Output.Capabilities.OutputProtocolCapabilities.MouseCursorShape"/>. Today the
/// protocol is honored by Kitty, Ghostty, and Foot; other terminals strip the OSC silently.
/// </para>
/// <para>
/// <b>Stack semantics.</b> Kitty maintains a per-terminal pointer-shape stack.
/// <see cref="WriteSet(IBufferWriter&lt;byte&gt;, MouseCursorShape)"/> replaces the current
/// shape without touching the stack — the right primitive for cursor changes driven by hover
/// state. <see cref="WritePush(IBufferWriter&lt;byte&gt;, MouseCursorShape)"/> / <see cref="WritePop"/>
/// are the matched-pair form, useful when an application needs to temporarily override the shape
/// and later restore whatever the surrounding code had set (e.g., a modal dialog that shows wait
/// during a long operation, then restores the prior shape on dismiss). Pushes and pops should
/// be balanced.
/// </para>
/// <para>
/// <b>Fallback chains.</b> The <see cref="ReadOnlySpan{MouseCursorShape}"/> overloads emit a
/// comma-separated list; the terminal picks the first shape it knows. Useful when an
/// application wants a richer cursor on capable terminals while keeping a sensible fallback
/// (e.g., <c>[Progress, Wait, Default]</c>).
/// </para>
/// </remarks>
public static class MouseCursorWriter
{
    /// <summary>
    /// Longest CSS cursor name in the supported set is <c>vertical-text</c> at 13 bytes. The
    /// worst-case size calculation uses this constant to pre-size the destination span.
    /// </summary>
    private const int MaxShapeNameBytes = 16;

    /// <summary>Set the pointer shape (replaces the current, does not push onto the stack).</summary>
    public static void WriteSet(IBufferWriter<byte> writer, MouseCursorShape shape)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Write(writer, op: 0, [shape]);
    }

    /// <summary>
    /// Set the pointer shape from a fallback chain — the terminal picks the first shape it
    /// recognizes. Useful when richer shapes (e.g., <see cref="MouseCursorShape.Progress"/>)
    /// have a sensible degradation to plainer ones the terminal is more likely to know.
    /// </summary>
    public static void WriteSet(IBufferWriter<byte> writer, ReadOnlySpan<MouseCursorShape> shapes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (shapes.IsEmpty)
            throw new ArgumentException("At least one shape is required. Use WriteReset to revert to the terminal default.", nameof(shapes));
        Write(writer, op: 0, shapes);
    }

    /// <summary>
    /// Push a shape onto the terminal's pointer-shape stack. Pair with <see cref="WritePop"/>
    /// to restore the prior shape — useful for transient overrides like a modal-dialog wait
    /// cursor.
    /// </summary>
    public static void WritePush(IBufferWriter<byte> writer, MouseCursorShape shape)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Write(writer, op: (byte) '>', [shape]);
    }

    /// <summary>Push a fallback-chain shape onto the pointer-shape stack.</summary>
    public static void WritePush(IBufferWriter<byte> writer, ReadOnlySpan<MouseCursorShape> shapes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (shapes.IsEmpty)
            throw new ArgumentException("At least one shape is required.", nameof(shapes));
        Write(writer, op: (byte) '>', shapes);
    }

    /// <summary>
    /// Pop the topmost shape from the terminal's pointer-shape stack, restoring whatever was
    /// active before the matching <see cref="WritePush(IBufferWriter{byte}, MouseCursorShape)"/>.
    /// Popping with an empty stack is benign — the terminal reverts to its default.
    /// </summary>
    public static void WritePop(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var sequence = VtOutputSequences.KittyPointerShape.Pop;
        var dest = writer.GetSpan(sequence.Length);
        sequence.CopyTo(dest);
        writer.Advance(sequence.Length);
    }

    /// <summary>
    /// Reset the pointer shape to the terminal's default. Equivalent to setting with an empty
    /// shape list per the protocol.
    /// </summary>
    public static void WriteReset(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var sequence = VtOutputSequences.KittyPointerShape.Reset;
        var dest = writer.GetSpan(sequence.Length);
        sequence.CopyTo(dest);
        writer.Advance(sequence.Length);
    }

    private static void Write(IBufferWriter<byte> writer, byte op, ReadOnlySpan<MouseCursorShape> shapes)
    {
        var prefix = VtOutputSequences.KittyPointerShape.Prefix;
        var terminator = VtOutputSequences.KittyPointerShape.StringTerminator;
        int worstCase = prefix.Length + 1 /* op */ + shapes.Length * (MaxShapeNameBytes + 1) + terminator.Length;
        var dest = writer.GetSpan(worstCase);
        int written = 0;

        prefix.CopyTo(dest);
        written += prefix.Length;

        if (op != 0)
            dest[written++] = op;

        for (int i = 0; i < shapes.Length; i++)
        {
            if (i > 0) dest[written++] = (byte) ',';
            var name = CssName(shapes[i]);
            name.CopyTo(dest[written..]);
            written += name.Length;
        }

        terminator.CopyTo(dest[written..]);
        written += terminator.Length;

        writer.Advance(written);
    }

    private static ReadOnlySpan<byte> CssName(MouseCursorShape shape) => shape switch
    {
        MouseCursorShape.Default      => "default"u8,
        MouseCursorShape.None         => "none"u8,
        MouseCursorShape.ContextMenu  => "context-menu"u8,
        MouseCursorShape.Help         => "help"u8,
        MouseCursorShape.Pointer      => "pointer"u8,
        MouseCursorShape.Progress     => "progress"u8,
        MouseCursorShape.Wait         => "wait"u8,
        MouseCursorShape.Cell         => "cell"u8,
        MouseCursorShape.Crosshair    => "crosshair"u8,
        MouseCursorShape.Text         => "text"u8,
        MouseCursorShape.VerticalText => "vertical-text"u8,
        MouseCursorShape.Alias        => "alias"u8,
        MouseCursorShape.Copy         => "copy"u8,
        MouseCursorShape.Move         => "move"u8,
        MouseCursorShape.NoDrop       => "no-drop"u8,
        MouseCursorShape.NotAllowed   => "not-allowed"u8,
        MouseCursorShape.Grab         => "grab"u8,
        MouseCursorShape.Grabbing     => "grabbing"u8,
        MouseCursorShape.EResize      => "e-resize"u8,
        MouseCursorShape.NResize      => "n-resize"u8,
        MouseCursorShape.NeResize     => "ne-resize"u8,
        MouseCursorShape.NwResize     => "nw-resize"u8,
        MouseCursorShape.SResize      => "s-resize"u8,
        MouseCursorShape.SeResize     => "se-resize"u8,
        MouseCursorShape.SwResize     => "sw-resize"u8,
        MouseCursorShape.WResize      => "w-resize"u8,
        MouseCursorShape.EwResize     => "ew-resize"u8,
        MouseCursorShape.NsResize     => "ns-resize"u8,
        MouseCursorShape.NeswResize   => "nesw-resize"u8,
        MouseCursorShape.NwseResize   => "nwse-resize"u8,
        MouseCursorShape.ColResize    => "col-resize"u8,
        MouseCursorShape.RowResize    => "row-resize"u8,
        MouseCursorShape.AllScroll    => "all-scroll"u8,
        MouseCursorShape.ZoomIn       => "zoom-in"u8,
        MouseCursorShape.ZoomOut      => "zoom-out"u8,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown MouseCursorShape value."),
    };
}
