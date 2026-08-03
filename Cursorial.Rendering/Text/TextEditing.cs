using Cursorial.Text;

namespace Cursorial.Rendering.Text;

/// <summary>
/// A grapheme-cluster layout of one line of text (design doc §12.7 / spec-controls "TextBox" model): maps
/// a char offset to its display column and back, snapping to cluster boundaries so the caret never lands
/// inside a multi-codepoint glyph and a wide cluster (CJK / emoji) counts as two columns. Built per use
/// over the current text — a single-line field is short, so the O(length) build is negligible and the
/// edit/render paths already re-raster (a caret move or keystroke is not the zero-allocation steady state).
/// </summary>
public readonly struct GraphemeLayout
{
    // Parallel arrays of cluster-start boundaries, length Count+1 with a trailing sentinel:
    // _charIndex[^1] == text length, _column[^1] == total display width.
    private readonly int[] _charIndex;
    private readonly int[] _column;

    private GraphemeLayout(int[] charIndex, int[] column)
    {
        _charIndex = charIndex;
        _column = column;
    }

    /// <summary>The total display width of the text, in columns.</summary>
    public int TotalColumns => _column[^1];

    /// <summary>The char length of the text.</summary>
    public int Length => _charIndex[^1];

    /// <summary>The number of grapheme clusters (display columns when each masks to a width-1 glyph).</summary>
    public int ClusterCount => _charIndex.Length - 1;

    /// <summary>The cluster ordinal (0-based) of the boundary at or before <paramref name="charIndex"/> — the
    /// display index when each cluster maps to one masked glyph (PasswordBox).</summary>
    public int ClusterIndexAtOrBefore(int charIndex) => BoundaryAtOrBefore(charIndex);

    /// <summary>The char index where cluster <paramref name="clusterIndex"/> starts (clamped to <see cref="Length"/>) —
    /// the inverse of <see cref="ClusterIndexAtOrBefore"/> for masked-display ↔ model index mapping.</summary>
    public int CharIndexOfCluster(int clusterIndex) => _charIndex[Math.Clamp(clusterIndex, 0, _charIndex.Length - 1)];

    /// <summary>Builds the layout for <paramref name="text"/> (null/empty ⇒ a single zero boundary).</summary>
    public static GraphemeLayout Build(string? text) => Build(text.AsSpan());

    /// <summary>Builds the layout with cluster columns measured through <paramref name="metrics"/>
    /// (proposal-glyph-runs Phase 2): a uniformly sized/FIGlet-sourced editor lays out — and maps
    /// caret columns — at the source's per-cluster cell advances. Cluster boundaries are
    /// unchanged, so caret atomicity (one stop per glyph, whatever its footprint) comes free.</summary>
    public static GraphemeLayout Build(string? text, Fonts.GlyphMetrics? metrics)
        => Build(text.AsSpan(), metrics);

    /// <summary>
    /// Builds the cluster-boundary layout of <paramref name="text"/> — the allocation-free entry point for a
    /// caller holding a slice (a substring, a cell span) that would otherwise pay a <c>ToString()</c> just to
    /// project boundaries. The <see cref="string"/> overload delegates here, so the two never diverge.
    /// </summary>
    public static GraphemeLayout Build(ReadOnlySpan<char> text) => Build(text, metrics: null);

    /// <inheritdoc cref="Build(string?, Fonts.GlyphMetrics?)"/>
    public static GraphemeLayout Build(ReadOnlySpan<char> text, Fonts.GlyphMetrics? metrics)
    {
        if (text.IsEmpty)
            return new GraphemeLayout([0], [0]);

        var charIndex = new List<int>(text.Length + 1) { 0 };
        var column = new List<int>(text.Length + 1) { 0 };
        var enumerator = text.GetGraphemeEnumerator();
        int ci = 0, col = 0;
        string prev = ""; // kerning context: an editor line paints as one piece, so junctions smush
        while (enumerator.MoveNext())
        {
            var cluster = enumerator.Current;
            ci += cluster.Length;
            col += metrics?.Advance(prev, cluster) ?? GraphemeWidth.ClusterWidth(cluster);
            if (metrics is not null) prev = cluster.ToString();
            charIndex.Add(ci);
            column.Add(col);
        }

        return new GraphemeLayout([.. charIndex], [.. column]);
    }

    /// <summary>The display column of the cluster boundary at or before <paramref name="charIndex"/>.</summary>
    public int ColumnOf(int charIndex) => _column[BoundaryAtOrBefore(charIndex)];

    /// <summary>Snaps <paramref name="charIndex"/> to the cluster boundary at or before it (clamped to [0, Length]).</summary>
    public int PinToBoundary(int charIndex) => _charIndex[BoundaryAtOrBefore(charIndex)];

    /// <summary>The next cluster boundary strictly after <paramref name="charIndex"/> (clamped to Length).</summary>
    public int NextBoundary(int charIndex)
    {
        foreach (var boundary in _charIndex)
        {
            if (boundary > charIndex)
                return boundary;
        }

        return Length;
    }

    /// <summary>The previous cluster boundary strictly before <paramref name="charIndex"/> (clamped to 0).</summary>
    public int PrevBoundary(int charIndex)
    {
        for (var i = _charIndex.Length - 1; i >= 0; i--)
        {
            if (_charIndex[i] < charIndex)
                return _charIndex[i];
        }

        return 0;
    }

    /// <summary>The char index of the first cluster boundary whose column is ≥ <paramref name="column"/> (clamped to Length).</summary>
    public int CharIndexAtOrAfterColumn(int column)
    {
        for (var i = 0; i < _column.Length; i++)
        {
            if (_column[i] >= column)
                return _charIndex[i];
        }

        return Length;
    }

    /// <summary>The char index of the cluster boundary at or before <paramref name="column"/> (the cluster covering it).</summary>
    public int CharIndexAtOrBeforeColumn(int column)
    {
        var best = 0;
        for (var i = 0; i < _column.Length && _column[i] <= column; i++)
            best = i;
        return _charIndex[best];
    }

    private int BoundaryAtOrBefore(int charIndex)
    {
        var best = 0;
        for (var i = 0; i < _charIndex.Length && _charIndex[i] <= charIndex; i++)
            best = i;
        return best;
    }
}

/// <summary>Whitespace-delimited word navigation for single-line editing (Ctrl+Arrow / Ctrl+Backspace/Delete).</summary>
public static class TextNavigation
{
    /// <summary>The offset after the next word: skip a leading whitespace run, then the word run (clamped to length).</summary>
    public static int NextWord(string text, int index) => NextWord(text.AsSpan(), index);

    /// <summary>The offset after the next word over a <paramref name="text"/> slice (the allocation-free entry point; the string overload delegates here).</summary>
    public static int NextWord(ReadOnlySpan<char> text, int index)
    {
        var i = Math.Clamp(index, 0, text.Length);
        while (i < text.Length && IsWordBoundary(text[i])) i++;
        while (i < text.Length && !IsWordBoundary(text[i])) i++;
        return i;
    }

    /// <summary>The offset at the start of the previous word: skip a trailing whitespace run, then the word run (clamped to 0).</summary>
    public static int PrevWord(string text, int index) => PrevWord(text.AsSpan(), index);

    /// <summary>The offset at the start of the previous word over a <paramref name="text"/> slice (the allocation-free entry point; the string overload delegates here).</summary>
    public static int PrevWord(ReadOnlySpan<char> text, int index)
    {
        var i = Math.Clamp(index, 0, text.Length);
        while (i > 0 && IsWordBoundary(text[i - 1])) i--;
        while (i > 0 && !IsWordBoundary(text[i - 1])) i--;
        return i;
    }

    private static bool IsWordBoundary(char c) => char.IsWhiteSpace(c) || c is ':' or '/' or '\\' or ';' or '|' or '.';
}
