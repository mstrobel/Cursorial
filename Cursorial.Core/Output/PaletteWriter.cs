using System.Buffers;

namespace Cursorial.Output;

/// <summary>
/// Pure byte-emitting writer for the OSC 4 / OSC 104 palette protocol. Mirrors the shape of
/// the other <c>*Writer</c> classes in this namespace — operations take an
/// <see cref="IBufferWriter{T}"/> destination so they compose with any of the codebase's
/// byte sinks (<see cref="System.IO.Pipelines.PipeWriter"/>,
/// <see cref="ArrayBufferWriter{T}"/>, etc.).
/// </summary>
/// <remarks>
/// <para>
/// This is the low-level surface. The higher-level
/// <c>Cursorial.Terminal.TerminalPalette</c> controller layers query/restore semantics on top
/// of these primitives.
/// </para>
/// </remarks>
public static class PaletteWriter
{
    /// <summary>
    /// Emit <c>ESC ] 4 ; &lt;index&gt; ; rgb:RRRR/GGGG/BBBB ST</c>, setting the 256-color palette
    /// entry at <paramref name="index"/> to the supplied RGB color. Each 8-bit channel is
    /// emitted as a 4-digit hex value with the byte duplicated into both nibbles, matching the
    /// X11 16-bit channel convention.
    /// </summary>
    public static void WriteSet(IBufferWriter<byte> output, byte index, byte red, byte green, byte blue)
    {
        ArgumentNullException.ThrowIfNull(output);

        // Max length: prefix(4) + index(3) + ";rgb:"(5) + 14 hex+slashes + ST(2) = 28.
        Span<byte> scratch = stackalloc byte[32];
        int written = 0;

        AppendSpan(scratch, ref written, VtOutputSequences.Palette.SetPrefix);
        VtWriterUtilities.WriteAsciiInt(index, scratch, ref written);
        scratch[written++] = (byte) ';';
        AppendSpan(scratch, ref written, VtOutputSequences.Palette.RgbInfix);
        AppendHexChannel(scratch, ref written, red);
        scratch[written++] = (byte) '/';
        AppendHexChannel(scratch, ref written, green);
        scratch[written++] = (byte) '/';
        AppendHexChannel(scratch, ref written, blue);
        AppendSpan(scratch, ref written, VtOutputSequences.Palette.StringTerminator);

        Flush(output, scratch[..written]);
    }

    /// <summary>
    /// Emit <c>ESC ] 10 ; rgb:RRRR/GGGG/BBBB ST</c>, setting the default foreground to the supplied
    /// RGB color. Each 8-bit channel is emitted as a 4-digit hex value with the byte duplicated into
    /// both nibbles, matching the X11 16-bit channel convention.
    /// </summary>
    public static void WriteSetForeground(IBufferWriter<byte> output, byte red, byte green, byte blue)
    {
        ArgumentNullException.ThrowIfNull(output);

        // Max length: prefix(4) + index(3) + ";rgb:"(5) + 14 hex+slashes + ST(2) = 28.
        Span<byte> scratch = stackalloc byte[32];
        int written = 0;

        AppendSpan(scratch, ref written, VtOutputSequences.Palette.SetForegroundPrefix);
        AppendSpan(scratch, ref written, VtOutputSequences.Palette.RgbInfix);
        AppendHexChannel(scratch, ref written, red);
        scratch[written++] = (byte) '/';
        AppendHexChannel(scratch, ref written, green);
        scratch[written++] = (byte) '/';
        AppendHexChannel(scratch, ref written, blue);
        AppendSpan(scratch, ref written, VtOutputSequences.Palette.StringTerminator);

        Flush(output, scratch[..written]);
    }

    /// <summary>
    /// Emit <c>ESC ] 11 ; rgb:RRRR/GGGG/BBBB ST</c>, setting the default background to the supplied
    /// RGB color. Each 8-bit channel is emitted as a 4-digit hex value with the byte duplicated into
    /// both nibbles, matching the X11 16-bit channel convention.
    /// </summary>
    public static void WriteSetBackground(IBufferWriter<byte> output, byte red, byte green, byte blue)
    {
        ArgumentNullException.ThrowIfNull(output);

        // Max length: prefix(4) + index(3) + ";rgb:"(5) + 14 hex+slashes + ST(2) = 28.
        Span<byte> scratch = stackalloc byte[32];
        int written = 0;

        AppendSpan(scratch, ref written, VtOutputSequences.Palette.SetBackgroundPrefix);
        AppendSpan(scratch, ref written, VtOutputSequences.Palette.RgbInfix);
        AppendHexChannel(scratch, ref written, red);
        scratch[written++] = (byte) '/';
        AppendHexChannel(scratch, ref written, green);
        scratch[written++] = (byte) '/';
        AppendHexChannel(scratch, ref written, blue);
        AppendSpan(scratch, ref written, VtOutputSequences.Palette.StringTerminator);

        Flush(output, scratch[..written]);
    }

    /// <summary>
    /// Emit <c>ESC ] 11 ; rgb:RRRR/GGGG/BBBB ST</c>, setting the default background to the supplied
    /// RGB color. Each 8-bit channel is emitted as a 4-digit hex value with the byte duplicated into
    /// both nibbles, matching the X11 16-bit channel convention.
    /// </summary>
    public static void WriteSetCursor(IBufferWriter<byte> output, byte red, byte green, byte blue)
    {
        ArgumentNullException.ThrowIfNull(output);

        // Max length: prefix(4) + index(3) + ";rgb:"(5) + 14 hex+slashes + ST(2) = 28.
        Span<byte> scratch = stackalloc byte[32];
        int written = 0;

        AppendSpan(scratch, ref written, VtOutputSequences.Palette.SetCursorPrefix);
        AppendSpan(scratch, ref written, VtOutputSequences.Palette.RgbInfix);
        AppendHexChannel(scratch, ref written, red);
        scratch[written++] = (byte) '/';
        AppendHexChannel(scratch, ref written, green);
        scratch[written++] = (byte) '/';
        AppendHexChannel(scratch, ref written, blue);
        AppendSpan(scratch, ref written, VtOutputSequences.Palette.StringTerminator);

        Flush(output, scratch[..written]);
    }

    /// <summary>
    /// Emit <c>ESC ] 4 ; &lt;index&gt; ; ? ST</c>, requesting the current palette entry at
    /// <paramref name="index"/>. The terminal's reply arrives as a <c>DeviceResponseEvent</c>
    /// with <c>Kind = PaletteColor</c>; consumers route the response through their own input
    /// path.
    /// </summary>
    public static void WriteQuery(IBufferWriter<byte> output, byte index)
    {
        ArgumentNullException.ThrowIfNull(output);

        Span<byte> scratch = stackalloc byte[16];
        int written = 0;

        AppendSpan(scratch, ref written, VtOutputSequences.Palette.SetPrefix);
        VtWriterUtilities.WriteAsciiInt(index, scratch, ref written);
        AppendSpan(scratch, ref written, VtOutputSequences.Palette.QuerySuffix);
        AppendSpan(scratch, ref written, VtOutputSequences.Palette.StringTerminator);

        Flush(output, scratch[..written]);
    }

    /// <summary>
    /// Emit <c>ESC ] 104 ; &lt;index&gt; ST</c>, resetting a single palette entry to the
    /// terminal's startup default.
    /// </summary>
    public static void WriteReset(IBufferWriter<byte> output, byte index)
    {
        ArgumentNullException.ThrowIfNull(output);

        Span<byte> scratch = stackalloc byte[16];
        int written = 0;

        AppendSpan(scratch, ref written, VtOutputSequences.Palette.ResetPrefix);
        scratch[written++] = (byte) ';';
        VtWriterUtilities.WriteAsciiInt(index, scratch, ref written);
        AppendSpan(scratch, ref written, VtOutputSequences.Palette.StringTerminator);

        Flush(output, scratch[..written]);
    }

    /// <summary>
    /// Emit <c>ESC ] 104 ST</c>, resetting every entry of the 256-color palette to its startup
    /// default in one round-trip. Also emits <c>ESC ] 110 ST</c>, <c>ESC ] 111 ST</c>, and
    /// <c>ESC ] 112 ST</c> to reset the terminal's default foreground, background, and cursor.
    /// </summary>
    public static void WriteResetAll(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        Span<byte> scratch = stackalloc byte[8];
        int written = 0;

        AppendSpan(scratch, ref written, VtOutputSequences.Palette.ResetPrefix);
        AppendSpan(scratch, ref written, VtOutputSequences.Palette.StringTerminator);

        Flush(output, scratch[..written]);
        
        WriteResetForeground(output);
        WriteResetBackground(output);
        WriteResetCursor(output);
    }

    /// <summary>
    /// Emit <c>ESC ] 110 ST</c>, resetting the terminal's default foreground color.
    /// </summary>
    public static void WriteResetForeground(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        Span<byte> scratch = stackalloc byte[8];
        int written = 0;

        AppendSpan(scratch, ref written, VtOutputSequences.Palette.ResetForegroundPrefix);
        AppendSpan(scratch, ref written, VtOutputSequences.Palette.StringTerminator);

        Flush(output, scratch[..written]);
    }

    /// <summary>
    /// Emit <c>ESC ] 111 ST</c>, resetting the terminal's default background color.
    /// </summary>
    public static void WriteResetBackground(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        Span<byte> scratch = stackalloc byte[8];
        int written = 0;

        AppendSpan(scratch, ref written, VtOutputSequences.Palette.ResetBackgroundPrefix);
        AppendSpan(scratch, ref written, VtOutputSequences.Palette.StringTerminator);

        Flush(output, scratch[..written]);
    }

    /// <summary>
    /// Emit <c>ESC ] 111 2T</c>, resetting the terminal's default cursor color.
    /// </summary>
    public static void WriteResetCursor(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        Span<byte> scratch = stackalloc byte[8];
        int written = 0;

        AppendSpan(scratch, ref written, VtOutputSequences.Palette.ResetCursorPrefix);
        AppendSpan(scratch, ref written, VtOutputSequences.Palette.StringTerminator);

        Flush(output, scratch[..written]);
    }

    /// <summary>
    /// Emit <c>ESC ] 104 ; i1 ; i2 ; … ST</c>, resetting the supplied entries in one round-trip.
    /// Empty <paramref name="indices"/> resets nothing (no sequence is emitted).
    /// </summary>
    public static void WriteResetMany(IBufferWriter<byte> output, ReadOnlySpan<byte> indices)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (indices.IsEmpty) return;

        // ResetPrefix(6) + (";<index>")(up to 4 chars) per index + ST(2). Worst case 256 indices
        // → 6 + 256*4 + 2 = 1032. Heap-allocate when it'd overflow a reasonable stack budget.
        int worstCase = VtOutputSequences.Palette.ResetPrefix.Length
                        + indices.Length * 4
                        + VtOutputSequences.Palette.StringTerminator.Length;

        byte[]? rented = null;
        Span<byte> scratch = worstCase <= 128
            ? stackalloc byte[128]
            : (rented = ArrayPool<byte>.Shared.Rent(worstCase));

        try
        {
            int written = 0;
            AppendSpan(scratch, ref written, VtOutputSequences.Palette.ResetPrefix);
            foreach (byte index in indices)
            {
                scratch[written++] = (byte) ';';
                VtWriterUtilities.WriteAsciiInt(index, scratch, ref written);
            }
            AppendSpan(scratch, ref written, VtOutputSequences.Palette.StringTerminator);
            Flush(output, scratch[..written]);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void AppendSpan(Span<byte> buffer, ref int written, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(buffer[written..]);
        written += bytes.Length;
    }

    /// <summary>
    /// Emit a 4-digit lowercase hex channel for an 8-bit value, duplicating both nibbles into
    /// the high and low bytes of the 16-bit X11 form (e.g. <c>0xAB</c> becomes <c>"abab"</c>).
    /// </summary>
    private static void AppendHexChannel(Span<byte> buffer, ref int written, byte value)
    {
        int high = value >> 4;
        int low = value & 0xF;
        buffer[written++] = HexDigit(high);
        buffer[written++] = HexDigit(low);
        buffer[written++] = HexDigit(high);
        buffer[written++] = HexDigit(low);
    }

    private static byte HexDigit(int nibble) =>
        (byte) (nibble < 10 ? '0' + nibble : 'a' + nibble - 10);

    private static void Flush(IBufferWriter<byte> output, ReadOnlySpan<byte> bytes)
    {
        var dest = output.GetSpan(bytes.Length);
        bytes.CopyTo(dest);
        output.Advance(bytes.Length);
    }
}
