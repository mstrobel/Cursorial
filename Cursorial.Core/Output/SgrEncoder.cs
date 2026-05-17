using System.Buffers;

namespace Cursorial.Output;

/// <summary>
/// Encodes a <see cref="Style"/> into the SGR escape-sequence bytes that drive a terminal's
/// rendering state. Pure — no awareness of terminal capabilities (use <c>StyleQuantizer</c> to
/// adapt a style for a given capability set first) and no internal "previously-emitted" state.
/// </summary>
/// <remarks>
/// <para>
/// Two operations are provided. <see cref="WriteAbsolute"/> emits <c>SGR 0</c> (reset all)
/// followed by the parameters needed to express <see cref="Style"/>, producing a sequence that
/// puts the terminal into a known state regardless of what was previously set. This is the
/// right call for one-off styled output ("print red text"). <see cref="WriteDelta"/> emits only
/// the parameters needed to transition from one <see cref="Style"/> to another — the right
/// call for a frame compositor that's painting many adjacent runs of styled cells.
/// </para>
/// <para>
/// Output goes into an <see cref="IBufferWriter{T}"/>. Both <see cref="System.IO.Pipelines.PipeWriter"/>
/// and <see cref="ArrayBufferWriter{T}"/> implement it, so the same encoder feeds both
/// live-terminal output and the diff renderer's scratch buffer.
/// </para>
/// </remarks>
public static class SgrEncoder
{
    private const byte Escape = 0x1B;
    private const byte CsiOpen = (byte) '[';
    private const byte CsiSeparator = (byte) ';';
    private const byte SgrFinal = (byte) 'm';
    private const byte SubParamSeparator = (byte) ':';

    /// <summary>
    /// Emit <c>CSI 0 m</c> — reset all SGR state to terminal defaults.
    /// </summary>
    public static void WriteReset(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var span = writer.GetSpan(4);
        span[0] = Escape;
        span[1] = CsiOpen;
        span[2] = (byte) '0';
        span[3] = SgrFinal;
        writer.Advance(4);
    }

    /// <summary>
    /// Emit an SGR sequence that puts the terminal's rendering state into <paramref name="style"/>
    /// regardless of what was previously active. Always begins with <c>SGR 0</c>.
    /// </summary>
    public static void WriteAbsolute(IBufferWriter<byte> writer, in Style style)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // Worst case: SGR 0 + every attribute (~9 small ints) + FG truecolor (5 ints) +
        // BG truecolor (5 ints) + underline shape (4 bytes) + underline truecolor (6 ints).
        // Each int is at most 3 ASCII digits. Round up to a comfortable budget.
        var buffer = writer.GetSpan(96);
        int written = 0;

        buffer[written++] = Escape;
        buffer[written++] = CsiOpen;

        // Always lead with "0" — guarantees a known starting state.
        buffer[written++] = (byte) '0';
        bool needSeparator = true;

        WriteStyleParameters(style, buffer, ref written, ref needSeparator);

        buffer[written++] = SgrFinal;
        writer.Advance(written);
    }

    /// <summary>
    /// Emit an SGR sequence that transitions the terminal from <paramref name="from"/> to
    /// <paramref name="to"/>. No <c>SGR 0</c> prefix — only the parameters that changed are
    /// emitted, including reset codes for attributes that <paramref name="from"/> had set and
    /// <paramref name="to"/> does not. Emits nothing (writes zero bytes) when the two styles
    /// are equal.
    /// </summary>
    public static void WriteDelta(IBufferWriter<byte> writer, in Style from, in Style to)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (from == to) return;

        var buffer = writer.GetSpan(96);
        int written = 0;

        buffer[written++] = Escape;
        buffer[written++] = CsiOpen;
        bool needSeparator = false;

        // Foreground.
        if (from.Foreground != to.Foreground)
        {
            WriteForeground(to.Foreground, buffer, ref written, ref needSeparator);
        }

        // Background.
        if (from.Background != to.Background)
        {
            WriteBackground(to.Background, buffer, ref written, ref needSeparator);
        }

        // Attributes: figure out which bits are turning on (need to enable) and which are
        // turning off (need explicit disable codes — there's no "blanket reset Italic" so
        // each attribute has its own disable parameter).
        var added = to.Attributes & ~from.Attributes;
        var removed = from.Attributes & ~to.Attributes;

        if (added != TextAttributes.None || removed != TextAttributes.None)
        {
            WriteAttributeChanges(added, removed, to.UnderlineStyle, buffer, ref written, ref needSeparator);
        }
        else if (to.Attributes.HasFlag(TextAttributes.Underline) && from.UnderlineStyle != to.UnderlineStyle)
        {
            // Underline was already on; only the shape changed. Re-emit it with the new shape.
            WriteUnderline(to.UnderlineStyle, buffer, ref written, ref needSeparator);
        }

        // Underline color.
        if (from.UnderlineColor != to.UnderlineColor)
        {
            WriteUnderlineColor(to.UnderlineColor, buffer, ref written, ref needSeparator);
        }

        if (written == 2)
        {
            // We wrote `ESC [` but nothing else — the styles differ in a way we don't
            // surface (e.g., only UnderlineStyle when Underline isn't set). Skip the empty
            // sequence to avoid a malformed `ESC [ m`.
            return;
        }

        buffer[written++] = SgrFinal;
        writer.Advance(written);
    }

    private static void WriteStyleParameters(
        in Style style,
        Span<byte> buffer,
        ref int written,
        ref bool needSeparator)
    {
        // Attributes.
        var attrs = style.Attributes;
        if (attrs.HasFlag(TextAttributes.Bold)) WriteParam(1, buffer, ref written, ref needSeparator);
        if (attrs.HasFlag(TextAttributes.Faint)) WriteParam(2, buffer, ref written, ref needSeparator);
        if (attrs.HasFlag(TextAttributes.Italic)) WriteParam(3, buffer, ref written, ref needSeparator);

        if (attrs.HasFlag(TextAttributes.Underline))
            WriteUnderline(style.UnderlineStyle, buffer, ref written, ref needSeparator);

        if (attrs.HasFlag(TextAttributes.Blink)) WriteParam(5, buffer, ref written, ref needSeparator);
        if (attrs.HasFlag(TextAttributes.Inverse)) WriteParam(7, buffer, ref written, ref needSeparator);
        if (attrs.HasFlag(TextAttributes.Hidden)) WriteParam(8, buffer, ref written, ref needSeparator);
        if (attrs.HasFlag(TextAttributes.Strikethrough)) WriteParam(9, buffer, ref written, ref needSeparator);
        if (attrs.HasFlag(TextAttributes.Overline)) WriteParam(53, buffer, ref written, ref needSeparator);

        if (!style.Foreground.IsDefault) WriteForeground(style.Foreground, buffer, ref written, ref needSeparator);
        if (!style.Background.IsDefault) WriteBackground(style.Background, buffer, ref written, ref needSeparator);
        if (!style.UnderlineColor.IsDefault) WriteUnderlineColor(style.UnderlineColor, buffer, ref written, ref needSeparator);
    }

    private static void WriteAttributeChanges(
        TextAttributes added,
        TextAttributes removed,
        UnderlineStyle underlineStyle,
        Span<byte> buffer,
        ref int written,
        ref bool needSeparator)
    {
        // Per-attribute SGR codes and their matching reset codes. Bold and Faint share SGR 22
        // for reset (xterm convention); we emit 22 once if either was removed.
        if (removed.HasFlag(TextAttributes.Bold) || removed.HasFlag(TextAttributes.Faint))
            WriteParam(22, buffer, ref written, ref needSeparator);

        if (removed.HasFlag(TextAttributes.Italic)) WriteParam(23, buffer, ref written, ref needSeparator);
        if (removed.HasFlag(TextAttributes.Underline)) WriteParam(24, buffer, ref written, ref needSeparator);
        if (removed.HasFlag(TextAttributes.Blink)) WriteParam(25, buffer, ref written, ref needSeparator);
        if (removed.HasFlag(TextAttributes.Inverse)) WriteParam(27, buffer, ref written, ref needSeparator);
        if (removed.HasFlag(TextAttributes.Hidden)) WriteParam(28, buffer, ref written, ref needSeparator);
        if (removed.HasFlag(TextAttributes.Strikethrough)) WriteParam(29, buffer, ref written, ref needSeparator);
        if (removed.HasFlag(TextAttributes.Overline)) WriteParam(55, buffer, ref written, ref needSeparator);

        if (added.HasFlag(TextAttributes.Bold)) WriteParam(1, buffer, ref written, ref needSeparator);
        if (added.HasFlag(TextAttributes.Faint)) WriteParam(2, buffer, ref written, ref needSeparator);
        if (added.HasFlag(TextAttributes.Italic)) WriteParam(3, buffer, ref written, ref needSeparator);

        if (added.HasFlag(TextAttributes.Underline))
            WriteUnderline(underlineStyle, buffer, ref written, ref needSeparator);

        if (added.HasFlag(TextAttributes.Blink)) WriteParam(5, buffer, ref written, ref needSeparator);
        if (added.HasFlag(TextAttributes.Inverse)) WriteParam(7, buffer, ref written, ref needSeparator);
        if (added.HasFlag(TextAttributes.Hidden)) WriteParam(8, buffer, ref written, ref needSeparator);
        if (added.HasFlag(TextAttributes.Strikethrough)) WriteParam(9, buffer, ref written, ref needSeparator);
        if (added.HasFlag(TextAttributes.Overline)) WriteParam(53, buffer, ref written, ref needSeparator);
    }

    private static void WriteUnderline(
        UnderlineStyle style,
        Span<byte> buffer,
        ref int written,
        ref bool needSeparator)
    {
        if (style == UnderlineStyle.Single)
        {
            WriteParam(4, buffer, ref written, ref needSeparator);
            return;
        }

        // Extended forms use the 4:n colon sub-parameter syntax (xterm + Kitty + WezTerm + …).
        WriteSeparatorIfNeeded(buffer, ref written, ref needSeparator);
        WriteAsciiInt(4, buffer, ref written);
        buffer[written++] = SubParamSeparator;
        WriteAsciiInt((uint) style + 1, buffer, ref written); // Double=2, Curly=3, Dotted=4, Dashed=5.
        needSeparator = true;
    }

    private static void WriteForeground(
        Color color,
        Span<byte> buffer,
        ref int written,
        ref bool needSeparator)
    {
        switch (color.Kind)
        {
            case ColorKind.Default:
                WriteParam(39, buffer, ref written, ref needSeparator);
                return;

            case ColorKind.Palette when color.PaletteIndex < 8:
                WriteParam(30u + color.PaletteIndex, buffer, ref written, ref needSeparator);
                return;

            case ColorKind.Palette when color.PaletteIndex < 16:
                WriteParam(90u + (color.PaletteIndex - 8u), buffer, ref written, ref needSeparator);
                return;

            case ColorKind.Palette:
                WriteSeparatorIfNeeded(buffer, ref written, ref needSeparator);
                WriteAsciiInt(38, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(5, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(color.PaletteIndex, buffer, ref written);
                needSeparator = true;
                return;

            case ColorKind.Rgb:
                WriteSeparatorIfNeeded(buffer, ref written, ref needSeparator);
                WriteAsciiInt(38, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(2, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(color.Red, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(color.Green, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(color.Blue, buffer, ref written);
                needSeparator = true;
                return;
        }
    }

    private static void WriteBackground(
        Color color,
        Span<byte> buffer,
        ref int written,
        ref bool needSeparator)
    {
        switch (color.Kind)
        {
            case ColorKind.Default:
                WriteParam(49, buffer, ref written, ref needSeparator);
                return;

            case ColorKind.Palette when color.PaletteIndex < 8:
                WriteParam(40u + color.PaletteIndex, buffer, ref written, ref needSeparator);
                return;

            case ColorKind.Palette when color.PaletteIndex < 16:
                WriteParam(100u + (color.PaletteIndex - 8u), buffer, ref written, ref needSeparator);
                return;

            case ColorKind.Palette:
                WriteSeparatorIfNeeded(buffer, ref written, ref needSeparator);
                WriteAsciiInt(48, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(5, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(color.PaletteIndex, buffer, ref written);
                needSeparator = true;
                return;

            case ColorKind.Rgb:
                WriteSeparatorIfNeeded(buffer, ref written, ref needSeparator);
                WriteAsciiInt(48, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(2, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(color.Red, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(color.Green, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(color.Blue, buffer, ref written);
                needSeparator = true;
                return;
        }
    }

    private static void WriteUnderlineColor(
        Color color,
        Span<byte> buffer,
        ref int written,
        ref bool needSeparator)
    {
        switch (color.Kind)
        {
            case ColorKind.Default:
                // SGR 59 — restore underline color to "follow foreground."
                WriteParam(59, buffer, ref written, ref needSeparator);
                return;

            case ColorKind.Palette:
                WriteSeparatorIfNeeded(buffer, ref written, ref needSeparator);
                WriteAsciiInt(58, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(5, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(color.PaletteIndex, buffer, ref written);
                needSeparator = true;
                return;

            case ColorKind.Rgb:
                WriteSeparatorIfNeeded(buffer, ref written, ref needSeparator);
                WriteAsciiInt(58, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(2, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(color.Red, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(color.Green, buffer, ref written);
                buffer[written++] = CsiSeparator;
                WriteAsciiInt(color.Blue, buffer, ref written);
                needSeparator = true;
                return;
        }
    }

    private static void WriteParam(
        uint value,
        Span<byte> buffer,
        ref int written,
        ref bool needSeparator)
    {
        WriteSeparatorIfNeeded(buffer, ref written, ref needSeparator);
        WriteAsciiInt(value, buffer, ref written);
        needSeparator = true;
    }

    private static void WriteSeparatorIfNeeded(Span<byte> buffer, ref int written, ref bool needSeparator)
    {
        if (needSeparator)
        {
            buffer[written++] = CsiSeparator;
        }
    }

    private static void WriteAsciiInt(uint value, Span<byte> buffer, ref int written)
    {
        // Up to 10 decimal digits for a uint; SGR values realistically fit in 3.
        Span<byte> tmp = stackalloc byte[10];
        int idx = tmp.Length;

        do
        {
            tmp[--idx] = (byte) ('0' + value % 10);
            value /= 10;
        }
        while (value > 0);

        int len = tmp.Length - idx;
        tmp.Slice(idx, len).CopyTo(buffer[written..]);
        written += len;
    }
}