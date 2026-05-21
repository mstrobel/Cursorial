using System.Collections.Immutable;
using System.Text;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Text;

namespace Cursorial.Rendering.Text;

/// <summary>
/// Lays out a <see cref="RichText"/> document against a column budget and (optionally) a row
/// budget, producing an immutable <see cref="FormattedText"/> result. One formatter instance
/// is reusable across many <see cref="Format"/> calls and is safe to share across threads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 2 scope.</b> This iteration handles <see cref="TextParagraph"/> blocks fully —
/// wrap modes, soft hyphens, trimming, alignment (including <see cref="TextAlignment.Justify"/>),
/// and <see cref="TextParagraph.MaxLines"/>. Other block types (<see cref="HorizontalRule"/>,
/// <see cref="FigletBlock"/>, <see cref="SizedTextBlock"/>, <see cref="BlockContent"/>) and
/// <see cref="InlineContent"/> arrive in Phase 3. Encountering an unsupported block / inline
/// throws <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
public sealed class TextFormatter
{
    private const char SoftHyphen = '­';

    /// <summary>Default wrap mode when a paragraph doesn't specify its own.</summary>
    public WrapMode Wrap { get; init; } = WrapMode.WordWrap;

    /// <summary>Default trimming when a paragraph doesn't specify its own; also used for document-level (MaxRows) trim.</summary>
    public TextTrimming Trim { get; init; } = TextTrimming.None;

    /// <summary>Default horizontal alignment when a paragraph doesn't specify its own.</summary>
    public TextAlignment Alignment { get; init; } = TextAlignment.Left;

    /// <summary>Ellipsis appended by <see cref="TextTrimming.CharacterEllipsis"/> and <see cref="TextTrimming.WordEllipsis"/>.</summary>
    public string Ellipsis { get; init; } = "…";

    /// <summary>Cells per tab character. Tabs expand to spaces of the supplied style at format time.</summary>
    public int TabWidth { get; init; } = 4;

    /// <summary>
    /// Format <paramref name="text"/> against the supplied column budget. The optional
    /// <paramref name="maxRows"/> applies a document-level row cap; when content would exceed
    /// it the formatter's <see cref="Trim"/> rule is applied to the final visible line.
    /// </summary>
    public FormattedText Format(
        RichText text,
        int availableColumns,
        int? maxRows = null,
        OutputCapabilities? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (availableColumns <= 0)
            throw new ArgumentOutOfRangeException(nameof(availableColumns), availableColumns, "Available columns must be positive.");
        if (maxRows is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRows), maxRows, "MaxRows must be positive when supplied.");

        if (text.IsEmpty) return FormattedText.Empty;

        var caps = capabilities ?? OutputCapabilities.None;
        var formattedBlocks = ImmutableArray.CreateBuilder<FormattedBlock>();
        int totalRows = 0;
        int widthUsed = 0;
        bool first = true;
        Margins lastBlockMargins = Margins.Zero;

        foreach (var block in text.Blocks)
        {
            int marginTop = first ? 0 : Math.Max(block.Margin.Top, lastBlockMargins.Bottom);
            int rowsBeforeBlock = totalRows + marginTop;
            int budget = maxRows is { } cap ? cap - rowsBeforeBlock : int.MaxValue;
            if (budget <= 0) break;

            FormattedBlock formatted = FormatBlock(block, availableColumns, caps);
            // Carry margin onto the formatted block so the painter knows the stacking gap.
            formatted = formatted with { Margin = block.Margin };

            if (formatted is FormattedParagraph p && p.Lines.Length > budget)
            {
                formatted = ApplyDocumentRowCap(p, budget, availableColumns) with { Margin = block.Margin };
                totalRows = rowsBeforeBlock + formatted.Size.Rows;
                widthUsed = Math.Max(widthUsed, formatted.Size.Columns);
                formattedBlocks.Add(formatted);
                break;
            }

            // Non-paragraph blocks that don't fit the remaining budget: drop them entirely (no
            // partial render). They take effect only if at least one row of them fits AND the
            // budget can absorb the whole block.
            if (formatted.Size.Rows > budget)
                break;

            formattedBlocks.Add(formatted);
            totalRows = rowsBeforeBlock + formatted.Size.Rows;
            widthUsed = Math.Max(widthUsed, formatted.Size.Columns);
            first = false;
            lastBlockMargins = block.Margin;
        }

        return new FormattedText(formattedBlocks.ToImmutable(), new Size(widthUsed, totalRows));
    }

    private FormattedBlock FormatBlock(Block block, int availableColumns, OutputCapabilities capabilities) =>
        block switch
        {
            TextParagraph p  => FormatParagraph(p, availableColumns),
            HorizontalRule r => FormatHorizontalRule(r, availableColumns),
            FigletBlock f    => FormatFigletBlock(f, availableColumns),
            SizedTextBlock s => FormatSizedTextBlock(s, availableColumns, capabilities),
            BlockContent c   => FormatBlockContent(c, availableColumns, capabilities),
            _                => throw new NotSupportedException($"Block type {block.GetType().Name} is not supported by TextFormatter.")
        };

    private static FormattedHorizontalRule FormatHorizontalRule(HorizontalRule rule, int availableColumns)
    {
        // A horizontal rule always fills its column budget — alignment is meaningful only when
        // the caller is rendering at a width smaller than the document budget, which we don't
        // express here. Size is (columns, 1).
        if (string.IsNullOrEmpty(rule.Glyph))
            throw new InvalidOperationException("HorizontalRule.Glyph must be non-empty.");

        return new FormattedHorizontalRule(rule.Glyph, rule.Style, rule.Alignment, new Size(availableColumns, 1));
    }

    private static FormattedFigletBlock FormatFigletBlock(FigletBlock block, int availableColumns)
    {
        var measured = block.Face.Measure(block.Text);
        // Clip to the column budget; rows are whatever the face produces.
        int columns = Math.Min(measured.Columns, availableColumns);
        return new FormattedFigletBlock(block.Text, block.Face, block.Style, block.Alignment,
                                        new Size(columns, measured.Rows));
    }

    private static FormattedSizedTextBlock FormatSizedTextBlock(
        SizedTextBlock block, int availableColumns, OutputCapabilities capabilities)
    {
        // Use ScaledText to compute the realized footprint — it already encodes the
        // "OSC 66 if supported, otherwise font fallback" logic and the bundled-font selection
        // when no explicit fallback is given.
        var scaled = new Content.ScaledText(block.Text, block.Sizing, block.Fallback);
        var measured = scaled.Measure(new Size(availableColumns, int.MaxValue), capabilities);
        return new FormattedSizedTextBlock(
            block.Text, block.Sizing, block.Style, block.Fallback, block.Alignment, measured);
    }

    private static FormattedContentBlock FormatBlockContent(
        BlockContent block, int availableColumns, OutputCapabilities capabilities)
    {
        var measured = block.Content.Measure(new Size(availableColumns, int.MaxValue), capabilities);
        // Clip horizontally to the column budget; let content drive its own row count.
        var size = new Size(Math.Min(measured.Columns, availableColumns), measured.Rows);
        return new FormattedContentBlock(block.Content, block.Alignment, size);
    }

    private FormattedParagraph FormatParagraph(TextParagraph paragraph, int availableColumns)
    {
        // 1. Decompose inlines into wrap atoms with applied glyph maps and soft-hyphen markers.
        var atoms = new Tokenizer(this).Run(paragraph.Inlines);

        // 2. Greedy line packing per WrapMode.
        var lines = PackLines(atoms, availableColumns, paragraph.Wrap);

        // 3. Apply per-paragraph MaxLines cap (with trim if content was dropped), then per-line
        //    trim for over-width lines (NoWrap or WordWrapOverflow can produce these).
        bool droppedByMaxLines = paragraph.MaxLines is { } maxLines && lines.Count > maxLines;
        if (droppedByMaxLines)
        {
            lines.RemoveRange(paragraph.MaxLines!.Value, lines.Count - paragraph.MaxLines!.Value);
            lines[^1] = TrimLine(lines[^1], availableColumns, paragraph.Trim, forceEllipsis: true);
        }

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Width > availableColumns)
                lines[i] = TrimLine(lines[i], availableColumns, paragraph.Trim, forceEllipsis: false);
        }

        // 4. Alignment converts LineDraft → FormattedLine.
        var aligned = ImmutableArray.CreateBuilder<FormattedLine>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            bool isLastLine = i == lines.Count - 1;
            bool endedByHardBreak = lines[i].EndedByHardBreak;
            aligned.Add(ApplyAlignment(lines[i], availableColumns, paragraph.Alignment, isLastLine || endedByHardBreak));
        }

        int width = aligned.Count == 0 ? 0 : aligned.Max(l => l.Columns);
        return new FormattedParagraph(aligned.ToImmutable(), new Size(width, aligned.Count));
    }

    // ---- Tokenization ----

    /// <summary>
    /// Walks inlines, applies each <see cref="TextRun"/>'s <see cref="IGlyphMap"/>, and emits a
    /// flat list of <see cref="Atom"/>s consumable by the line packer. Held as a class so the
    /// in-progress word buffer can be mutated without ref-parameter closure pain.
    /// </summary>
    private sealed class Tokenizer(TextFormatter outer)
    {
        private readonly List<Atom> _atoms = [];
        private readonly List<FormattedRun> _wordRuns = [];
        private readonly List<SoftBreakPoint> _softBreaks = [];
        private int _wordWidth;

        public List<Atom> Run(ImmutableArray<Inline> inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case TextRun run:
                        EmitRun(run);
                        break;
                    case LineBreak:
                        FlushWord();
                        _atoms.Add(HardBreakAtom.Instance);
                        break;
                    case InlineContent:
                        throw new NotSupportedException(
                            "InlineContent isn't handled by TextFormatter yet. Phase 2 covers " +
                            "TextRun + LineBreak; InlineContent arrives in Phase 3.");
                    default:
                        throw new NotSupportedException(
                            $"Inline type {inline.GetType().Name} is not handled by TextFormatter.");
                }
            }

            FlushWord();
            return _atoms;
        }

        private void EmitRun(TextRun run)
        {
            var fragmentBuilder = new StringBuilder();
            int fragmentWidth = 0;

            void EmitFragment()
            {
                if (fragmentBuilder.Length == 0) return;
                _wordRuns.Add(new FormattedRun(fragmentBuilder.ToString(), run.Style, run.Hyperlink));
                _wordWidth += fragmentWidth;
                fragmentBuilder.Clear();
                fragmentWidth = 0;
            }

            var inputEnumerator = run.Text.GetGraphemeEnumerator();
            while (inputEnumerator.MoveNext())
            {
                ReadOnlySpan<char> input = inputEnumerator.Current;
                string mapped = run.Map is {} m ? m.Map(input) : input.ToString();

                // StringInfo.GetTextElementEnumerator clusters soft hyphens with the preceding
                // grapheme, masking them. Split mapped on U+00AD at the char level first so
                // each chunk can be enumerated normally and soft hyphens are recorded explicitly.
                int chunkStart = 0;
                for (int idx = 0; idx <= mapped.Length; idx++)
                {
                    bool isSoftHyphen = idx < mapped.Length && mapped[idx] == SoftHyphen;
                    bool atEnd = idx == mapped.Length;
                    if (!isSoftHyphen && !atEnd) continue;

                    if (idx > chunkStart)
                        ProcessChunk(mapped, chunkStart, idx);

                    if (isSoftHyphen)
                    {
                        EmitFragment();
                        _softBreaks.Add(new SoftBreakPoint(
                            FragmentIndex: _wordRuns.Count,
                            WidthBefore: _wordWidth,
                            Style: run.Style,
                            Hyperlink: run.Hyperlink));
                    }

                    chunkStart = idx + 1;
                }
            }

            EmitFragment();

            void ProcessChunk(string source, int start, int endExclusive)
            {
                string chunk = source[start..endExclusive];
                var en = chunk.GetGraphemeEnumerator();
                while (en.MoveNext())
                {
                    ReadOnlySpan<char> g = en.Current;

                    if (g.Length == 1 && g[0] == '\t')
                    {
                        EmitFragment();
                        FlushWord();
                        _atoms.Add(new SpaceAtom(
                            new FormattedRun(new string(' ', outer.TabWidth), run.Style, run.Hyperlink),
                            outer.TabWidth));
                        continue;
                    }

                    if (IsBreakingWhitespace(g))
                    {
                        EmitFragment();
                        FlushWord();
                        int spaceWidth = GraphemeWidth.ClusterWidth(g);
                        _atoms.Add(new SpaceAtom(
                            new FormattedRun(g.ToString(), run.Style, run.Hyperlink),
                            spaceWidth));
                        continue;
                    }

                    fragmentBuilder.Append(g);
                    fragmentWidth += GraphemeWidth.ClusterWidth(g);
                }
            }
        }

        private void FlushWord()
        {
            if (_wordRuns.Count == 0)
            {
                // A word position that captured only soft-hyphens with no visible content —
                // drop the markers; they have nothing to anchor to.
                _softBreaks.Clear();
                return;
            }

            _atoms.Add(new WordAtom(
                [.._wordRuns],
                _wordWidth,
                [.._softBreaks]));
            _wordRuns.Clear();
            _softBreaks.Clear();
            _wordWidth = 0;
        }
    }

    private static bool IsBreakingWhitespace(ReadOnlySpan<char> grapheme)
    {
        if (grapheme.Length != 1) return false;
        char c = grapheme[0];
        // ASCII space + common Unicode spaces. \r and \n are NOT here — LineBreak is the channel
        // for hard breaks; literal \n in run text is treated as a word character.
        return c is ' ' or ' ' or ' ' or ' ' or ' ';
    }

    // ---- Line packing ----

    private static List<LineDraft> PackLines(List<Atom> atoms, int columns, WrapMode mode)
    {
        var lines = new List<LineDraft>();
        var current = new LineDraft();

        // ReSharper disable AccessToModifiedClosure
        void Emit(bool hardBreak)
        {
            current.EndedByHardBreak = hardBreak;
            lines.Add(current);
            current = new LineDraft();
        }
        // ReSharper restore AccessToModifiedClosure

        int i = 0;
        while (i < atoms.Count)
        {
            var atom = atoms[i];

            switch (atom)
            {
                case HardBreakAtom:
                    Emit(true);
                    i++;
                    break;

                case SpaceAtom space:
                    if (current.Width == 0)
                    {
                        // Leading whitespace dropped (except NoWrap, which keeps everything).
                        if (mode == WrapMode.NoWrap)
                            current.AppendSpace(space);
                        i++;
                        break;
                    }

                    if (mode == WrapMode.NoWrap || current.Width + space.Width <= columns)
                    {
                        current.AppendSpace(space);
                        i++;
                    }
                    else
                    {
                        // Whitespace is the natural break point — consume it as the break and emit.
                        Emit(false);
                        i++;
                    }

                    break;

                case WordAtom word:
                    PlaceWord(word, ref current, columns, mode, Emit);
                    i++;
                    break;
            }
        }

        if (current.Width > 0 || lines.Count == 0)
            lines.Add(current);

        return lines;
    }

    private static void PlaceWord(
        WordAtom word, ref LineDraft current, int columns, WrapMode mode, Action<bool> emit)
    {
        // Easy case: fits as-is, or we're in NoWrap and never wrap.
        if (mode == WrapMode.NoWrap || current.Width + word.Width <= columns)
        {
            current.AppendWord(word);
            return;
        }

        if (current.Width == 0)
        {
            // Word wider than the budget on an empty line. WordWrapOverflow lets it overflow;
            // WordWrap and CharacterWrap split — WordWrap prefers soft hyphens when available,
            // then falls back to a character-boundary split.
            if (mode == WrapMode.WordWrapOverflow)
            {
                current.AppendWord(word);
                return;
            }

            if (mode == WrapMode.WordWrap &&
                TrySplitAtSoftBreak(word, columns, out var first, out var rest))
            {
                current.AppendWord(first);
                emit(false);
                // Rest may still be too long — recurse.
                PlaceWord(rest, ref current, columns, mode, emit);
                return;
            }

            var (head, tail) = SplitWordAtChar(word, columns);
            if (head.Width > 0) current.AppendWord(head);
            if (tail.Width > 0)
            {
                emit(false);
                PlaceWord(tail, ref current, columns, mode, emit);
            }
            return;
        }

        // Current line has content; the word doesn't fit.
        int remaining = columns - current.Width;

        switch (mode)
        {
            case WrapMode.WordWrap:
            case WrapMode.WordWrapOverflow:
                {
                    if (TrySplitAtSoftBreak(word, remaining, out var first, out var rest))
                    {
                        current.AppendWord(first);
                        emit(false);
                        PlaceWord(rest, ref current, columns, mode, emit);
                        return;
                    }

                    current.TrimTrailingSpaces();
                    emit(false);
                    PlaceWord(word, ref current, columns, mode, emit);
                    return;
                }

            case WrapMode.CharacterWrap:
                {
                    var (head, tail) = SplitWordAtChar(word, remaining);
                    if (head.Width > 0) current.AppendWord(head);
                    emit(false);
                    if (tail.Width > 0)
                        PlaceWord(tail, ref current, columns, mode, emit);
                    return;
                }
        }
    }

    /// <summary>
    /// Largest soft-hyphen split such that the first piece (plus a "-" hyphen) fits in
    /// <paramref name="maxWidth"/> cells. Returns false when no soft break is usable.
    /// </summary>
    private static bool TrySplitAtSoftBreak(
        WordAtom word, int maxWidth, out WordAtom first, out WordAtom rest)
    {
        first = null!;
        rest = null!;

        // Walk soft-breaks from rightmost down.
        for (int i = word.SoftBreaks.Length - 1; i >= 0; i--)
        {
            var sb = word.SoftBreaks[i];
            if (sb.WidthBefore + 1 > maxWidth) continue;

            // first = runs[0..FragmentIndex] + "-"
            var firstRuns = ImmutableArray.CreateBuilder<FormattedRun>(sb.FragmentIndex + 1);
            for (int r = 0; r < sb.FragmentIndex; r++) firstRuns.Add(word.Runs[r]);
            firstRuns.Add(new FormattedRun("-", sb.Style, sb.Hyperlink));

            var restRuns = ImmutableArray.CreateBuilder<FormattedRun>(word.Runs.Length - sb.FragmentIndex);
            for (int r = sb.FragmentIndex; r < word.Runs.Length; r++) restRuns.Add(word.Runs[r]);

            var restSoftBreaks = ImmutableArray.CreateBuilder<SoftBreakPoint>();
            for (int j = i + 1; j < word.SoftBreaks.Length; j++)
            {
                var s2 = word.SoftBreaks[j];
                restSoftBreaks.Add(s2 with
                {
                    FragmentIndex = s2.FragmentIndex - sb.FragmentIndex,
                    WidthBefore = s2.WidthBefore - sb.WidthBefore
                });
            }

            first = new WordAtom(firstRuns.ToImmutable(), sb.WidthBefore + 1, ImmutableArray<SoftBreakPoint>.Empty);
            rest = new WordAtom(restRuns.ToImmutable(), word.Width - sb.WidthBefore, restSoftBreaks.ToImmutable());
            return true;
        }

        return false;
    }

    /// <summary>
    /// Character-boundary split. Head occupies at most <paramref name="maxWidth"/> cells; tail
    /// holds whatever didn't fit. When maxWidth ≤ 0 or the first grapheme alone exceeds it, Head
    /// is empty.
    /// </summary>
    private static (WordAtom Head, WordAtom Tail) SplitWordAtChar(WordAtom word, int maxWidth)
    {
        if (maxWidth <= 0)
            return (Empty(), word);

        var headRuns = ImmutableArray.CreateBuilder<FormattedRun>();
        var tailRuns = ImmutableArray.CreateBuilder<FormattedRun>();
        int headWidth = 0;
        bool splittingInProgress = true;

        foreach (var run in word.Runs)
        {
            if (!splittingInProgress)
            {
                tailRuns.Add(run);
                continue;
            }

            var enumerator = run.Text.GetGraphemeEnumerator();
            var headFragment = new StringBuilder();
            var tailFragment = new StringBuilder();
            int headFragmentWidth = 0;

            while (enumerator.MoveNext())
            {
                ReadOnlySpan<char> g = enumerator.Current;
                int gw = GraphemeWidth.ClusterWidth(g);

                if (splittingInProgress && headWidth + headFragmentWidth + gw <= maxWidth)
                {
                    headFragment.Append(g);
                    headFragmentWidth += gw;
                }
                else
                {
                    splittingInProgress = false;
                    tailFragment.Append(g);
                }
            }

            if (headFragment.Length > 0)
            {
                headRuns.Add(new FormattedRun(headFragment.ToString(), run.Style, run.Hyperlink));
                headWidth += headFragmentWidth;
            }

            if (tailFragment.Length > 0)
                tailRuns.Add(new FormattedRun(tailFragment.ToString(), run.Style, run.Hyperlink));
        }

        var head = new WordAtom(headRuns.ToImmutable(), headWidth, ImmutableArray<SoftBreakPoint>.Empty);
        var tail = new WordAtom(tailRuns.ToImmutable(), word.Width - headWidth, word.SoftBreaks);
        return (head, tail);

        static WordAtom Empty() => new(ImmutableArray<FormattedRun>.Empty, 0, ImmutableArray<SoftBreakPoint>.Empty);
    }

    // ---- Trimming ----

    /// <summary>
    /// Trim <paramref name="line"/> to fit <paramref name="maxWidth"/>. <paramref name="forceEllipsis"/>
    /// requests an ellipsis even if the line itself fits — used when MaxLines / MaxRows dropped
    /// content past this line.
    /// </summary>
    private LineDraft TrimLine(LineDraft line, int maxWidth, TextTrimming trim, bool forceEllipsis)
    {
        if (trim == TextTrimming.None)
            return line;

        bool overflows = line.Width > maxWidth;

        if (!overflows && !forceEllipsis) return line;

        return trim switch
               {
                   TextTrimming.ClipFromEnd       => ClipDraft(line, maxWidth),
                   TextTrimming.CharacterEllipsis => AppendEllipsisCharacter(line, maxWidth),
                   TextTrimming.WordEllipsis      => AppendEllipsisAtWordBoundary(line, maxWidth),
                   _                              => line
               };
    }

    private static LineDraft ClipDraft(LineDraft line, int maxWidth)
    {
        var (head, _) = SplitWordAtChar(line.AsWord(), maxWidth);
        return LineDraft.FromWord(head);
    }

    private LineDraft AppendEllipsisCharacter(LineDraft line, int maxWidth)
    {
        int ellipsisWidth = GraphemeWidth.StringWidth(Ellipsis);

        if (line.Width + ellipsisWidth <= maxWidth)
        {
            var style = line.Runs.Count > 0 ? line.Runs[^1].Style : default;
            line.Append(new FormattedRun(Ellipsis, style, null), ellipsisWidth);
            return line;
        }

        int budget = Math.Max(0, maxWidth - ellipsisWidth);
        var clipped = ClipDraft(line, budget);
        var clippedStyle = clipped.Runs.Count > 0 ? clipped.Runs[^1].Style : default;
        clipped.Append(new FormattedRun(Ellipsis, clippedStyle, null), ellipsisWidth);
        return clipped;
    }

    private LineDraft AppendEllipsisAtWordBoundary(LineDraft line, int maxWidth)
    {
        int ellipsisWidth = GraphemeWidth.StringWidth(Ellipsis);
        int budget = Math.Max(0, maxWidth - ellipsisWidth);

        // Walk forward through the line accumulating cell width; remember the latest "space"
        // grapheme position that fits within budget.
        int cumulative = 0;
        int cutRunIndex = -1;
        int cutCharIndex = 0;

        for (int r = 0; r < line.Runs.Count; r++)
        {
            var run = line.Runs[r];
            var enumerator = run.Text.GetGraphemeEnumerator();
            int charIndex = 0;

            while (enumerator.MoveNext())
            {
                ReadOnlySpan<char> g = enumerator.Current;
                int gw = GraphemeWidth.ClusterWidth(g);

                if (cumulative + gw > budget)
                {
                    if (cutRunIndex >= 0)
                    {
                        var draft = TruncateAt(line, cutRunIndex, cutCharIndex);
                        var style = draft.Runs.Count > 0 ? draft.Runs[^1].Style : default;
                        draft.Append(new FormattedRun(Ellipsis, style, null), ellipsisWidth);
                        return draft;
                    }
                    // No word boundary seen — fall back to character ellipsis.
                    return AppendEllipsisCharacter(line, maxWidth);
                }

                cumulative += gw;
                charIndex += g.Length;

                if (g.Length == 1 && g[0] == ' ')
                {
                    cutRunIndex = r;
                    cutCharIndex = charIndex;
                }
            }
        }

        // Whole line fits already — append ellipsis directly.
        var styleEnd = line.Runs.Count > 0 ? line.Runs[^1].Style : default;
        line.Append(new FormattedRun(Ellipsis, styleEnd, null), ellipsisWidth);
        return line;
    }

    /// <summary>
    /// Build a new draft from <paramref name="line"/>'s runs up to (run=<paramref name="cutRunIndex"/>,
    /// charIndex=<paramref name="cutCharIndex"/>), stripping the trailing space that marked the
    /// cut. The new draft's width is recomputed from its surviving runs.
    /// </summary>
    private static LineDraft TruncateAt(LineDraft line, int cutRunIndex, int cutCharIndex)
    {
        var draft = new LineDraft();

        for (int r = 0; r < cutRunIndex; r++)
        {
            var run = line.Runs[r];
            draft.Runs.Add(run);
            draft.Width += GraphemeWidth.StringWidth(run.Text);
        }

        var partial = line.Runs[cutRunIndex].Text[..cutCharIndex].TrimEnd(' ');
        if (partial.Length > 0)
        {
            draft.Runs.Add(line.Runs[cutRunIndex] with { Text = partial });
            draft.Width += GraphemeWidth.StringWidth(partial);
        }

        return draft;
    }

    private FormattedParagraph ApplyDocumentRowCap(FormattedParagraph paragraph, int budget, int columns)
    {
        if (paragraph.Lines.Length <= budget) return paragraph;

        var kept = paragraph.Lines.Take(budget).ToImmutableArray();
        var last = LineDraft.FromFormatted(kept[^1]);
        var trimmed = TrimLine(last, columns, Trim, forceEllipsis: true);
        kept = kept.SetItem(budget - 1, trimmed.ToFormattedLine());

        return new FormattedParagraph(kept, new Size(columns, budget));
    }

    // ---- Alignment ----

    private FormattedLine ApplyAlignment(LineDraft line, int columns, TextAlignment alignment, bool isLastLine)
    {
        var effective = (alignment, isLastLine) switch
                        {
                            (TextAlignment.Justify, true) => TextAlignment.Left,
                            _                             => alignment
                        };

        return effective switch
               {
                   TextAlignment.Left    => line.ToFormattedLine(),
                   TextAlignment.Right   => PadStart(line, columns - line.Width),
                   TextAlignment.Center  => PadStart(line, Math.Max(0, (columns - line.Width) / 2)),
                   TextAlignment.Justify => JustifyLine(line, columns),
                   _                     => line.ToFormattedLine()
               };
    }

    private static FormattedLine PadStart(LineDraft line, int padding)
    {
        if (padding <= 0) return line.ToFormattedLine();
        var runs = ImmutableArray.CreateBuilder<FormattedRun>(line.Runs.Count + 1);
        runs.Add(new FormattedRun(new string(' ', padding), default, null));
        foreach (var run in line.Runs) runs.Add(run);
        return new FormattedLine(runs.ToImmutable(), line.Width + padding);
    }

    private static FormattedLine JustifyLine(LineDraft line, int columns)
    {
        int slack = columns - line.Width;
        if (slack <= 0) return line.ToFormattedLine();

        // Inter-word gaps are space-only runs sitting between non-space runs.
        var gapIndices = new List<int>();
        for (int i = 1; i < line.Runs.Count - 1; i++)
            if (IsAllSpaces(line.Runs[i].Text)) gapIndices.Add(i);

        if (gapIndices.Count == 0) return line.ToFormattedLine();

        int extraPer = slack / gapIndices.Count;
        int remainder = slack % gapIndices.Count;

        var newRuns = ImmutableArray.CreateBuilder<FormattedRun>(line.Runs.Count);
        for (int i = 0; i < line.Runs.Count; i++)
        {
            int gapPosition = gapIndices.IndexOf(i);
            if (gapPosition >= 0)
            {
                int extra = extraPer + (gapPosition < remainder ? 1 : 0);
                newRuns.Add(line.Runs[i] with { Text = line.Runs[i].Text + new string(' ', extra) });
            }
            else
            {
                newRuns.Add(line.Runs[i]);
            }
        }

        return new FormattedLine(newRuns.ToImmutable(), columns);
    }

    private static bool IsAllSpaces(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s) if (c != ' ') return false;
        return true;
    }

    // ---- Internal data structures ----

    private abstract record Atom;

    private sealed record WordAtom(
        ImmutableArray<FormattedRun> Runs,
        int Width,
        ImmutableArray<SoftBreakPoint> SoftBreaks) : Atom;

    private sealed record SpaceAtom(FormattedRun Run, int Width) : Atom;

    private sealed record HardBreakAtom : Atom
    {
        public static HardBreakAtom Instance { get; } = new();
    }

    private readonly record struct SoftBreakPoint(
        int FragmentIndex, int WidthBefore, Style Style, string? Hyperlink);

    /// <summary>
    /// Mutable line buffer used during layout. Converts to an immutable
    /// <see cref="FormattedLine"/> only at the alignment step.
    /// </summary>
    private sealed class LineDraft
    {
        public List<FormattedRun> Runs { get; } = [];
        public int Width { get; set; }
        public bool EndedByHardBreak { get; set; }

        public void AppendWord(WordAtom word)
        {
            foreach (var run in word.Runs) Runs.Add(run);
            Width += word.Width;
        }

        public void AppendSpace(SpaceAtom space)
        {
            Runs.Add(space.Run);
            Width += space.Width;
        }

        public void Append(FormattedRun run, int width)
        {
            Runs.Add(run);
            Width += width;
        }

        public void TrimTrailingSpaces()
        {
            while (Runs.Count > 0 && Runs[^1].Hyperlink is null && IsAllSpaces(Runs[^1].Text))
            {
                Width -= GraphemeWidth.StringWidth(Runs[^1].Text);
                Runs.RemoveAt(Runs.Count - 1);
            }
        }

        public WordAtom AsWord() => new([..Runs], Width, ImmutableArray<SoftBreakPoint>.Empty);

        public FormattedLine ToFormattedLine() => new([..Runs], Width);

        public static LineDraft FromWord(WordAtom word)
        {
            var draft = new LineDraft { Width = word.Width };
            foreach (var run in word.Runs) draft.Runs.Add(run);
            return draft;
        }

        public static LineDraft FromFormatted(FormattedLine line)
        {
            var draft = new LineDraft { Width = line.Columns };
            foreach (var run in line.Runs) draft.Runs.Add(run);
            return draft;
        }
    }
}
