using System.ComponentModel;
using System.Globalization;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering.Media;
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
/// Load fonts via <see cref="FigletFontParser.LoadFromFile(string, string?)"/> or
/// <see cref="FigletFontParser.Load(Stream, string, Uri?, string?)"/>. Cursorial bundles a small set of
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
/// space glyph (a row of blanks). Customize the fallback behavior by subclassing if you need
/// per-app fallback chains; for the typical case "missing glyph = blank gap" is the least
/// surprising default.
/// </para>
/// </remarks>
[TypeConverter(typeof(FigletFontConverter))]
public sealed class FigletFont : IGlyphFont
{
    private const TextAttributes ForbiddenAttributes = TextAttributes.Italic |
                                                       TextAttributes.Underline |
                                                       TextAttributes.Overline |
                                                       TextAttributes.Strikethrough;

    // ReSharper disable once ReplaceWithFieldKeyword
    private static readonly CellStyle s_defaultStyle = default(CellStyle) with { Background = Color.Transparent };

    public static ref readonly CellStyle DefaultStyle => ref s_defaultStyle;

    private readonly Dictionary<uint, FigletGlyph> _glyphs;
    private readonly FigletGlyph _spaceGlyph;

    /// <summary>Construct a font from already-parsed glyphs. See <see cref="FigletFontParser"/>.</summary>
    /// <param name="name">Display name of the font (typically the source file's stem).</param>
    /// <param name="hardBlank">The hardblank character — a non-space inside a glyph that displays as a space.</param>
    /// <param name="height">Glyph row count; every glyph in the font has this many lines.</param>
    /// <param name="layoutMode">Horizontal layout (kerning / smushing) rules applied to adjacent glyphs.</param>
    /// <param name="glyphs">The parsed glyph table, keyed by codepoint.</param>
    /// <param name="sourceUri">The URI the font was loaded from, when it came from one.</param>
    /// <param name="baseline">
    /// The FLF header's baseline field: rows from the top of the glyph box THROUGH the baseline
    /// row — a COUNT, not a 0-based index (see <see cref="Baseline"/>). Omit (or pass
    /// <see langword="null"/>) for a face whose glyphs rest on their bottom row.
    /// </param>
    public FigletFont(
        string name,
        char hardBlank,
        int height,
        FigletLayoutMode layoutMode,
        IReadOnlyDictionary<uint, FigletGlyph> glyphs,
        Uri? sourceUri = null,
        int? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(glyphs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Name = name;
        HardBlank = hardBlank;
        Height = height;
        LayoutMode = layoutMode;
        SourceUri = sourceUri;

        // MALFORMED BASELINE: CLAMP, don't throw. The spec bounds the field at 1 ≤ baseline ≤
        // height; a file outside that range is still a perfectly renderable font, because the
        // baseline affects nothing but where a foreign-metric run (a fallback trim indicator)
        // sits BESIDE the glyphs. Refusing to load the whole face over a cosmetic metric would
        // trade a one-row misplacement for a hard failure — the same judgement the constructor
        // already makes two statements below, where a font missing its mandatory space glyph gets
        // a fabricated one instead of an exception.
        //
        // These are bundled resources, so "never throw on parse" is not the automatic answer it
        // is for user input — but the argument runs the same way, and stronger: our own faces are
        // pinned by tests that assert the parsed values (VerticalMetricsTests), so a clamp
        // cannot silently hide a regression in what we ship, while a throw WOULD punish a
        // third-party .flf the user is perfectly happy with.
        Baseline = Math.Clamp(baseline ?? height, 1, height);

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

    /// <summary>
    /// The FLF header's baseline: rows from the top of the glyph box down to and INCLUDING the
    /// row the glyph bodies rest on — a <b>COUNT, not a 0-based row index</b>. <c>standard.flf</c>
    /// declares <c>6 5</c>, so <see cref="Height"/> is 6, this is 5, and the baseline's 0-based
    /// row index is <c>Baseline - 1</c> = 4, with one descender row (index 5) underneath.
    /// Always within <c>[1, Height]</c> — a header outside the spec's range is clamped, not
    /// rejected (see the constructor).
    /// </summary>
    /// <remarks>
    /// Reading this as an index is an off-by-one that renders CORRECTLY on the one bundled face
    /// where <c>Height == Baseline</c> (ansi-shadow, 7/7) and wrong on every other one — hence
    /// the emphasis.
    /// </remarks>
    public int Baseline { get; }

    /// <summary>Rows above the baseline, counting the baseline row — identical to
    /// <see cref="Baseline"/> by definition. See <see cref="IGlyphFont.Ascender"/>.</summary>
    public int Ascender => Baseline;

    /// <summary>Rows below the baseline row: <c><see cref="Height"/> - <see cref="Baseline"/></c>.
    /// 1 for standard/small/slant/mini, 2 for big, 0 for ansi-shadow.</summary>
    public int Descender => Height - Baseline;

    /// <summary>Horizontal layout rules applied to adjacent glyphs.</summary>
    public FigletLayoutMode LayoutMode { get; }

    /// <summary>Number of glyphs defined in the font.</summary>
    public int GlyphCount => _glyphs.Count;

    /// <summary>The source URI from which this font was loaded, if available.</summary>
    public Uri? SourceUri { get; private init; }

    /// <summary>True when a glyph is defined for <paramref name="codepoint"/>. Undefined
    /// codepoints paint as a blank gap, so callers that can choose their characters check
    /// first — see <see cref="IGlyphFont.HasGlyph"/>.</summary>
    public bool HasGlyph(uint codepoint) => _glyphs.ContainsKey(codepoint);

    /// <summary>Resolve a codepoint to its glyph, falling back to the space glyph when undefined.</summary>
    public FigletGlyph GetGlyph(uint codepoint)
        => _glyphs.GetValueOrDefault(codepoint, _spaceGlyph);

    /// <inheritdoc/>
    public string DisplayName => Name;

    /// <inheritdoc/>
    public CellStyle EnsureCompatibleStyle(in CellStyle style)
        => style with { Attributes = style.Attributes & ~ForbiddenAttributes };

    private GlyphMetrics? _metrics;

    /// <inheritdoc/>
    public GlyphMetrics GetMetrics() => _metrics ??= new MeasuredGlyphMetrics(this);

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
    public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in PartialStyle style)
    {
        // This face is the reason the two modes exist: its glyphs are mostly holes, so a stamp shows
        // whatever it was painted over and a box does not. Both go through the same ink pass — the box
        // has already been filled by the time GlyphPaint hands the delta back, minus its background.
        var ink = GlyphPaint.Ink(buffer, column, row, Measure(text), style);

        // Compatibility applies to the FOLDED style, per cell: the delta is one way for an attribute this
        // face cannot render to arrive and the cell underneath is another, and the face's constraint is on
        // what it paints, not on what it was told.
        return PaintCore(buffer, column, row, text,
                         (_, _, backdrop) => EnsureCompatibleStyle(GlyphPaint.Over(backdrop, ink)));
    }

    /// <summary>
    /// Paint <paramref name="legacyBaseStyle"/> adjusted per painted cell by <paramref name="baseStyle"/> resolved
    /// against <paramref name="bounds"/> — so a gradient (or any position-dependent source) flows across the
    /// rendered glyphs rather than the whole headline taking one flat color, while the channels the brushed style
    /// says nothing about come from the base.
    /// </summary>
    /// <remarks>
    /// This face is the reason the per-cell overload exists at all: one CHARACTER covers many cells here, so
    /// resolving once per character (let alone once per run) would band the gradient at glyph boundaries.
    /// A uniform BrushedStyle cannot vary, though, so it takes the same single-style path as the flat overload —
    /// a saving the previous callback form could not even ask about.
    /// </remarks>
    public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text,
                      in CellStyle legacyBaseStyle, in BrushedStyle baseStyle, in Rect bounds)
    {
        // EnsureCompatibleStyle applies to the FOLDED style, not to the base alone: a brushed style is a second way
        // for an attribute this face cannot render to arrive, and the face's constraint is on what it paints.
        if (baseStyle.IsUniform)
        {
            var uniform = EnsureCompatibleStyle(baseStyle.Resolve(column, row, bounds).ApplyTo(legacyBaseStyle));
            return PaintCore(buffer, column, row, text, (_, _, _) => uniform);
        }

        // `in` parameters cannot be captured; the fold needs all three at every sample.
        var fallback = legacyBaseStyle;
        var baseStyleCopy = baseStyle;
        var box = bounds;

        return PaintCore(buffer, column, row, text,
                         (c, r, _) => EnsureCompatibleStyle(baseStyleCopy.Resolve(c, r, box).ApplyTo(fallback)));
    }

    // The resolved-style form both Paint overloads funnel into: by this point the BrushedStyle has been resolved
    // and folded, so what flows through here is a plain per-cell CellStyle lookup. The third argument is the
    // style the cell being painted already holds — the base the flat overload's delta folds onto, and unread
    // by the BrushedStyle path, whose base came from its caller.
    private Size PaintCore(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text,
                           Func<int, int, CellStyle, CellStyle> provider)
    {
        if (buffer.IsEmpty || text.IsEmpty) return Size.Empty;

        // The block's own rows run [row, row + Height); it is off-surface only when its FIRST row
        // already sits past the window's bottom (a block whose top rows are above the window still
        // has visible rows below). The edges are the view's LOCAL addressable window rather than
        // [0, Columns) × [0, Rows) — WithOrigin moves that window in local space, and this face's
        // anchor is documented as negative-capable.
        if (row >= buffer.LocalRowEnd || column >= buffer.LocalColumnEnd) return Size.Empty;

        int caret = column;
        FigletGlyph? prev = null;

        foreach (uint cp in EnumerateCodepoints(text))
        {
            var glyph = GetGlyph(cp);
            int overlap = prev is null ? 0 : ComputeOverlap(prev, glyph);
            caret -= overlap;
            PaintGlyph(buffer, caret, row, glyph, provider);
            caret += glyph.Width;
            prev = glyph;
        }

        int painted = Math.Max(0, caret - column);
        int height = Math.Min(Height, Math.Max(0, buffer.LocalRowEnd - row));

        return new Size(painted, height);
    }

    private void PaintGlyph(in CellBufferView buffer, int column, int row, FigletGlyph glyph, Func<int, int, CellStyle, CellStyle> style)
    {
        var lines = glyph.Lines;

        for (int r = 0; r < lines.Length; r++)
        {
            int targetRow = row + r;

            // Rows above the window are skipped, rows below end the glyph. Both edges are the view's
            // LOCAL addressable window: WithOrigin re-bases the origin away from the window, so the
            // unpaintable rows are at local coordinates outside [LocalRowStart, LocalRowEnd), which
            // on a re-based view is NOT [0, Rows).
            if (targetRow < buffer.LocalRowStart) continue;
            if (targetRow >= buffer.LocalRowEnd) break;

            var line = lines[r];
            var targetCol = column;

            var e = line.GetGraphemeEnumerator();

            while (e.MoveNext())
            {
                var grapheme = e.GetCurrentGrapheme();
                var width = GraphemeWidth.StringWidth(grapheme);

                if (targetCol >= buffer.LocalColumnEnd) break;

                // Left of the window: skip the cell but ADVANCE the caret. Skipping without advancing
                // pins the caret on the first clipped column, so every later grapheme in the line is
                // skipped too and the whole line is lost instead of its visible tail being painted.
                if (targetCol < buffer.LocalColumnStart)
                {
                    targetCol += width;
                    continue;
                }

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

                // The cell as it stands, read ONCE and used twice: as the smush look-back below, and
                // as the base the flat overload's delta folds onto (an absent channel means "keep
                // whatever is here").
                //
                // Read, not the indexer: the indexer VALIDATES and throws, and its contract is
                // "the caller has proven this is in range". The guards above prove the cell is
                // inside the window, but they used to be written against [0, Columns) — which is
                // the wrong interval on a re-based view, so a negative push translate turned this
                // look-back into an ArgumentOutOfRangeException thrown out of the render pass.
                // Read is the non-throwing form and yields a blank outside the window, which is
                // exactly the "nothing to smush with" answer — and, for the flat overload, the
                // "nothing underneath to inherit" one.
                var existing = buffer.Read(targetCol, targetRow);

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
                    if (existing.Grapheme is { Length: > 0 and 1 } prev &&
                        prev[0] is var prevCh &&
                        prevCh != ' ' &&
                        TrySmush(prevCh, ch, out char smushed))
                    {
                        cluster = smushed.ToString();
                    }
                }

                buffer.Set(targetCol, targetRow, cluster, style(targetCol, targetRow, existing.Style));

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

        // An entirely ink-free glyph is a deliberate word gap (FIGlet fonts that want interior
        // blanks to survive smushing protect them with HARDBLANKS, which classify as ink; a
        // plain-blank space glyph has no such protection). Without this guard both neighbors
        // slide through the blank glyph's full width (no ink to touch: leftEnd < 0 makes
        // spaceAfter the whole line) and word gaps vanish whenever a run paints as one piece —
        // words visually joined until a caret split happened to break the smush chain.
        if (left.IsBlank || right.IsBlank) return 0;

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
