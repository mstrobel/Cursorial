using System.Buffers;

namespace Cursorial.Output;

/// <summary>
/// Emits cursor-positioning, cursor-visibility, and cursor-shape escape sequences. Pure byte
/// writers — output goes into an <see cref="IBufferWriter{T}"/> so the same calls feed both
/// live terminal output and the diff renderer's scratch buffer.
/// </summary>
/// <remarks>
/// <para>
/// Positions are 1-based on the wire (the convention VT inherited from DEC's terminals) but
/// 0-based in the API surface — applications using cell coordinates throughout should not have
/// to mentally adjust for the +1. The writers translate at the boundary.
/// </para>
/// <para>
/// Movement deltas (<see cref="WriteMoveUp"/> et al.) accept a count and emit one CSI sequence;
/// a count of zero produces no bytes. Counts &lt; 0 are clamped to zero.
/// </para>
/// </remarks>
public static class CursorWriter
{
    private const byte Escape = 0x1B;
    private const byte CsiOpen = (byte) '[';
    private const byte CsiSeparator = (byte) ';';

    /// <summary>Move the cursor to the absolute 0-based <paramref name="row"/> / <paramref name="column"/> (CUP).</summary>
    public static void WriteMoveTo(IBufferWriter<byte> writer, int column, int row)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var buffer = writer.GetSpan(16);
        int written = 0;
        buffer[written++] = Escape;
        buffer[written++] = CsiOpen;
        VtWriterUtilities.WriteAsciiInt(Math.Max(0, row) + 1, buffer, ref written);
        buffer[written++] = CsiSeparator;
        VtWriterUtilities.WriteAsciiInt(Math.Max(0, column) + 1, buffer, ref written);
        buffer[written++] = (byte) 'H';
        writer.Advance(written);
    }

    /// <summary>Move the cursor to absolute 0-based column <paramref name="column"/> on the current row (CHA).</summary>
    public static void WriteColumnAbsolute(IBufferWriter<byte> writer, int column)
        => WriteSingleParamFinal(writer, Math.Max(0, column) + 1, (byte) 'G');

    /// <summary>Move the cursor to absolute 0-based row <paramref name="row"/> in the current column (VPA).</summary>
    public static void WriteRowAbsolute(IBufferWriter<byte> writer, int row)
        => WriteSingleParamFinal(writer, Math.Max(0, row) + 1, (byte) 'd');

    /// <summary>Move the cursor up <paramref name="cells"/> rows (CUU). Zero or negative is a no-op.</summary>
    public static void WriteMoveUp(IBufferWriter<byte> writer, int cells) => WriteRelativeMove(writer, cells, (byte) 'A');

    /// <summary>Move the cursor down <paramref name="cells"/> rows (CUD). Zero or negative is a no-op.</summary>
    public static void WriteMoveDown(IBufferWriter<byte> writer, int cells) => WriteRelativeMove(writer, cells, (byte) 'B');

    /// <summary>Move the cursor right <paramref name="cells"/> columns (CUF). Zero or negative is a no-op.</summary>
    public static void WriteMoveRight(IBufferWriter<byte> writer, int cells) => WriteRelativeMove(writer, cells, (byte) 'C');

    /// <summary>Move the cursor left <paramref name="cells"/> columns (CUB). Zero or negative is a no-op.</summary>
    public static void WriteMoveLeft(IBufferWriter<byte> writer, int cells) => WriteRelativeMove(writer, cells, (byte) 'D');

    /// <summary>Save the cursor position and SGR state (DECSC, <c>ESC 7</c>).</summary>
    public static void WriteSavePosition(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var span = writer.GetSpan(2);
        span[0] = Escape;
        span[1] = (byte) '7';
        writer.Advance(2);
    }

    /// <summary>Restore the cursor position and SGR state saved by <see cref="WriteSavePosition"/> (DECRC, <c>ESC 8</c>).</summary>
    public static void WriteRestorePosition(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var span = writer.GetSpan(2);
        span[0] = Escape;
        span[1] = (byte) '8';
        writer.Advance(2);
    }

    /// <summary>Hide the cursor (DECRST 25, <c>CSI ? 25 l</c>).</summary>
    public static void WriteHide(IBufferWriter<byte> writer) => WriteDecMode(writer, 25, enable: false);

    /// <summary>Show the cursor (DECSET 25, <c>CSI ? 25 h</c>).</summary>
    public static void WriteShow(IBufferWriter<byte> writer) => WriteDecMode(writer, 25, enable: true);

    /// <summary>Configure cursor shape via DECSCUSR (<c>CSI Ps SP q</c>).</summary>
    public static void WriteShape(IBufferWriter<byte> writer, CursorShape shape)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var buffer = writer.GetSpan(8);
        int written = 0;
        buffer[written++] = Escape;
        buffer[written++] = CsiOpen;
        VtWriterUtilities.WriteAsciiInt((uint) shape, buffer, ref written);
        buffer[written++] = (byte) ' ';
        buffer[written++] = (byte) 'q';
        writer.Advance(written);
    }

    /// <summary>
    /// Place Kitty extra cursors — beam shape — on every cell of the single-column rectangle at
    /// 0-based <paramref name="column"/> spanning rows <paramref name="topRow"/>..<paramref name="bottomRow"/>
    /// inclusive (<c>CSI &gt; 2 ; 4 : top : left : bottom : right SP q</c>, the multiple-cursors
    /// protocol's rectangle form; 1-based on the wire like every other cursor writer). Extra
    /// cursors share the main cursor's color / opacity / blink and are <b>screen-fixed</b> —
    /// re-emit or <see cref="WriteClearExtraCursors"/> as the band moves. Emit only on terminals
    /// with the negotiated <c>MultipleCursors</c> capability: others mis-parse the
    /// <c>&gt; … SP q</c> form (Apple Terminal prints the literal 'q').
    /// </summary>
    public static void WriteExtraCursorsBeam(IBufferWriter<byte> writer, int column, int topRow, int bottomRow)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var buffer = writer.GetSpan(40);
        int written = 0;
        buffer[written++] = Escape;
        buffer[written++] = CsiOpen;
        buffer[written++] = (byte) '>';
        buffer[written++] = (byte) '2'; // beam
        buffer[written++] = CsiSeparator;
        buffer[written++] = (byte) '4'; // rectangle coordinates: top : left : bottom : right
        buffer[written++] = (byte) ':';
        VtWriterUtilities.WriteAsciiInt(Math.Max(0, topRow) + 1, buffer, ref written);
        buffer[written++] = (byte) ':';
        VtWriterUtilities.WriteAsciiInt(Math.Max(0, column) + 1, buffer, ref written);
        buffer[written++] = (byte) ':';
        VtWriterUtilities.WriteAsciiInt(Math.Max(0, bottomRow) + 1, buffer, ref written);
        buffer[written++] = (byte) ':';
        VtWriterUtilities.WriteAsciiInt(Math.Max(0, column) + 1, buffer, ref written);
        buffer[written++] = (byte) ' ';
        buffer[written++] = (byte) 'q';
        writer.Advance(written);
    }

    /// <summary>
    /// Clear every Kitty extra cursor from the screen (<c>CSI &gt; 0;4 SP q</c> — shape 0 over the
    /// whole-screen rectangle; byte-identical to the negotiator's restore-time clear). Same
    /// capability gate as <see cref="WriteExtraCursorsBeam"/>.
    /// </summary>
    public static void WriteClearExtraCursors(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ReadOnlySpan<byte> bytes = "\x1b[>0;4 q"u8;
        var buffer = writer.GetSpan(bytes.Length);
        bytes.CopyTo(buffer);
        writer.Advance(bytes.Length);
    }

    private static void WriteRelativeMove(IBufferWriter<byte> writer, int cells, byte final)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (cells <= 0) return;
        WriteSingleParamFinal(writer, cells, final);
    }

    private static void WriteSingleParamFinal(IBufferWriter<byte> writer, int value, byte final)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var buffer = writer.GetSpan(8);
        int written = 0;
        buffer[written++] = Escape;
        buffer[written++] = CsiOpen;
        VtWriterUtilities.WriteAsciiInt(value, buffer, ref written);
        buffer[written++] = final;
        writer.Advance(written);
    }

    private static void WriteDecMode(IBufferWriter<byte> writer, int mode, bool enable)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var buffer = writer.GetSpan(8);
        int written = 0;
        buffer[written++] = Escape;
        buffer[written++] = CsiOpen;
        buffer[written++] = (byte) '?';
        VtWriterUtilities.WriteAsciiInt(mode, buffer, ref written);
        buffer[written++] = enable ? (byte) 'h' : (byte) 'l';
        writer.Advance(written);
    }
}