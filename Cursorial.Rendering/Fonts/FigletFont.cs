using System.Globalization;

using Cursorial.Output;
using Cursorial.Text;

namespace Cursorial.Rendering.Fonts;

/// <summary>
/// A FIGlet-style ASCII-art font. Each input grapheme expands to a multi-line glyph pattern;
/// horizontal kerning and smushing rules (driven by the font's <see cref="LayoutMode"/>) can
/// pull adjacent glyphs together. Paints into a <see cref="CellBuffer"/> through the cell-grid
/// machinery, so wide-cell handling, the blending stack, and capability quantization all
/// continue to work for whatever fits inside the glyph's bounding rectangle.
/// </summary>
/// <remarks>
/// <para>
/// Load fonts via <see cref="FigletFontParser.LoadFromFile(string)"/> or
/// <see cref="FigletFontParser.Load(Stream, string)"/>. Cursorial bundles a small set of
/// public-domain FIGlet fonts under <see cref="FigletFonts"/> for the common cases.
/// </para>
/// <para>
/// <b>Smushing.</b> When <see cref="FigletLayoutMode.Smush"/> is set, adjacent glyph boundaries
/// can overlap by up to one cell when one of the rule flags (<see cref="FigletLayoutMode.Equal"/>,
/// <see cref="FigletLayoutMode.Lowline"/>, …) matches the boundary characters. Without the
/// <c>Smush</c> bit but with <see cref="FigletLayoutMode.Kern"/>, glyphs are slid together
/// until their non-whitespace columns touch but don't overlap. With neither, glyphs render at
/// full width.
/// </para>
/// <para>
/// <b>Unsupported glyphs.</b> Codepoints not defined by the font fall back to the font's
/// space glyph (a row of blanks). Customize the fallback behaviour by subclassing if you need
/// per-app fallback chains; for the typical case "missing glyph = blank gap" is the least
/// surprising default.
/// </para>
/// </remarks>
public sealed class FigletFont : IGlyphFont
{
    private const TextAttributes ForbiddenAttributes = TextAttributes.Italic |
                                                       TextAttributes.Underline |
                                                       TextAttributes.Inverse |
                                                       TextAttributes.Overline |
                                                       TextAttributes.Strikethrough;

    // ReSharper disable once ReplaceWithFieldKeyword
    private static readonly Style s_defaultStyle = default(Style) with { Background = Color.Transparent };

    public static ref readonly Style DefaultStyle => ref s_defaultStyle;

    private readonly Dictionary<uint, FigletGlyph> _glyphs;
    private readonly FigletGlyph _spaceGlyph;

    /// <summary>Construct a font from already-parsed glyphs. See <see cref="FigletFontParser"/>.</summary>
    public FigletFont(
        string name,
        char hardBlank,
        int height,
        FigletLayoutMode layoutMode,
        IReadOnlyDictionary<uint, FigletGlyph> glyphs)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(glyphs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Name = name;
        HardBlank = hardBlank;
        Height = height;
        LayoutMode = layoutMode;
        _glyphs = new Dictionary<uint, FigletGlyph>(glyphs);

        // Space (U+0020) must exist per the FIGlet spec; if a malformed font omits it, fabricate
        // a blank-row glyph rather than blowing up — missing-space is the most common author
        // mistake we see.
        if (!_glyphs.TryGetValue(0x0020, out var space))
        {
            var blankLines = new string[height];
            Array.Fill(blankLines, " ");
            space = new FigletGlyph(0x0020, blankLines, hardBlank);
            _glyphs[0x0020] = space;
        }

        _spaceGlyph = space;
    }

    /// <summary>Display name of the font (typically the file stem of the source <c>.flf</c>).</summary>
    public string Name { get; }

    /// <summary>The hardblank character — a non-space inside the glyph that displays as a space.</summary>
    public char HardBlank { get; }

    /// <summary>Glyph row count. Every glyph in the font has this many lines.</summary>
    public int Height { get; }

    /// <summary>Horizontal layout rules applied to adjacent glyphs.</summary>
    public FigletLayoutMode LayoutMode { get; }

    /// <summary>Number of glyphs defined in the font.</summary>
    public int GlyphCount => _glyphs.Count;

    /// <summary>True when a glyph is defined for <paramref name="codepoint"/>.</summary>
    public bool HasGlyph(uint codepoint) => _glyphs.ContainsKey(codepoint);

    /// <summary>Resolve a codepoint to its glyph, falling back to the space glyph when undefined.</summary>
    public FigletGlyph GetGlyph(uint codepoint)
        => _glyphs.GetValueOrDefault(codepoint, _spaceGlyph);

    /// <inheritdoc/>
    public Style EnsureCompatibleStyle(in Style style) 
        => style with { Attributes = style.Attributes & ~ForbiddenAttributes };

    /// <inheritdoc/>
    public Size Measure(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return Size.Empty;

        int total = 0;
        FigletGlyph? prev = null;

        foreach (uint cp in EnumerateCodepoints(text))
        {
            var glyph = GetGlyph(cp);

            if (prev is null)
            {
                total += glyph.Width;
            }
            else
            {
                int overlap = ComputeOverlap(prev, glyph);
                total += Math.Max(0, glyph.Width - overlap);
            }

            prev = glyph;
        }

        return new Size(total, Height);
    }

    /// <inheritdoc/>
    public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in Style style)
    {
        if (buffer.IsEmpty || text.IsEmpty) return Size.Empty;
        if (row < 0 || row >= buffer.Rows || column >= buffer.Columns) return Size.Empty;

        int caret = column;
        FigletGlyph? prev = null;

        var compatibleStyle = EnsureCompatibleStyle(style);

        foreach (uint cp in EnumerateCodepoints(text))
        {
            var glyph = GetGlyph(cp);
            int overlap = prev is null ? 0 : ComputeOverlap(prev, glyph);
            caret -= overlap;
            PaintGlyph(buffer, caret, row, glyph, compatibleStyle);
            caret += glyph.Width;
            prev = glyph;
        }

        int painted = Math.Max(0, caret - column);
        int height = Math.Min(Height, Math.Max(0, buffer.Rows - row));

        return new Size(painted, height);
    }

    private void PaintGlyph(in CellBufferView buffer, int column, int row, FigletGlyph glyph, in Style style)
    {
        var lines = glyph.Lines;

        for (int r = 0; r < lines.Length; r++)
        {
            int targetRow = row + r;
            if (targetRow < 0) continue;
            if (targetRow >= buffer.Rows) break;

            var line = lines[r];
            var targetCol = column;

            var e = line.GetGraphemeEnumerator();

            while (e.MoveNext())
            {
                var grapheme = e.GetCurrentGrapheme();
                var width = GraphemeWidth.StringWidth(grapheme);
                
                if (targetCol < 0) continue;
                if (targetCol >= buffer.Columns) break;

                char ch = grapheme[0];

                if (ch is ' ' or '\u00A0')
                {
                    targetCol += width;
                    continue;
                }

                string cluster;

                // Hardblanks render as visual spaces; pure spaces are transparent so previously
                // painted cells show through (this is what makes kerned/smushed boundaries
                // composite correctly with the underlying cell grid).
                if (ch == HardBlank) cluster = " ";
                else cluster = line.Length == grapheme.Length ? line : grapheme.ToString();

                // Smushing: if a previous glyph in this same Paint call already wrote a non-space
                // character into this cell (which happens when ComputeOverlap added the +1 smush
                // bonus), apply the FIGlet smush rule to merge the two characters rather than
                // letting the right glyph silently overwrite the left's edge. ComputeOverlap only
                // grants the +1 when TrySmush already returned true, so the merge is always
                // well-defined here. Falls back to plain overwrite if the existing cell holds a
                // character that doesn't pair with the new one (defensive \u2014 shouldn't fire under
                // a correct overlap computation).
                if (cluster != " " && cluster.Length == 1)
                {
                    var existing = buffer[targetCol, targetRow];
                    if (existing.Grapheme is { Length: > 0 } prev
                        && prev.Length == 1 && prev[0] is var prevCh
                        && prevCh != ' '
                        && TrySmush(prevCh, ch, out char smushed))
                    {
                        cluster = smushed.ToString();
                    }
                }

                buffer.Set(targetCol, targetRow, cluster, style);

                targetCol += width;
            }
        }
    }

    private static IEnumerable<uint> EnumerateCodepoints(ReadOnlySpan<char> text)
    {
        // We can't yield from a method taking a Span — materialize. FIGlet rendering already
        // allocates per-glyph buffers, so an enumerable string here is in the noise.
        var s = text.ToString();
        var iter = StringInfo.GetTextElementEnumerator(s);
        var result = new List<uint>(s.Length);

        while (iter.MoveNext())
        {
            var cluster = (string) iter.Current;
            // For each grapheme cluster, use the first codepoint as the glyph lookup key.
            // FIGlet fonts target single codepoints; combining marks and emoji clusters fall
            // back to the base character's glyph (or to space, if not defined).
            result.Add((uint) char.ConvertToUtf32(cluster, 0));
        }

        return result;
    }

    /// <summary>
    /// Compute how many columns the right glyph can move into the left glyph's territory under
    /// the current <see cref="LayoutMode"/>. Zero means "no overlap" (full-width spacing).
    /// </summary>
    internal int ComputeOverlap(FigletGlyph left, FigletGlyph right)
    {
        bool kern = LayoutMode.HasFlag(FigletLayoutMode.Kern);
        bool smush = LayoutMode.HasFlag(FigletLayoutMode.Smush);

        if (!kern && !smush) return 0;

        int minMove = int.MaxValue;
        int lines = Math.Max(left.Height, right.Height);

        for (int i = 0; i < lines; i++)
        {
            string leftLine = i < left.Lines.Length ? left.Lines[i] : string.Empty;
            string rightLine = i < right.Lines.Length ? right.Lines[i] : string.Empty;

            int leftEnd = i < left.Ends.Length ? left.Ends[i] : -1;
            int rightStart = i < right.Starts.Length ? right.Starts[i] : rightLine.Length;

            int spaceAfter = leftEnd < 0 ? leftLine.Length : leftLine.Length - leftEnd - 1;
            int spaceBefore = rightStart >= rightLine.Length ? rightLine.Length : rightStart;

            int move = spaceAfter + spaceBefore;

            // Smushing: when both boundary chars exist and the rule fires, we can move one
            // additional cell past the contact point — the boundary chars overlap into a single
            // resulting character.
            if (smush && leftEnd >= 0 && rightStart < rightLine.Length)
            {
                char leftBoundary = leftLine[leftEnd];
                char rightBoundary = rightLine[rightStart];

                if (TrySmush(leftBoundary, rightBoundary, out _))
                    move++;
            }

            // Can't move into territory the right glyph doesn't occupy.
            move = Math.Min(move, rightLine.Length);

            if (move < minMove)
                minMove = move;
        }

        return minMove == int.MaxValue ? 0 : Math.Max(0, minMove);
    }

    /// <summary>
    /// Apply the FIGlet smushing rules to two boundary characters. Returns true and the
    /// resulting character when the configured rules allow them to merge; false when they don't.
    /// Spaces never smush (they're handled by kerning instead).
    /// </summary>
    internal bool TrySmush(char left, char right, out char result)
    {
        result = '\0';

        if (left == ' ' || right == ' ')
            return false;

        // Rule 6 — hardblank with anything else resolves to the non-hardblank.
        if (left == HardBlank || right == HardBlank)
        {
            if (!LayoutMode.HasFlag(FigletLayoutMode.Hardblank)) return false;
            result = left == HardBlank ? right : left;
            return true;
        }

        // Rule 1 — identical characters smush into one.
        if (LayoutMode.HasFlag(FigletLayoutMode.Equal) && left == right)
        {
            result = left;
            return true;
        }

        // Rule 2 — underscore yields to one of "|/\[]{}()<>".
        if (LayoutMode.HasFlag(FigletLayoutMode.Lowline))
        {
            const string lowlineReplacers = "|/\\[]{}()<>";

            if (left == '_' && lowlineReplacers.Contains(right))
            {
                result = right;
                return true;
            }

            if (right == '_' && lowlineReplacers.Contains(left))
            {
                result = left;
                return true;
            }
        }

        // Rule 3 — hierarchy: characters earlier in the bracket family yield to later ones.
        if (LayoutMode.HasFlag(FigletLayoutMode.Hierarchy))
        {
            const string hierarchy = "|/\\[]{}()<>";

            int li = hierarchy.IndexOf(left);
            int ri = hierarchy.IndexOf(right);

            if (li >= 0 && ri >= 0)
            {
                result = li > ri ? left : right;
                return true;
            }
        }

        // Rule 4 — opposite-bracket pairs smush into '|'.
        if (LayoutMode.HasFlag(FigletLayoutMode.Pair))
        {
            if (left == '[' && right == ']' || left == ']' && right == '[' ||
                left == '{' && right == '}' || left == '}' && right == '{' ||
                left == '(' && right == ')' || left == ')' && right == '(')
            {
                result = '|';
                return true;
            }
        }

        // Rule 5 — /\ → |, \/ → Y, >< → X.
        if (LayoutMode.HasFlag(FigletLayoutMode.BigX))
        {
            if (left == '/' && right == '\\')
            {
                result = '|';
                return true;
            }

            if (left == '\\' && right == '/')
            {
                result = 'Y';
                return true;
            }

            if (left == '>' && right == '<')
            {
                result = 'X';
                return true;
            }
        }

        return false;
    }
}