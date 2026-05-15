namespace Cursorial.Rendering.Fonts;

/// <summary>
/// A single FIGlet glyph: <see cref="Height"/> lines of fixed width that together render one
/// codepoint. Each line is stored verbatim (with the font's hardblank still present); the
/// renderer substitutes spaces at paint time so the smushing algorithm can keep distinguishing
/// real whitespace from hardblanks until just before pixels hit the cell grid.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Starts"/> and <see cref="Ends"/> are precomputed per-line non-whitespace bounds.
/// They make the smushing algorithm O(height) per glyph pair instead of repeatedly scanning.
/// A line that is entirely whitespace has <see cref="Starts"/>[i] == line length and
/// <see cref="Ends"/>[i] == -1 — callers check <c>Ends &lt; 0</c> to detect that case.
/// </para>
/// </remarks>
public sealed class FigletGlyph
{
    private readonly string[] _lines;
    private readonly int[] _starts;
    private readonly int[] _ends;
    private readonly int _width;

    /// <summary>
    /// Construct a glyph from its <paramref name="codepoint"/> identity and <paramref name="lines"/>.
    /// The hardblank character is used only for whitespace classification — lines retain it.
    /// </summary>
    public FigletGlyph(uint codepoint, string[] lines, char hardblank)
    {
        ArgumentNullException.ThrowIfNull(lines);

        Codepoint = codepoint;
        _lines = lines;
        _starts = new int[lines.Length];
        _ends = new int[lines.Length];

        int maxWidth = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length > maxWidth) maxWidth = line.Length;

            // start = first non-space (hardblanks count as non-space — they're real ink).
            // Same as Consolonia, except we don't pre-replace hardblanks so the smushing
            // algorithm can still see them.
            int start = line.Length;
            for (int j = 0; j < line.Length; j++)
                if (line[j] != ' ') { start = j; break; }
            _starts[i] = start;

            int end = -1;
            for (int j = line.Length - 1; j >= 0; j--)
                if (line[j] != ' ') { end = j; break; }
            _ends[i] = end;
        }

        _width = maxWidth;
    }

    /// <summary>The Unicode codepoint this glyph renders.</summary>
    public uint Codepoint { get; }

    /// <summary>Width of the widest line in cells. Smushing reduces effective width at runtime.</summary>
    public int Width => _width;

    /// <summary>Height of the glyph in cells.</summary>
    public int Height => _lines.Length;

    /// <summary>Per-line raw text (hardblanks still present). Indexable by row 0..<see cref="Height"/>-1.</summary>
    public ReadOnlySpan<string> Lines => _lines;

    /// <summary>Per-line index of the first non-space character. Equal to line length for an all-space line.</summary>
    public ReadOnlySpan<int> Starts => _starts;

    /// <summary>Per-line index of the last non-space character. -1 for an all-space line.</summary>
    public ReadOnlySpan<int> Ends => _ends;
}
