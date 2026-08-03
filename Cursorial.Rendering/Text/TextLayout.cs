using Cursorial.Text;

namespace Cursorial.Rendering.Text;

/// <summary>
/// A multi-line, optionally word-wrapped grapheme layout of a text box's text (the multi-line generalization of
/// <see cref="GraphemeLayout"/>, design doc §12.7). It splits the model text into <b>visual lines</b> —
/// first on hard breaks (<c>\n</c>, <c>\r\n</c>, <c>\r</c>), then, when wrapping is on, further at the wrap width
/// on grapheme-aware word boundaries — and maps a flat model char offset to its visual <c>(line, column)</c>
/// and back. Each visual line carries its own single-line <see cref="GraphemeLayout"/> over its slice, so per-line
/// column math reuses the tested single-line code.
/// </summary>
/// <remarks>
/// A single logical line that ends the text with a trailing hard break yields an extra empty visual line (so the
/// caret can sit on the blank line after a final <c>Enter</c>). The degenerate case — no breaks, wrap off — is one
/// visual line spanning the whole text, equivalent to a bare <see cref="GraphemeLayout"/>. Built per use like
/// <see cref="GraphemeLayout"/>; the edit/render paths already re-raster, so the O(length) build is not a hot path.
/// Boundary / word navigation (Left / Right / Ctrl+Arrow) stays on the flat <see cref="GraphemeLayout"/> — a hard
/// break is just a cluster boundary there — so this type owns only the line-structure operations.
/// </remarks>
public readonly struct TextLayout
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
    public (int Line, int Column) Locate(int offset) => Locate(offset, preferLineEnd: false);

    /// <summary>
    /// Maps a model char <paramref name="offset"/> to its visual <c>(line, column)</c>, resolving the soft-wrap
    /// ambiguity via <paramref name="preferLineEnd"/>. A soft-wrap boundary offset is simultaneously the content
    /// end of one visual line and the start of the next (no break char separates them); the caret can sit at
    /// either visual position. With <paramref name="preferLineEnd"/> the offset resolves to the <b>earlier</b>
    /// line's visual end (the caret affinity left by Down/End); otherwise it resolves to the next line's start
    /// (the natural affinity of Right / Home / typing).
    /// </summary>
    public (int Line, int Column) Locate(int offset, bool preferLineEnd)
    {
        var line = LineOfOffset(offset, preferLineEnd);
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
    public int LineOfOffset(int offset) => LineOfOffset(offset, preferLineEnd: false);

    private int LineOfOffset(int offset, bool preferLineEnd)
    {
        offset = Math.Clamp(offset, 0, Length);
        // The last line whose Start is ≤ offset. An offset sitting on a break (== a line's content end and ==
        // the next line's Start) resolves to the NEXT line, matching a caret that has moved past the break.
        var best = 0;
        for (var i = 0; i < _lines.Length; i++)
        {
            if (_lines[i].Start <= offset)
                best = i;
            else
                break;
        }

        // Soft-wrap affinity: if the offset is the start of `best` AND the previous line is a soft wrap whose
        // content end coincides with it, a caret with end-affinity belongs to that earlier line's visual end
        // (a hard break leaves a gap, so its predecessor never coincides — only soft wraps alias here).
        if (preferLineEnd && best > 0 &&
            !_lines[best - 1].HardBreak &&
            _lines[best - 1].Start + _lines[best - 1].Length == offset && 
            _lines[best].Start == offset)
        {
            best--;
        }

        return best;
    }

    /// <summary>
    /// True when <paramref name="offset"/> is the content end of soft-wrapped visual <paramref name="line"/>
    /// and coincides with the next line's start — the one caret position that maps to two visual lines, where a
    /// caret should keep end-affinity so it renders at this line's visual end rather than the next line's start.
    /// </summary>
    public bool IsLineEndBoundary(int line, int offset)
    {
        line = ClampLine(line);
        return line < _lines.Length - 1 && 
               !_lines[line].HardBreak &&
               offset == LineContentEnd(line) &&
               offset > LineContentStart(line) &&
               _lines[line + 1].Start == offset;
    }

    /// <summary>The model char length of the text (the trailing sentinel of the last line's slice).</summary>
    public int Length => _lines.Length == 0 ? 0 : _lines[^1].Start + _lines[^1].Length;

    private int ClampLine(int line) => Math.Clamp(line, 0, _lines.Length - 1);

    /// <summary>
    /// Builds the layout. <paramref name="wrapWidth"/> is the column budget for soft wrapping (the arranged
    /// content width); wrapping engages only when <paramref name="wrap"/> is not <see cref="WrapMode.NoWrap"/>
    /// and the width is positive. <paramref name="wrap"/> selects how over-budget lines split:
    /// <see cref="WrapMode.WordWrap"/> breaks at whitespace (a single over-long word is broken mid-cluster),
    /// <see cref="WrapMode.WordWrapOverflow"/> keeps an over-long word whole (it overflows the right edge), and
    /// <see cref="WrapMode.CharacterWrap"/> breaks at the exact width with no word-boundary preference.
    /// </summary>
    public static TextLayout Build(string? text, int wrapWidth, WrapMode wrap)
        => Build(text, wrapWidth, wrap, metrics: null);

    /// <summary>Builds the layout with cluster columns measured through <paramref name="metrics"/>
    /// (proposal-glyph-runs Phase 2): a uniformly sized/FIGlet-sourced editor wraps and maps
    /// caret columns at the source's per-cluster cell advances. Null = the monospace identity.</summary>
    public static TextLayout Build(string? text, int wrapWidth, WrapMode wrap, Fonts.GlyphMetrics? metrics)
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
            AppendLogicalLine(lines, ref maxWidth, text, i, j, hard, wrapWidth, wrap, metrics);

            if (j >= n)
                break;

            // Advance past the break (\r\n counts as one).
            i = text[j] == '\r' && j + 1 < n && text[j + 1] == '\n' ? j + 2 : j + 1;

            // A break that ends the text leaves a blank final visual line so the caret can sit on it.
            if (i == n)
            {
                AppendLogicalLine(lines, ref maxWidth, text, n, n, hard: false, wrapWidth, wrap, metrics);
                break;
            }
        }

        return new TextLayout([.. lines], maxWidth);
    }

    // Adds the visual line(s) for one logical line [start, contentEnd). With wrapping off it is a single visual
    // line; with wrapping on it is split at the wrap width per the WrapMode (grapheme-aware throughout).
    private static void AppendLogicalLine(List<Line> lines, ref int maxWidth, string text, int start, int contentEnd,
                                          bool hard, int wrapWidth, WrapMode wrap, Fonts.GlyphMetrics? metrics)
    {
        var slice = text[start..contentEnd];
        var glyphs = GraphemeLayout.Build(slice, metrics);

        if (wrap == WrapMode.NoWrap || wrapWidth <= 0 || glyphs.TotalColumns <= wrapWidth)
        {
            lines.Add(new Line(start, slice.Length, hard, glyphs));
            maxWidth = Math.Max(maxWidth, glyphs.TotalColumns);
            return;
        }

        foreach (var (segStart, segLen) in WrapSegments(slice, wrapWidth, wrap, metrics))
        {
            var segSlice = slice.Substring(segStart, segLen);
            var segGlyphs = GraphemeLayout.Build(segSlice, metrics);
            // Only the final segment of the logical line carries the hard break.
            var isLast = segStart + segLen >= slice.Length;
            lines.Add(new Line(start + segStart, segLen, hard && isLast, segGlyphs));
            maxWidth = Math.Max(maxWidth, segGlyphs.TotalColumns);
        }
    }

    // Grapheme-aware wrap of one logical line into [start, length) char segments per the WrapMode. WordWrap and
    // WordWrapOverflow break at the last whitespace-cluster boundary before the overflow; they differ only for a
    // single word wider than the budget — WordWrap hard-breaks it at the overflowing cluster, WordWrapOverflow
    // keeps it whole (the segment overflows the right edge, matching WPF WrapWithOverflow). CharacterWrap breaks
    // at the exact width with no word-boundary preference. No mode ever produces a zero-length segment.
    private static List<(int Start, int Length)> WrapSegments(string slice, int wrapWidth, WrapMode wrap,
                                                              Fonts.GlyphMetrics? metrics)
    {
        // Built eagerly (not a yield iterator) — the grapheme enumerator is a ref struct and cannot cross a
        // yield boundary.
        var segments = new List<(int Start, int Length)>();
        var segStart = 0;
        var segCols = 0;
        var lastBreak = -1; // char offset (cluster boundary) of the most recent word-break opportunity in this segment
        var pos = 0;
        string prev = ""; // kerning context — reset when a wrap starts a new painted segment
        var enumerator = slice.GetGraphemeEnumerator();

        while (enumerator.MoveNext())
        {
            var cluster = enumerator.Current;
            var width = metrics?.Advance(prev, cluster) ?? GraphemeWidth.ClusterWidth(cluster);
            if (metrics is not null) prev = cluster.ToString();

            if (segCols + width > wrapWidth && segCols > 0)
            {
                // CharacterWrap ignores word boundaries; the word modes prefer the last whitespace. With no word
                // boundary available, WordWrap hard-breaks at the overflow while WordWrapOverflow lets the word
                // run on (breakAt < 0 ⇒ no break this cluster — the segment overflows until the next boundary).
                var breakAt = wrap == WrapMode.CharacterWrap
                                  ? pos
                                  : lastBreak > segStart
                                      ? lastBreak
                                      : wrap == WrapMode.WordWrapOverflow
                                          ? -1
                                          : pos;

                if (breakAt >= 0)
                {
                    segments.Add((segStart, breakAt - segStart));
                    segStart = breakAt;
                    segCols = ColumnsBetween(slice, segStart, pos, metrics);
                    lastBreak = -1;

                    if (metrics is not null)
                    {
                        // The wrap starts a fresh painted segment: when the current cluster LEADS
                        // it there is no junction to smush — re-measure context-free; otherwise
                        // its kerning context (the preceding cluster) moved into the new segment
                        // with it and the computed advance still holds.
                        if (segStart == pos)
                            width = metrics.ClusterWidth(cluster);
                    }
                }
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

    private static int ColumnsBetween(string slice, int from, int to, Fonts.GlyphMetrics? metrics = null)
    {
        var cols = 0;
        string prev = ""; // fresh piece — kerning accumulates from its own start
        var span = slice.AsSpan(from, to - from);
        var enumerator = span.GetGraphemeEnumerator();
        while (enumerator.MoveNext())
        {
            var cluster = enumerator.Current;
            cols += metrics?.Advance(prev, cluster) ?? GraphemeWidth.ClusterWidth(cluster);
            if (metrics is not null) prev = cluster.ToString();
        }

        return cols;
    }
}
