using Cursorial.Text;

namespace Cursorial.UI.Controls;

/// <summary>
/// A multi-line, optionally word-wrapped grapheme layout of a <see cref="TextBox"/>'s text (the multi-line
/// generalization of <see cref="GraphemeLayout"/>, design doc §12.7). It splits the model text into <b>visual
/// lines</b> — first on hard breaks (<c>\n</c>, <c>\r\n</c>, <c>\r</c>), then, when <paramref name="wrap"/> is
/// on, further at the wrap width on grapheme-aware word boundaries — and maps a flat model char offset to its
/// visual <c>(line, column)</c> and back. Each visual line carries its own single-line <see cref="GraphemeLayout"/>
/// over its slice, so per-line column math reuses the tested single-line code.
/// </summary>
/// <remarks>
/// A single logical line that ends the text with a trailing hard break yields an extra empty visual line (so the
/// caret can sit on the blank line after a final <c>Enter</c>). The degenerate case — no breaks, wrap off — is one
/// visual line spanning the whole text, equivalent to a bare <see cref="GraphemeLayout"/>. Built per use like
/// <see cref="GraphemeLayout"/>; the edit/render paths already re-raster, so the O(length) build is not a hot path.
/// Boundary / word navigation (Left / Right / Ctrl+Arrow) stays on the flat <see cref="GraphemeLayout"/> — a hard
/// break is just a cluster boundary there — so this type owns only the line-structure operations.
/// </remarks>
internal readonly struct TextLayout
{
    private readonly Line[] _lines;
    private readonly int _maxWidth;

    private TextLayout(Line[] lines, int maxWidth)
    {
        _lines = lines;
        _maxWidth = maxWidth;
    }

    /// <summary>One visual line: a slice of the model text plus its single-line glyph layout.</summary>
    private readonly struct Line(int start, int length, bool hardBreak, GraphemeLayout glyphs)
    {
        /// <summary>Model char offset of the line's first character.</summary>
        public int Start { get; } = start;

        /// <summary>Char length of the line's content, excluding any terminating hard break.</summary>
        public int Length { get; } = length;

        /// <summary>Whether a hard line break (<c>\n</c> etc.) terminates the logical line at this visual line.</summary>
        public bool HardBreak { get; } = hardBreak;

        /// <summary>The single-line cluster layout over this line's slice (columns are line-local).</summary>
        public GraphemeLayout Glyphs { get; } = glyphs;
    }

    /// <summary>The number of visual lines (always ≥ 1).</summary>
    public int LineCount => _lines.Length;

    /// <summary>The widest visual line's display width, in columns.</summary>
    public int MaxWidth => _maxWidth;

    /// <summary>The display width of visual <paramref name="line"/>, in columns.</summary>
    public int LineWidth(int line) => _lines[ClampLine(line)].Glyphs.TotalColumns;

    /// <summary>The model char offset of visual <paramref name="line"/>'s first character (Home target).</summary>
    public int LineContentStart(int line) => _lines[ClampLine(line)].Start;

    /// <summary>
    /// The model char offset just past visual <paramref name="line"/>'s content — before any terminating hard
    /// break (End target). For a soft-wrapped (non-hard-break) line this is the next line's start.
    /// </summary>
    public int LineContentEnd(int line)
    {
        var l = _lines[ClampLine(line)];
        return l.Start + l.Length;
    }

    /// <summary>The single-line glyph layout of visual <paramref name="line"/> (columns are line-local).</summary>
    public GraphemeLayout LineGlyphs(int line) => _lines[ClampLine(line)].Glyphs;

    /// <summary>Maps a model char <paramref name="offset"/> to its visual <c>(line, column)</c>.</summary>
    public (int Line, int Column) Locate(int offset)
    {
        var line = LineOfOffset(offset);
        var column = _lines[line].Glyphs.ColumnOf(offset - _lines[line].Start);
        return (line, column);
    }

    /// <summary>
    /// Maps a visual <c>(line, column)</c> back to a model char offset, snapped to the cluster boundary at or
    /// before <paramref name="column"/> and clamped to the line's content (so it never lands on a hard break).
    /// </summary>
    public int OffsetAt(int line, int column)
    {
        line = ClampLine(line);
        var local = _lines[line].Glyphs.CharIndexAtOrBeforeColumn(Math.Max(0, column));
        return _lines[line].Start + local;
    }

    /// <summary>The visual line index containing model char <paramref name="offset"/> (clamped).</summary>
    public int LineOfOffset(int offset)
    {
        offset = Math.Clamp(offset, 0, Length);
        // The last line whose Start is ≤ offset. An offset sitting on a hard break (== a line's content end and
        // == the next line's Start) resolves to the NEXT line, matching a caret that has moved past the break.
        var best = 0;
        for (var i = 0; i < _lines.Length; i++)
        {
            if (_lines[i].Start <= offset)
                best = i;
            else
                break;
        }
        return best;
    }

    /// <summary>The model char length of the text (the trailing sentinel of the last line's slice).</summary>
    public int Length => _lines.Length == 0 ? 0 : _lines[^1].Start + _lines[^1].Length;

    private int ClampLine(int line) => Math.Clamp(line, 0, _lines.Length - 1);

    /// <summary>
    /// Builds the layout. <paramref name="wrapWidth"/> is the column budget for soft wrapping (the arranged
    /// content width); wrapping engages only when <paramref name="wrap"/> is <see langword="true"/> and the
    /// width is positive.
    /// </summary>
    public static TextLayout Build(string? text, int wrapWidth, bool wrap)
    {
        text ??= "";
        var lines = new List<Line>();
        var maxWidth = 0;
        var n = text.Length;
        var i = 0;

        while (true)
        {
            // Scan the logical line's content up to the next hard break (or end of text).
            var j = i;
            while (j < n && text[j] != '\n' && text[j] != '\r')
                j++;

            var hard = j < n;
            AppendLogicalLine(lines, ref maxWidth, text, i, j, hard, wrapWidth, wrap);

            if (j >= n)
                break;

            // Advance past the break (\r\n counts as one).
            i = text[j] == '\r' && j + 1 < n && text[j + 1] == '\n' ? j + 2 : j + 1;

            // A break that ends the text leaves a blank final visual line so the caret can sit on it.
            if (i == n)
            {
                AppendLogicalLine(lines, ref maxWidth, text, n, n, hard: false, wrapWidth, wrap);
                break;
            }
        }

        return new TextLayout([.. lines], maxWidth);
    }

    // Adds the visual line(s) for one logical line [start, contentEnd). With wrapping off it is a single visual
    // line; with wrapping on it is split at the wrap width on grapheme-aware word boundaries.
    private static void AppendLogicalLine(
        List<Line> lines, ref int maxWidth, string text, int start, int contentEnd, bool hard, int wrapWidth, bool wrap)
    {
        var slice = text[start..contentEnd];
        var glyphs = GraphemeLayout.Build(slice);

        if (!wrap || wrapWidth <= 0 || glyphs.TotalColumns <= wrapWidth)
        {
            lines.Add(new Line(start, slice.Length, hard, glyphs));
            maxWidth = Math.Max(maxWidth, glyphs.TotalColumns);
            return;
        }

        foreach (var (segStart, segLen) in WrapSegments(slice, wrapWidth))
        {
            var segSlice = slice.Substring(segStart, segLen);
            var segGlyphs = GraphemeLayout.Build(segSlice);
            // Only the final segment of the logical line carries the hard break.
            var isLast = segStart + segLen >= slice.Length;
            lines.Add(new Line(start + segStart, segLen, hard && isLast, segGlyphs));
            maxWidth = Math.Max(maxWidth, segGlyphs.TotalColumns);
        }
    }

    // Grapheme-aware word wrap of one logical line into [start, length) char segments, each ≤ wrapWidth columns
    // where possible. Breaks at the last whitespace-cluster boundary before the overflow; a single word wider
    // than the budget is hard-broken at the cluster that overflows (never producing a zero-length segment).
    private static List<(int Start, int Length)> WrapSegments(string slice, int wrapWidth)
    {
        // Built eagerly (not a yield iterator) — the grapheme enumerator is a ref struct and cannot cross a
        // yield boundary.
        var segments = new List<(int Start, int Length)>();
        var segStart = 0;
        var segCols = 0;
        var lastBreak = -1; // char offset (cluster boundary) of the most recent word-break opportunity in this segment
        var pos = 0;
        var enumerator = slice.GetGraphemeEnumerator();

        while (enumerator.MoveNext())
        {
            var cluster = enumerator.Current;
            var width = GraphemeWidth.ClusterWidth(cluster);

            if (segCols + width > wrapWidth && segCols > 0)
            {
                var breakAt = lastBreak > segStart ? lastBreak : pos; // word break, else hard-break the long word
                segments.Add((segStart, breakAt - segStart));
                segStart = breakAt;
                segCols = ColumnsBetween(slice, segStart, pos);
                lastBreak = -1;
            }

            segCols += width;
            pos += cluster.Length;

            // A break opportunity is the boundary AFTER a whitespace cluster (so trailing spaces stay on the line).
            if (cluster.Length > 0 && char.IsWhiteSpace(cluster[0]))
                lastBreak = pos;
        }

        segments.Add((segStart, slice.Length - segStart));
        return segments;
    }

    private static int ColumnsBetween(string slice, int from, int to)
    {
        var cols = 0;
        var span = slice.AsSpan(from, to - from);
        var enumerator = span.GetGraphemeEnumerator();
        while (enumerator.MoveNext())
            cols += GraphemeWidth.ClusterWidth(enumerator.Current);
        return cols;
    }
}
