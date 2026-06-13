namespace Cursorial.UI.Xaml;

/// <summary>
/// Packs a 1-based (line, column) source position into a single <see cref="int"/> — line in the low
/// 20 bits (clamped to ~1M lines), column in the high 12 bits (clamped to 4095). Every record carries
/// one; the diagnostic contract (matrix XD1) requires both to be non-zero for a real position.
/// </summary>
internal static class LineInfo
{
    private const int LineBits = 20;
    private const int LineMask = (1 << LineBits) - 1;       // low 20 bits
    private const int ColumnMaxValue = (1 << 12) - 1;       // 4095

    /// <summary>Packs a 1-based line + column. Both are clamped into range.</summary>
    public static int Pack(int line, int column)
    {
        if (line < 0) line = 0;
        if (column < 0) column = 0;
        if (line > LineMask) line = LineMask;
        if (column > ColumnMaxValue) column = ColumnMaxValue;
        return line | (column << LineBits);
    }

    /// <summary>The packed line (1-based).</summary>
    public static int Line(int packed) => packed & LineMask;

    /// <summary>The packed column (1-based).</summary>
    public static int Column(int packed) => (int)((uint)packed >> LineBits);
}
