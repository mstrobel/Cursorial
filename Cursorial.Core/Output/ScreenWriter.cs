using System.Buffers;

namespace Cursorial.Output;

/// <summary>
/// Emits screen-level escape sequences: clear screen and line, alternate-screen-buffer toggle,
/// scroll region configuration. Pure byte writers — output goes into an
/// <see cref="IBufferWriter{T}"/> so the same calls feed both live terminal output and the
/// diff renderer's scratch buffer.
/// </summary>
/// <remarks>
/// <para>
/// The alternate screen buffer is toggled via DECSET / DECRST 1049 — the form that saves the
/// cursor position on enter and restores it on leave. Use this around any full-screen UI so
/// the user's prior terminal contents (shell prompt, scrollback) are preserved.
/// </para>
/// <para>
/// Scroll region (<see cref="WriteScrollRegion"/>) is DECSTBM, configured with 1-based inclusive
/// row coordinates on the wire; this API exposes 0-based rows.
/// </para>
/// </remarks>
public static class ScreenWriter
{
    private const byte Escape = 0x1B;
    private const byte CsiOpen = (byte) '[';
    private const byte CsiSeparator = (byte) ';';

    /// <summary>Clear the entire screen (ED 2, <c>CSI 2 J</c>). Does not move the cursor.</summary>
    public static void WriteClearScreen(IBufferWriter<byte> writer) => WriteEd(writer, 2);

    /// <summary>Clear from the cursor to the end of the screen (ED 0, <c>CSI 0 J</c>).</summary>
    public static void WriteClearScreenAfter(IBufferWriter<byte> writer) => WriteEd(writer, 0);

    /// <summary>Clear from the start of the screen to the cursor (ED 1, <c>CSI 1 J</c>).</summary>
    public static void WriteClearScreenBefore(IBufferWriter<byte> writer) => WriteEd(writer, 1);

    /// <summary>
    /// Clear the entire screen and the scrollback buffer (ED 3, <c>CSI 3 J</c>). Treated as a
    /// no-op by terminals that don't implement the xterm scrollback-clear extension.
    /// </summary>
    public static void WriteClearScreenAndScrollback(IBufferWriter<byte> writer) => WriteEd(writer, 3);

    /// <summary>Clear the entire line under the cursor (EL 2, <c>CSI 2 K</c>).</summary>
    public static void WriteClearLine(IBufferWriter<byte> writer) => WriteEl(writer, 2);

    /// <summary>Clear from the cursor to the end of the line (EL 0, <c>CSI 0 K</c>).</summary>
    public static void WriteClearLineAfter(IBufferWriter<byte> writer) => WriteEl(writer, 0);

    /// <summary>Clear from the start of the line to the cursor (EL 1, <c>CSI 1 K</c>).</summary>
    public static void WriteClearLineBefore(IBufferWriter<byte> writer) => WriteEl(writer, 1);

    /// <summary>
    /// Switch to the alternate screen buffer and save the cursor position (DECSET 1049). Pair
    /// with <see cref="WriteLeaveAlternateScreen"/> on shutdown so the user's prior shell
    /// contents reappear.
    /// </summary>
    public static void WriteEnterAlternateScreen(IBufferWriter<byte> writer)
        => WriteDecMode(writer, 1049, enable: true);

    /// <summary>Leave the alternate screen buffer and restore the cursor position (DECRST 1049).</summary>
    public static void WriteLeaveAlternateScreen(IBufferWriter<byte> writer)
        => WriteDecMode(writer, 1049, enable: false);

    /// <summary>
    /// Disable autowrap (DECRST 7 — DECAWM off). Writing past the rightmost column overwrites
    /// the rightmost cell instead of wrapping to the next row. Pair with
    /// <see cref="WriteEnableAutowrap"/> on shutdown so the user's prior shell behavior is
    /// restored.
    /// </summary>
    /// <remarks>
    /// Cell-grid renderers should disable autowrap during a session: with it on, writing the
    /// last column of a row puts the cursor into "deferred wrap" pending state. Most terminals
    /// clear that state cleanly on a CUP to the next row, but a few conhost / ConEmu families
    /// don't, and the resulting drift presents as the first few characters of subsequent rows
    /// silently shifting to the right edge ("Wide glyphs" rendering as "e glyphs" with "Wid"
    /// appearing at the right margin). Since cell-grid renderers always position via CUP and
    /// never rely on autowrap, turning it off costs nothing and eliminates the bug surface.
    /// </remarks>
    public static void WriteDisableAutowrap(IBufferWriter<byte> writer)
        => WriteDecMode(writer, 7, enable: false);

    /// <summary>Re-enable autowrap (DECSET 7 — DECAWM on). Restores the terminal default.</summary>
    public static void WriteEnableAutowrap(IBufferWriter<byte> writer)
        => WriteDecMode(writer, 7, enable: true);

    /// <summary>
    /// Configure the scroll region to the inclusive range [<paramref name="topRow"/>,
    /// <paramref name="bottomRow"/>] (0-based) via DECSTBM. After setting the region, the cursor
    /// is moved to row 0, column 0 of the new region per the standard's contract.
    /// </summary>
    public static void WriteScrollRegion(IBufferWriter<byte> writer, int topRow, int bottomRow)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var buffer = writer.GetSpan(16);
        int written = 0;
        buffer[written++] = Escape;
        buffer[written++] = CsiOpen;
        VtWriterUtilities.WriteAsciiInt(Math.Max(0, topRow) + 1, buffer, ref written);
        buffer[written++] = CsiSeparator;
        VtWriterUtilities.WriteAsciiInt(Math.Max(0, bottomRow) + 1, buffer, ref written);
        buffer[written++] = (byte) 'r';
        writer.Advance(written);
    }

    /// <summary>Reset the scroll region to the full viewport (DECSTBM with no parameters, <c>CSI r</c>).</summary>
    public static void WriteResetScrollRegion(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var span = writer.GetSpan(3);
        span[0] = Escape;
        span[1] = CsiOpen;
        span[2] = (byte) 'r';
        writer.Advance(3);
    }

    /// <summary>
    /// Scroll the active region up by <paramref name="lines"/> lines (<c>CSI Ps S</c>, SU).
    /// Top <paramref name="lines"/> rows scroll off; <paramref name="lines"/> blank rows appear
    /// at the bottom. Used by the diff renderer when it detects the back buffer equals the
    /// front buffer shifted up by N rows — emitting one SU is dramatically smaller than
    /// repainting every cell.
    /// </summary>
    public static void WriteScrollUp(IBufferWriter<byte> writer, int lines)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lines);
        WriteScrollFinal(writer, lines, (byte) 'S');
    }

    /// <summary>
    /// Scroll the active region down by <paramref name="lines"/> lines (<c>CSI Ps T</c>, SD).
    /// Bottom <paramref name="lines"/> rows scroll off; <paramref name="lines"/> blank rows
    /// appear at the top.
    /// </summary>
    public static void WriteScrollDown(IBufferWriter<byte> writer, int lines)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lines);
        WriteScrollFinal(writer, lines, (byte) 'T');
    }

    private static void WriteScrollFinal(IBufferWriter<byte> writer, int lines, byte final)
    {
        var buffer = writer.GetSpan(8);
        int written = 0;
        buffer[written++] = Escape;
        buffer[written++] = CsiOpen;
        VtWriterUtilities.WriteAsciiInt(lines, buffer, ref written);
        buffer[written++] = final;
        writer.Advance(written);
    }

    private static void WriteEd(IBufferWriter<byte> writer, byte digit) => WriteSingleDigitFinal(writer, digit, (byte) 'J');
    private static void WriteEl(IBufferWriter<byte> writer, byte digit) => WriteSingleDigitFinal(writer, digit, (byte) 'K');

    private static void WriteSingleDigitFinal(IBufferWriter<byte> writer, byte digit, byte final)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var span = writer.GetSpan(4);
        span[0] = Escape;
        span[1] = CsiOpen;
        span[2] = (byte) ('0' + digit);
        span[3] = final;
        writer.Advance(4);
    }

    private static void WriteDecMode(IBufferWriter<byte> writer, int mode, bool enable)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var buffer = writer.GetSpan(10);
        int written = 0;
        buffer[written++] = Escape;
        buffer[written++] = CsiOpen;
        buffer[written++] = (byte) '?';
        VtWriterUtilities.WriteAsciiInt(mode, buffer, ref written);
        buffer[written++] = enable ? (byte) 'h' : (byte) 'l';
        writer.Advance(written);
    }
}