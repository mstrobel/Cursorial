using System.Buffers;

using Cursorial.Media;
using Cursorial.Text;

namespace Cursorial.Output;

/// <summary>
/// Encodes a <see cref="CellStyle"/> into the SGR escape-sequence bytes that drive a terminal's
/// rendering state. Pure — no awareness of terminal capabilities (use <c>StyleQuantizer</c> to
/// adapt a style for a given capability set first) and no internal "previously-emitted" state.
/// </summary>
/// <remarks>
/// <para>
/// Two operations are provided. <see cref="WriteAbsolute"/> emits <c>SGR 0</c> (reset all)
/// followed by the parameters needed to express <see cref="CellStyle"/>, producing a sequence that
/// puts the terminal into a known state regardless of what was previously set. This is the
/// right call for one-off styled output ("print red text"). <see cref="WriteDelta"/> emits only
/// the parameters needed to transition from one <see cref="CellStyle"/> to another — the right
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
    public static void WriteAbsolute(IBufferWriter<byte> writer, in CellStyle style)
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
    /// <remarks>
    /// <para>
    /// The invariant: applied to a terminal whose SGR state is exactly <paramref name="from"/>,
    /// these bytes leave it in exactly <paramref name="to"/>. Two consequences drive the
    /// implementation, and neither follows from a naive added/removed set difference:
    /// </para>
    /// <list type="number">
    /// <item>
    /// Whenever a reset code is emitted, every piece of state that code clears which
    /// <paramref name="to"/> still wants must be re-emitted after it. <c>SGR 22</c> clears both
    /// Bold and Faint, so a weight present in <em>both</em> styles — and therefore in neither the
    /// added nor the removed set — still has to go back out (<c>Bold|Faint</c> → <c>Bold</c> is
    /// <c>22;1</c>, not a bare <c>22</c>).
    /// </item>
    /// <item>
    /// Every axis whose <em>value</em> differs must be emitted even when its on/off flag is
    /// unchanged. The underline shape is a value carried by the same <c>SGR 4</c> parameter that
    /// turns the underline on, so a shape change under a surviving Underline flag still emits
    /// <c>4:n</c>.
    /// </item>
    /// </list>
    /// </remarks>
    public static void WriteDelta(IBufferWriter<byte> writer, in CellStyle from, in CellStyle to)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (from == to) return;

        // Worst case: FG truecolor (16) + BG truecolor (17) + every attribute reset (24) +
        // underline truecolor (18) + `ESC [` and the final `m` (3). The reset and set phases
        // can't both be maximal: a reset only fires for an attribute the destination doesn't
        // want, so the only parameter that can join a full reset run is the `1`/`2` restored
        // after the shared 22 (+2). Comfortably inside the same 96-byte budget as WriteAbsolute.
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
        if (from.Background != to.Background && !to.Background.IsTransparent)
        {
            WriteBackground(to.Background, buffer, ref written, ref needSeparator);
        }

        // Attributes. Called unconditionally: "nothing to say" is not the same as "no flags
        // changed" — a bare underline-shape change moves no flags but still needs an SGR 4:n —
        // and the `written == 2` check below already elides a genuinely empty sequence.
        WriteAttributeChanges(from, to, buffer, ref written, ref needSeparator);

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
        in CellStyle style,
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
        if (style.Background is { IsDefault: false, IsTransparent: false }) WriteBackground(style.Background, buffer, ref written, ref needSeparator);
        if (!style.UnderlineColor.IsDefault) WriteUnderlineColor(style.UnderlineColor, buffer, ref written, ref needSeparator);
    }

    private static void WriteAttributeChanges(
        in CellStyle from,
        in CellStyle to,
        Span<byte> buffer,
        ref int written,
        ref bool needSeparator)
    {
        // SGR 22 is the one genuinely shared reset in Cursorial's model: it disables Bold AND
        // Faint (xterm convention), so it's emitted once if either weight was removed. Every
        // other reset below is effectively single-member. (ECMA-48 also has 23 clearing Fraktur
        // and 25 clearing rapid blink, but Cursorial models neither attribute, so neither reset
        // can do collateral damage here.)
        const TextAttributes weightMask = TextAttributes.Bold | TextAttributes.Faint;

        var added = to.Attributes & ~from.Attributes;
        var removed = from.Attributes & ~to.Attributes;

        // Reset phase — ascending parameter order.
        var weightReset = (removed & weightMask) != TextAttributes.None;
        if (weightReset) WriteParam(22, buffer, ref written, ref needSeparator);

        if (removed.HasFlag(TextAttributes.Italic)) WriteParam(23, buffer, ref written, ref needSeparator);
        if (removed.HasFlag(TextAttributes.Underline)) WriteParam(24, buffer, ref written, ref needSeparator);
        if (removed.HasFlag(TextAttributes.Blink)) WriteParam(25, buffer, ref written, ref needSeparator);
        if (removed.HasFlag(TextAttributes.Inverse)) WriteParam(27, buffer, ref written, ref needSeparator);
        if (removed.HasFlag(TextAttributes.Hidden)) WriteParam(28, buffer, ref written, ref needSeparator);
        if (removed.HasFlag(TextAttributes.Strikethrough)) WriteParam(29, buffer, ref written, ref needSeparator);
        if (removed.HasFlag(TextAttributes.Overline)) WriteParam(55, buffer, ref written, ref needSeparator);

        // `stale` = what `to` wants whose terminal state the reset phase did NOT leave correct:
        // newly-added, plus anything the shared 22 collaterally cleared. Always a subset of
        // to.Attributes, so this can never turn on something the destination doesn't want.
        // Another shared reset later means OR-ing one more `to.Attributes & <mask>` term here.
        var stale = added;
        if (weightReset) stale |= to.Attributes & weightMask;

        // Set phase — ascending parameter order.
        if (stale.HasFlag(TextAttributes.Bold)) WriteParam(1, buffer, ref written, ref needSeparator);
        if (stale.HasFlag(TextAttributes.Faint)) WriteParam(2, buffer, ref written, ref needSeparator);
        if (stale.HasFlag(TextAttributes.Italic)) WriteParam(3, buffer, ref written, ref needSeparator);

        // SGR 4 carries the shape, so it also goes stale when the shape changes under a surviving
        // Underline flag — the flag is then in neither `added` nor `removed`. Gated on the
        // destination actually wanting an underline, so a shape change with the flag off emits
        // nothing (the off-form is always the plain 24 above, never `4:0`).
        if (to.Attributes.HasFlag(TextAttributes.Underline) &&
            (stale.HasFlag(TextAttributes.Underline) || from.UnderlineStyle != to.UnderlineStyle))
        {
            WriteUnderline(to.UnderlineStyle, buffer, ref written, ref needSeparator);
        }

        if (stale.HasFlag(TextAttributes.Blink)) WriteParam(5, buffer, ref written, ref needSeparator);
        if (stale.HasFlag(TextAttributes.Inverse)) WriteParam(7, buffer, ref written, ref needSeparator);
        if (stale.HasFlag(TextAttributes.Hidden)) WriteParam(8, buffer, ref written, ref needSeparator);
        if (stale.HasFlag(TextAttributes.Strikethrough)) WriteParam(9, buffer, ref written, ref needSeparator);
        if (stale.HasFlag(TextAttributes.Overline)) WriteParam(53, buffer, ref written, ref needSeparator);
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

            // SGR 58 (underline color) is emitted in the ITU-T T.416 / ISO 8613-6 colon
            // sub-parameter form (58:5:idx, 58:2::r:g:b) rather than the legacy semicolon form
            // (58;5;idx, 58;2;r;g;b). Both are accepted by terminals that implement SGR 58
            // natively (xterm, kitty, WezTerm, foot, …), but the colon form is critical for
            // robustness behind a parser that does NOT implement 58 — notably ConPTY, which
            // mediates every Windows session under a third-party terminal. With the semicolon
            // form, an unknowing parser drops the `58` token but then applies each remaining
            // sub-value (2, r, g, b — or 5, idx) as an independent top-level SGR code; values
            // like 2 (faint) and 5 (blink) leak as real attributes and persist on every cell
            // painted afterward (the renderer never models blink, so it never emits SGR 25 to
            // clear it). The colon form packs the whole spec into a single logical parameter,
            // so a parser that doesn't recognize 58 skips the entire group cleanly. The RGB
            // form uses the spec's empty color-space-id field (the `::` after the `2`).
            case ColorKind.Palette:
                WriteSeparatorIfNeeded(buffer, ref written, ref needSeparator);
                WriteAsciiInt(58, buffer, ref written);
                buffer[written++] = SubParamSeparator;
                WriteAsciiInt(5, buffer, ref written);
                buffer[written++] = SubParamSeparator;
                WriteAsciiInt(color.PaletteIndex, buffer, ref written);
                needSeparator = true;
                return;

            case ColorKind.Rgb:
                WriteSeparatorIfNeeded(buffer, ref written, ref needSeparator);
                WriteAsciiInt(58, buffer, ref written);
                buffer[written++] = SubParamSeparator;
                WriteAsciiInt(2, buffer, ref written);
                buffer[written++] = SubParamSeparator; // empty color-space-id field …
                buffer[written++] = SubParamSeparator; // … (T.416 `58:2::r:g:b`).
                WriteAsciiInt(color.Red, buffer, ref written);
                buffer[written++] = SubParamSeparator;
                WriteAsciiInt(color.Green, buffer, ref written);
                buffer[written++] = SubParamSeparator;
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