using System.Buffers;

namespace Cursorial.Core.Output;

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
    private const byte CsiOpen = (byte)'[';
    private const byte CsiSeparator = (byte)';';

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
    /// Configure the scroll region to the inclusive range [<paramref name="topRow"/>,
    /// <paramref name="bottomRow"/>] (0-based) via DECSTBM. After setting the region the cursor
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
        buffer[written++] = (byte)'r';
        writer.Advance(written);
    }

    /// <summary>Reset the scroll region to the full viewport (DECSTBM with no parameters, <c>CSI r</c>).</summary>
    public static void WriteResetScrollRegion(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var span = writer.GetSpan(3);
        span[0] = Escape;
        span[1] = CsiOpen;
        span[2] = (byte)'r';
        writer.Advance(3);
    }

    private static void WriteEd(IBufferWriter<byte> writer, byte digit) => WriteSingleDigitFinal(writer, digit, (byte)'J');
    private static void WriteEl(IBufferWriter<byte> writer, byte digit) => WriteSingleDigitFinal(writer, digit, (byte)'K');

    private static void WriteSingleDigitFinal(IBufferWriter<byte> writer, byte digit, byte final)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var span = writer.GetSpan(4);
        span[0] = Escape;
        span[1] = CsiOpen;
        span[2] = (byte)('0' + digit);
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
        buffer[written++] = (byte)'?';
        VtWriterUtilities.WriteAsciiInt(mode, buffer, ref written);
        buffer[written++] = enable ? (byte)'h' : (byte)'l';
        writer.Advance(written);
    }
}
