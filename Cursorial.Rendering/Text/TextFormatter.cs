using System.Collections.Immutable;
using System.Text;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Fonts;
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
    public const string DefaultEllipsis = "…";

    /// <summary>
    /// The trim indicator used when the painting face cannot draw <see cref="Ellipsis"/>. Three
    /// periods: '.' is inside the ASCII 32–126 range every FIGlet font is required to define, so
    /// this is renderable by construction wherever U+2026 is not.
    /// </summary>
    public const string DerivedEllipsis = "...";

    public const char SoftHyphen = '­';
    public const WrapMode DefaultWrap = WrapMode.WordWrap;
    public const TextTrimming DefaultTrim = TextTrimming.None;

    /// <summary>Default wrap mode when a paragraph doesn't specify its own.</summary>
    public WrapMode Wrap { get; init; } = DefaultWrap;

    /// <summary>Default trimming when a paragraph doesn't specify its own; also used for document-level (MaxRows) trim.</summary>
    public TextTrimming Trim { get; init; } = DefaultTrim;

    /// <summary>Default horizontal alignment when a paragraph doesn't specify its own.</summary>
    public TextAlignment Alignment { get; init; } = TextAlignment.Left;

    /// <summary>
    /// Ellipsis appended by <see cref="TextTrimming.CharacterEllipsis"/> and
    /// <see cref="TextTrimming.WordEllipsis"/>. A face that cannot draw this string falls back to
    /// <see cref="DerivedEllipsis"/> — see <see cref="ChooseEllipsis"/>.
    /// </summary>
    public string Ellipsis { get; init; } = DefaultEllipsis;

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
        OutputCapabilities? capabilities = null,
        bool fillEntireBounds = false)
    {
        return FormatCore(text, availableColumns, maxRows, capabilities, fillEntireBounds, plainTextOnly: false);
    }

    public string FormatPlainText(RichText text, int availableColumns, int? maxRows = null)
        => FormatPlainText(new StringBuilder(), text, availableColumns, maxRows).ToString();

    public StringBuilder FormatPlainText(StringBuilder sb, RichText text, int availableColumns, int? maxRows = null)
    {
        var ft = FormatCore(text, availableColumns, maxRows, OutputCapabilities.None, false, plainTextOnly: true);

        foreach (var block in ft.Blocks.OfType<FormattedParagraph>())
        {
            foreach (var inline in block.Lines)
            {
                if (sb.Length > 0) sb.Append('\n'); // '\n' explicitly — this text feeds TextBlock tooltips and fragments that split on LF

                foreach (var run in inline.Runs.OfType<FormattedTextRun>())
                    sb.Append(run.Text);
            }
        }

        return sb;
    }

    private FormattedText FormatCore(RichText text, int availableColumns, int? maxRows, OutputCapabilities? capabilities,
                                     bool fillEntireBounds, bool plainTextOnly)
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
        bool documentRowCapDropped = false; // whole blocks hidden by the row cap ARE trimming — the flag drives the trimmed-content tooltip
        Margins lastBlockMargins = Margins.Zero;

        foreach (var block in text.Blocks)
        {
            int marginTop = first ? 0 : Math.Max(block.Margin.Top, lastBlockMargins.Bottom);
            int rowsBeforeBlock = totalRows + marginTop;
            int budget = maxRows is { } cap ? cap - rowsBeforeBlock : int.MaxValue;
            if (budget <= 0)
            {
                documentRowCapDropped = true;
                break;
            }

            FormattedBlock? formatted = FormatBlock(block, availableColumns, caps, plainTextOnly); 

            if (formatted is null) continue;

            // Carry margin onto the formatted block so the painter knows the stacking gap.
            formatted = formatted with { Margin = block.Margin };

            if (formatted is FormattedParagraph p && p.Size.Rows > budget)
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
            {
                documentRowCapDropped = true;
                break;
            }

            formattedBlocks.Add(formatted);
            totalRows = rowsBeforeBlock + formatted.Size.Rows;
            widthUsed = Math.Max(widthUsed, formatted.Size.Columns);
            first = false;
            lastBlockMargins = block.Margin;
        }

        var result = new FormattedText(formattedBlocks.ToImmutable(),
                                       new Size(widthUsed, totalRows),
                                       availableColumns,
                                       text.DefaultStyle,
                                       fillEntireBounds);

        return documentRowCapDropped && !result.HasTrimmedLines
                   ? result with { HasTrimmedLines = true }
                   : result;
    }

    private FormattedBlock? FormatBlock(Block block, int availableColumns, OutputCapabilities capabilities,
                                        bool plainTextOnly) =>
        block switch
        {
            TextParagraph p  => FormatParagraph(p, availableColumns, capabilities, plainTextOnly),
            HorizontalRule r => plainTextOnly ? null : FormatHorizontalRule(r, availableColumns),
            FigletBlock f    => FormatFigletBlock(f, availableColumns, plainTextOnly),
            SizedTextBlock s => FormatSizedTextBlock(s, availableColumns, capabilities, plainTextOnly),
            BlockContent c   => plainTextOnly ? null : FormatBlockContent(c, availableColumns, capabilities),
            _                => throw new NotSupportedException($"Block type {block.GetType().Name} is not supported by TextFormatter.")
        };

    private FormattedHorizontalRule FormatHorizontalRule(HorizontalRule rule, int availableColumns)
    {
        // A horizontal rule always fills its column budget — alignment is meaningful only when
        // the caller is rendering at a width smaller than the document budget, which we don't
        // express here. Size is (columns, 1).
        if (string.IsNullOrEmpty(rule.Glyph))
            throw new InvalidOperationException("HorizontalRule.Glyph must be non-empty.");

        return new FormattedHorizontalRule(rule.Glyph, rule.Style, rule.Alignment ?? Alignment, new Size(availableColumns, 1));
    }

    private FormattedBlock FormatFigletBlock(FigletBlock block, int availableColumns, bool plainTextOnly)
    {
        if (plainTextOnly)
        {
            var lines = PlainTextToLines(availableColumns, OutputCapabilities.None, block.Text);
            return FormatParagraphCore(lines, alignment: block.Alignment ?? Alignment);
        }

        // The block form is SUGAR over a one-run paragraph (proposal-glyph-runs): the face's
        // metrics drive the shared tokenizer / packer / trimmer, the paragraph painter renders
        // the face per piece, and there is exactly one wrap/trim/paint path to maintain.
        // Kerning/smushing makes per-glyph sums conservative — a wrapped line never comes out
        // wider than predicted, only (rarely) a touch narrower.
        var source = new GlyphSource(block.Face);
        var lines2 = PlainTextToLines(availableColumns, OutputCapabilities.None, block.Text, source,
                                      block.Style);
        return FormatParagraphCore(lines2, alignment: block.Alignment ?? Alignment);
    }

    private FormattedBlock FormatSizedTextBlock(SizedTextBlock block, int availableColumns,
                                                OutputCapabilities capabilities, bool plainTextOnly)
    {
        // The block form is SUGAR over a one-run paragraph (proposal-glyph-runs). The run's
        // source resolves against the terminal at tokenization — "OSC 66 if supported, otherwise
        // the (explicit or bundled) fallback face" — so measurement and paint agree per tier,
        // exactly as the old ScaledText.Measure-based path did.
        var source = plainTextOnly ? null : new GlyphSource(block.Fallback, block.Sizing);

        var lines = PlainTextToLines(availableColumns, capabilities, block.Text, source, block.Style);

        return FormatParagraphCore(lines, alignment: block.Alignment ?? Alignment);
    }

    private List<LineDraft> PlainTextToLines(int availableColumns, OutputCapabilities capabilities, string blockText,
                                             GlyphSource? source = null, Style style = default)
    {
        // Author-supplied line breaks are honored as hard breaks. The paragraph lane keeps its
        // documented contract (LineBreak inlines are the channel; a literal '\n' in a TextRun is
        // an ordinary character), but block TEXT (sized text, FIGlet) has no inline channel — the
        // string is all the author gets, and the fragments downstream split on LF themselves.
        var normalized = blockText.Contains('\r') ? blockText.Replace("\r\n", "\n").Replace('\r', '\n') : blockText;
        var segments = normalized.Split('\n');

        var atoms = new List<Atom>();

        for (int i = 0; i < segments.Length; i++)
        {
            if (i > 0) atoms.Add(HardBreakAtom.Instance);
            // A fresh Tokenizer per segment — Run accumulates into (and returns) instance state.
            // The block's glyph source rides the run; every advance downstream resolves from it.
            atoms.AddRange(new Tokenizer(this, availableColumns, capabilities)
                              .Run([new TextRun(segments[i], style) { Source = source }]));
        }

        var lines = PackLines(atoms, availableColumns, Wrap);

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Width > availableColumns)
                lines[i] = TrimLine(lines[i], availableColumns, Trim, forceEllipsis: false);
        }

        return lines;
    }

    private FormattedContentBlock FormatBlockContent(
        BlockContent block, int availableColumns, OutputCapabilities capabilities)
    {
        var measured = block.Content.Measure(new Size(availableColumns, int.MaxValue), capabilities);
        // Clip horizontally to the column budget; let content drive its own row count.
        var size = new Size(Math.Min(measured.Columns, availableColumns), measured.Rows);
        return new FormattedContentBlock(block.Content, block.Alignment ?? Alignment, size);
    }

    private FormattedParagraph FormatParagraph(TextParagraph paragraph, int availableColumns,
                                               OutputCapabilities capabilities, bool plainTextOnly)
    {
        // 1. Decompose inlines into wrap atoms with applied glyph maps and soft-hyphen markers.
        var atoms = new Tokenizer(this, availableColumns, capabilities, plainTextOnly).Run(paragraph.Inlines);

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
        // Dropping lines IS trimming, whatever the last kept line looks like — with Trim=None the
        // forced ellipsis above is a no-op, and without this the hidden lines would never raise
        // the trimmed flag (no tooltip exactly where content vanished).

        var usedWidth = 0;
        var trimmedLines = droppedByMaxLines;

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Width > availableColumns)
            {
                lines[i] = TrimLine(lines[i], availableColumns, paragraph.Trim, forceEllipsis: false);
                trimmedLines = true;
            }
            usedWidth = Math.Max(usedWidth, lines[i].Width);
        }

        return FormatParagraphCore(lines,
                                   usedWidth,
                                   plainTextOnly ? TextAlignment.Left : paragraph.Alignment,
                                   trimmedLines,
                                   paragraph.VerticalAlignment);
    }

    private FormattedParagraph FormatParagraphCore(List<LineDraft> lines, int? knownUsedWidth = null,
                                                   TextAlignment? alignment = null, bool trimmedLines = false,
                                                   VerticalTextAlignment? verticalAlignment = null)
    {
        var usedWidth = knownUsedWidth ?? lines.Max(l => l.Width);

        // 4. Alignment converts LineDraft → FormattedLine.
        var aligned = ImmutableArray.CreateBuilder<FormattedLine>(lines.Count);

        for (int i = 0; i < lines.Count; i++)
        {
            bool isLastLine = i == lines.Count - 1;
            bool endedByHardBreak = lines[i].EndedByHardBreak;
            aligned.Add(ApplyAlignment(lines[i], usedWidth, alignment ?? Alignment, isLastLine || endedByHardBreak));
        }

        int width = aligned.Count == 0 ? 0 : aligned.Max(l => l.Columns);

        // A line's band is Rows tall (max of its runs' LineRows) — the paragraph stacks bands,
        // so its height is the SUM of line rows, not the line count.
        int rows = 0;
        foreach (var l in aligned) rows += l.Rows;

        return new FormattedParagraph(aligned.ToImmutable(),
                                      new Size(width, rows),
                                      alignment ?? Alignment,
                                      trimmedLines) { VerticalAlignment = verticalAlignment ?? VerticalTextAlignment.Bottom };
    }

    // ---- Tokenization ----

    /// <summary>
    /// Walks inlines, applies each <see cref="TextRun"/>'s <see cref="IGlyphMap"/>, and emits a
    /// flat list of <see cref="Atom"/>s consumable by the line packer. Held as a class so the
    /// in-progress word buffer can be mutated without ref-parameter closure pain.
    /// </summary>
    private sealed class Tokenizer(TextFormatter outer, int availableColumns, OutputCapabilities capabilities,
                                   bool plainTextOnly = false)
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
                    case InlineContent ic:
                        EmitInlineContent(ic);
                        break;
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
            // The run's glyph source decides every advance in this run (proposal-glyph-runs) —
            // and rides every produced FormattedTextRun so splits, trims, and paint stay
            // per-run-sourced downstream.
            // Resolve against the terminal ONCE, at layout time — an unsupported sizing falls
            // back to its face here, so wrap points, trim math, and paint all agree on the tier.
            var runSource = plainTextOnly ? GlyphSource.Default : (run.Source ?? GlyphSource.Default).ResolveFor(capabilities);
            var metrics = runSource.Metrics;

            var fragmentBuilder = new StringBuilder();
            int fragmentWidth = 0;
            string previousCluster = ""; // kerning context — resets wherever a painted piece boundary falls
            // Wrap-invariant 1-D brush sampling: track each piece's cumulative logical offset within this
            // source run, sharing a carrier whose total width is back-filled below (W isn't known until the
            // run is fully emitted). Only when the run carries a brush tag — untagged runs skip the accounting.
            var scope = run.Tag is not null ? new InlineRunScope() : null;
            int runOffset = 0;

            void EmitFragment()
            {
                previousCluster = ""; // a new piece paints independently — no junction to smush
                if (fragmentBuilder.Length == 0) return;
                _wordRuns.Add(new FormattedTextRun(fragmentBuilder.ToString(), run.Style, run.Hyperlink)
                                  { Tag = run.Tag, LogicalStart = runOffset, Scope = scope, Source = runSource });
                runOffset += fragmentWidth;
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
                        // The hyphen visually joins the text BEFORE the break — when a word
                        // spans sources and the soft hyphen leads the second run, the preceding
                        // fragment's source measures (and paints) the "-".
                        _softBreaks.Add(new SoftBreakPoint(
                            FragmentIndex: _wordRuns.Count,
                            WidthBefore: _wordWidth,
                            Style: run.Style,
                            Hyperlink: run.Hyperlink,
                            Source: _wordRuns.Count > 0 && _wordRuns[^1] is FormattedTextRun prev
                                        ? prev.Source
                                        : runSource));
                    }

                    chunkStart = idx + 1;
                }
            }

            EmitFragment();
            // The run is fully emitted — record its total logical width so every wrapped piece samples a
            // brush over the same 1-D strip regardless of where it landed.
            if (scope is not null) scope.TotalWidth = runOffset;

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
                        // TabWidth is in the run's own glyphs — the expanded spaces measure (and
                        // paint) through the source, so the atom's budget matches CellWidth.
                        var tabSpaces = new string(' ', outer.TabWidth);
                        int tabWidth = metrics.StringWidth(tabSpaces);
                        _atoms.Add(new SpaceAtom(
                            new FormattedTextRun(tabSpaces, run.Style, run.Hyperlink)
                                { Tag = run.Tag, LogicalStart = runOffset, Scope = scope, Source = runSource },
                            tabWidth));
                        runOffset += tabWidth;
                        continue;
                    }

                    if (IsBreakingWhitespace(g))
                    {
                        EmitFragment();
                        FlushWord();
                        int spaceWidth = metrics.ClusterWidth(g);
                        _atoms.Add(new SpaceAtom(
                            new FormattedTextRun(g.ToString(), run.Style, run.Hyperlink)
                                { Tag = run.Tag, LogicalStart = runOffset, Scope = scope, Source = runSource },
                            spaceWidth));
                        runOffset += spaceWidth;
                        continue;
                    }

                    fragmentBuilder.Append(g);
                    fragmentWidth += metrics.Advance(previousCluster, g);
                    previousCluster = g.ToString();
                }
            }
        }

        private void EmitInlineContent(InlineContent ic)
        {
            // Flush the in-progress word — inline content is an atomic word of its own; it
            // doesn't fuse with surrounding text. Width comes from IContent.Measure against the
            // paragraph's column budget (clamped to a single row).
            FlushWord();

            // Pass the column budget as a hint; content may legitimately ask for more, in which
            // case the wrap algorithm decides whether to overflow (WordWrapOverflow) or wrap to a
            // fresh line (other modes). Content is atomic — never split.
            var size = ic.Content.Measure(new Size(availableColumns, 1), capabilities);
            int width = Math.Max(0, size.Columns);
            if (width == 0) return;

            var contentRun = new FormattedContentRun(ic.Content, width);
            _atoms.Add(new WordAtom(
                [contentRun],
                width,
                ImmutableArray<SoftBreakPoint>.Empty));
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

    private static void PlaceWord(WordAtom word, ref LineDraft current, int columns, WrapMode mode,
                                  Action<bool> emit)
    {
        // Easy case: fits as-is, or we're in NoWrap and never wrap. All widths are CELLS — the
        // tokenizer already measured every atom through the glyph source's metrics.
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
            if (mode is WrapMode.WordWrapOverflow)
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
            if (head.Width == 0 && tail.Width == word.Width)
            {
                // SplitWordAtChar made no progress — the leading run is unsplittable (atomic
                // inline content) and wider than the column budget. Place it on this empty line
                // and accept overflow rather than recurse forever.
                current.AppendWord(word);
                return;
            }

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
                // Even in CharacterWrap mode, prefer word wrapping when the line is already at
                // least 2/3 filled — wrapping the word whole wastes at most the remaining 1/3.
                // Below that, splitting mid-word keeps lines dense (the mode's whole point).
                // (The original guard tested the COMPLEMENT of this documented threshold —
                // remaining < 2/3 ⟺ filled > 1/3 — engaging word-wrap far too eagerly.)
            case WrapMode.CharacterWrap when current.Width >= columns * 2 / 3:
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
    private static bool TrySplitAtSoftBreak(WordAtom word, int maxWidth, out WordAtom first,
                                            out WordAtom rest)
    {
        first = null!;
        rest = null!;

        // Walk soft-breaks from rightmost down. WidthBefore is already in cells (the tokenizer
        // measures through each run's own source).
        for (int i = word.SoftBreaks.Length - 1; i >= 0; i--)
        {
            var sb = word.SoftBreaks[i];

            // The appended "-" measures (and paints) through the SOURCE of the run it joins — at
            // scaled or FIGlet metrics a hyphen is not 1 cell, and budgeting it as 1 let heads
            // through that overflowed once the hyphen landed.
            int hyphenWidth = sb.Source.Metrics.ClusterWidth("-");
            if (sb.WidthBefore + hyphenWidth > maxWidth) continue;

            // first = runs[0..FragmentIndex] + "-"
            var firstRuns = ImmutableArray.CreateBuilder<FormattedRun>(sb.FragmentIndex + 1);
            for (int r = 0; r < sb.FragmentIndex; r++) firstRuns.Add(word.Runs[r]);
            firstRuns.Add(new FormattedTextRun("-", sb.Style, sb.Hyperlink) { Source = sb.Source });

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

            first = new WordAtom(firstRuns.ToImmutable(), sb.WidthBefore + hyphenWidth, ImmutableArray<SoftBreakPoint>.Empty);
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
        int tailWidth = 0;
        bool splittingInProgress = true;

        foreach (var run in word.Runs)
        {
            if (!splittingInProgress)
            {
                tailRuns.Add(run);
                tailWidth += run.CellWidth;
                continue;
            }

            // Content runs are atomic — if the whole run fits in the remaining head budget,
            // include it; otherwise push it (and everything after) to the tail.
            if (run is not FormattedTextRun text)
            {
                if (headWidth + run.CellWidth <= maxWidth)
                {
                    headRuns.Add(run);
                    headWidth += run.CellWidth;
                }
                else
                {
                    splittingInProgress = false;
                    tailRuns.Add(run);
                    tailWidth += run.CellWidth;
                }
                continue;
            }

            var runMetrics = text.Source.Metrics; // each piece splits at its OWN source's advances
            var enumerator = text.Text.GetGraphemeEnumerator();
            var headFragment = new StringBuilder();
            var tailFragment = new StringBuilder();
            int headFragmentWidth = 0;
            int tailFragmentWidth = 0;
            string headPrev = "", tailPrev = ""; // kerning contexts — the cut starts a fresh piece

            while (enumerator.MoveNext())
            {
                ReadOnlySpan<char> g = enumerator.Current;

                if (splittingInProgress)
                {
                    int gw = runMetrics.Advance(headPrev, g);

                    if (headWidth + headFragmentWidth + gw <= maxWidth)
                    {
                        headFragment.Append(g);
                        headFragmentWidth += gw;
                        headPrev = g.ToString();
                        continue;
                    }

                    splittingInProgress = false;
                }

                tailFragment.Append(g);
                tailFragmentWidth += runMetrics.Advance(tailPrev, g);
                tailPrev = g.ToString();
            }

            if (headFragment.Length > 0)
            {
                // The head keeps this piece's logical start; the tail begins headFragmentWidth further along
                // the source run. Both share the run's scope, so a char-wrapped brushed run stays continuous.
                headRuns.Add(new FormattedTextRun(headFragment.ToString(), text.Style, text.Hyperlink)
                             {
                                 Tag = text.Tag,
                                 LogicalStart = text.LogicalStart,
                                 Scope = text.Scope,
                                 Source = text.Source
                             });
                headWidth += headFragmentWidth;
            }

            if (tailFragment.Length > 0)
            {
                tailRuns.Add(new FormattedTextRun(tailFragment.ToString(), text.Style, text.Hyperlink)
                             {
                                 Tag = text.Tag,
                                 LogicalStart = text.LogicalStart + headFragmentWidth,
                                 Scope = text.Scope,
                                 Source = text.Source
                             });
                tailWidth += tailFragmentWidth;
            }
        }

        // The tail's width is ACCUMULATED at its own kerning contexts, never derived by
        // subtraction — the junction the cut removed belongs to neither piece once they paint
        // independently, so word.Width - headWidth would overcount kerned faces by one smush.
        var head = new WordAtom(headRuns.ToImmutable(), headWidth, ImmutableArray<SoftBreakPoint>.Empty);
        var tail = new WordAtom(tailRuns.ToImmutable(), tailWidth, word.SoftBreaks);
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
        return LineDraft.FromWord(head, trimmed: true);
    }

    /// <summary>
    /// The trim indicator <paramref name="source"/> can actually PAINT, paired with the source
    /// that will paint it. A face draws nothing at all for a codepoint it lacks
    /// (<see cref="FigletFont"/>'s "missing glyph = blank gap" rule), so appending
    /// <see cref="Ellipsis"/> unconditionally made a trimmed FIGlet line indistinguishable from a
    /// complete one — the truncation signal rendered as empty cells. Candidates, in order: the
    /// configured <see cref="Ellipsis"/> (author-settable, so a custom value gets the same
    /// treatment as the default "…"), then <see cref="DerivedEllipsis"/>.
    /// </summary>
    /// <remarks>
    /// <b>Last resort.</b> A decorative face with neither the configured ellipsis nor '.' paints
    /// the configured ellipsis through the MONOSPACE identity instead — the terminal's own font
    /// draws it, one cell per cluster, beside the face's glyphs. That indicator does not match
    /// the face, but a mismatched indicator is a defect in taste while an invisible one is a
    /// defect in correctness: trimming exists to signal truncation, so "render nothing" is never
    /// an outcome. An author who sets <see cref="Ellipsis"/> to the empty string is asking for no
    /// indicator and gets exactly that.
    /// </remarks>
    private EllipsisChoice ChooseEllipsis(GlyphSource source, int maxWidth)
    {
        // Only a face-painted run consults the face. An OSC 66 run's cells are drawn by the
        // TERMINAL's font (a non-normal sizing survives tokenization only when the protocol is
        // supported), and the identity source is a pass-through — both render anything.
        if (Ellipsis.Length == 0 || source.Font is not {} face || !source.Sizing.IsNormal)
            return new EllipsisChoice(Ellipsis, source);

        var metrics = face.GetMetrics();

        if (CanRender(face, Ellipsis) && metrics.StringWidth(Ellipsis) <= maxWidth) 
            return new EllipsisChoice(Ellipsis, source);

        if (CanRender(face, DerivedEllipsis) && metrics.StringWidth(DerivedEllipsis) <= maxWidth)
            return new EllipsisChoice(DerivedEllipsis, source);

        // The last resort is the only construct in the system that puts runs of DIFFERENT metrics
        // in one band, so it is the only one that has to say where its run sits — see
        // EllipsisChoice.ToRun.
        return new EllipsisChoice(Ellipsis, GlyphSource.Default, MixedSource: true);
    }

    /// <summary>Whether <paramref name="face"/> has ink for every codepoint of <paramref name="text"/>.</summary>
    private static bool CanRender(IGlyphFont face, string text)
    {
        for (int i = 0; i < text.Length;)
        {
            // Codepoints, not chars — HasGlyph is keyed the way glyph tables are, and an
            // unpaired surrogate (TryGetRuneAt false) is not something any face can draw.
            if (!Rune.TryGetRuneAt(text, i, out var rune)) return false;
            if (!face.HasGlyph((uint) rune.Value)) return false;
            i += rune.Utf16SequenceLength;
        }

        return true;
    }

    /// <summary>
    /// The chosen trim indicator and the glyph source that measures AND paints it.
    /// <paramref name="MixedSource"/> marks the last resort — the one case where the indicator's
    /// source is NOT the source of the run it joins.
    /// </summary>
    private readonly record struct EllipsisChoice(string Text, GlyphSource Source, bool MixedSource = false)
    {
        /// <summary>Cells the indicator occupies. Measured from the string actually chosen — the
        /// derived "..." is wider than "…" once the face's kerning applies, and a budget computed
        /// from the un-chosen candidate overflows the line by the difference.</summary>
        public int Width => Source.Metrics.StringWidth(Text);

        /// <summary>
        /// The indicator as a run. A MIXED-SOURCE indicator additionally pins its own vertical
        /// placement to <see cref="VerticalTextAlignment.Baseline"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why per-run, and not by changing the paragraph default.</b> Bottom-of-band placement
        /// drops this one-cell indicator into the FACE's descender row — under the glyph bodies
        /// rather than beside them (<c>standard.flf</c>: 6 rows tall, bodies resting on row index
        /// 4, one descender row beneath). But the paragraph's
        /// <see cref="TextParagraph.VerticalAlignment"/> is the AUTHOR's rule for the author's own
        /// content, and it is genuinely right as it stands: bands that mix a face with OSC 66
        /// sized text have no shared baseline to align to — the terminal fills those cell blocks
        /// itself — so Bottom must stay the default there.
        /// </para>
        /// <para>
        /// This run is different in kind: the formatter SYNTHESIZED it, and did so against what
        /// the author asked for (a face-painted indicator the face turned out to be unable to
        /// draw). Having substituted a foreign face, the formatter owns where that face's one cell
        /// lands, under every paragraph rule — Top or Center would float it mid-glyph just as
        /// wrongly as Bottom buries it. And the override costs nothing where nothing was broken:
        /// against a zero-descent face (ansi-shadow, 7/7) the baseline row IS the bottom row.
        /// </para>
        /// </remarks>
        public FormattedTextRun ToRun(in Style style) => new(Text, style, null)
                                                         {
                                                             Source = Source,
                                                             VerticalAlignment = MixedSource
                                                                                     ? VerticalTextAlignment.Baseline
                                                                                     : null
                                                         };
    }

    private LineDraft AppendEllipsisCharacter(LineDraft line, int maxWidth)
    {
        // The ellipsis measures (and paints) through the SOURCE of the run it visually joins —
        // at scaled or FIGlet metrics it is not one cell wide — and its TEXT is whatever that
        // source can actually draw.
        var joins = LastTextRun(line.Runs);
        var source = joins?.Source ?? GlyphSource.Default;
        var choice = ChooseEllipsis(source, maxWidth);
        int ellipsisWidth = choice.Width;

        if (line.Width + ellipsisWidth <= maxWidth)
        {
            line.Append(choice.ToRun(joins?.Style ?? default), ellipsisWidth);
            return line;
        }

        int budget = Math.Max(0, maxWidth - ellipsisWidth);
        var clipped = ClipDraft(line, budget);
        var clippedJoins = LastTextRun(clipped.Runs);
        var clippedChoice = ChooseEllipsis(clippedJoins?.Source ?? source, maxWidth);
        int clippedEllipsisWidth = clippedChoice.Width;

        // The budget was estimated with the LINE-END run's ellipsis width; if clipping landed on
        // a wider-sourced run (or on a face that derives a wider indicator), the join ellipsis can
        // overflow the column budget — re-clip against the actual width until it fits (bounded:
        // widths only shrink).
        for (int guard = 0; clipped.Width + clippedEllipsisWidth > maxWidth && clipped.Width > 0 && guard < 4; guard++)
        {
            clipped = ClipDraft(clipped, Math.Max(0, maxWidth - clippedEllipsisWidth));
            clippedJoins = LastTextRun(clipped.Runs);
            clippedChoice = ChooseEllipsis(clippedJoins?.Source ?? source, maxWidth);
            clippedEllipsisWidth = clippedChoice.Width;
        }

        clipped.Append(clippedChoice.ToRun(clippedJoins?.Style ?? default), clippedEllipsisWidth);
        return clipped;
    }

    private LineDraft AppendEllipsisAtWordBoundary(LineDraft line, int maxWidth)
    {
        // Budget with the ellipsis measured at the LINE-END run's source (the widest the tail
        // ellipsis can be); the appended ellipsis re-measures at the actual cut run's source.
        var lineEnd = LastTextRun(line.Runs);
        var lineEndChoice = ChooseEllipsis(lineEnd?.Source ?? GlyphSource.Default, maxWidth);
        int ellipsisWidth = lineEndChoice.Width;
        int budget = Math.Max(0, maxWidth - ellipsisWidth);

        // Walk forward through the line accumulating cell width; remember the latest "space"
        // grapheme position that fits within budget.
        int cumulative = 0;
        int cutRunIndex = -1;
        int cutCharIndex = 0;

        for (int r = 0; r < line.Runs.Count; r++)
        {
            if (line.Runs[r] is not FormattedTextRun textRun)
            {
                // Content runs are atomic; if they fit in budget, advance; otherwise fall back
                // to character ellipsis (we'd need a way to fold ellipsis into content otherwise).
                cumulative += line.Runs[r].CellWidth;

                if (cumulative > budget)
                    return AppendEllipsisCharacter(line, maxWidth);

                continue;
            }

            var runMetrics = textRun.Source.Metrics;
            var enumerator = textRun.Text.GetGraphemeEnumerator();
            int charIndex = 0;
            string prev = ""; // kerning context — each run is its own painted piece

            while (enumerator.MoveNext())
            {
                ReadOnlySpan<char> g = enumerator.Current;
                int gw = runMetrics.Advance(prev, g);

                if (cumulative + gw > budget)
                {
                    if (cutRunIndex >= 0)
                    {
                        var draft = TruncateAt(line, cutRunIndex, cutCharIndex);
                        var joins = LastTextRun(draft.Runs);
                        var joinChoice = ChooseEllipsis(joins?.Source ?? GlyphSource.Default, maxWidth);
                        int joinWidth = joinChoice.Width;

                        // The budget above assumed the LINE-END run's indicator; the cut can land
                        // on a run whose face derives a wider one. Hand those to the character
                        // path, which re-clips against the width it is actually going to append.
                        if (draft.Width + joinWidth > maxWidth)
                            return AppendEllipsisCharacter(line, maxWidth);

                        draft.Append(joinChoice.ToRun(joins?.Style ?? default), joinWidth);
                        return draft;
                    }
                    // No word boundary seen — fall back to character ellipsis.
                    return AppendEllipsisCharacter(line, maxWidth);
                }

                cumulative += gw;
                charIndex += g.Length;
                prev = g.ToString();

                if (g.Length == 1 && g[0] == ' ')
                {
                    cutRunIndex = r;
                    cutCharIndex = charIndex;
                }
            }
        }

        // Whole line fits already — append ellipsis directly.
        line.Append(lineEndChoice.ToRun(lineEnd?.Style ?? default), ellipsisWidth);
        return line;
    }

    /// <summary>The last visible text run — the one an appended ellipsis visually joins (its
    /// style AND glyph source both carry over).</summary>
    private static FormattedTextRun? LastTextRun(IReadOnlyList<FormattedRun> runs)
    {
        for (int i = runs.Count - 1; i >= 0; i--)
            if (runs[i] is FormattedTextRun text) return text;
        return null;
    }

    /// <summary>
    /// Build a new draft from <paramref name="line"/>'s runs up to (run=<paramref name="cutRunIndex"/>,
    /// charIndex=<paramref name="cutCharIndex"/>), stripping the trailing space that marked the
    /// cut. The new draft's width is recomputed from its surviving runs.
    /// </summary>
    private static LineDraft TruncateAt(LineDraft line, int cutRunIndex, int cutCharIndex)
    {
        // Cutting content away IS trimming — the draft must say so, or a WordEllipsis-trimmed
        // line reports untrimmed and the trimmed-content tooltip never offers the hidden text.
        var draft = new LineDraft { Trimmed = true };

        for (int r = 0; r < cutRunIndex; r++)
        {
            var run = line.Runs[r];
            // Append (not raw Runs.Add) — it maintains the band's Rows alongside Width. A kept
            // scaled run in a draft whose Rows stayed 1 painted one row ABOVE its band (or
            // vanished at row -1) under the default Bottom alignment.
            draft.Append(run, run.CellWidth);
        }

        // Word-boundary cuts always land on a space inside a text run; if we ever wire content
        // runs to be wrap-boundary candidates we'd need more logic here.
        if (line.Runs[cutRunIndex] is FormattedTextRun text)
        {
            var partial = text.Text[..cutCharIndex].TrimEnd(' ');
            if (partial.Length > 0)
                draft.Append(text with { Text = partial }, text.Source.Metrics.StringWidth(partial));
        }

        return draft;
    }

    private FormattedParagraph ApplyDocumentRowCap(FormattedParagraph paragraph, int budget, int columns)
    {
        if (paragraph.Size.Rows <= budget) return paragraph;

        // Keep whole line BANDS while they fit the row budget (a mixed-source line is Rows tall
        // and is kept or dropped whole — no partial bands). A FIRST band taller than the entire
        // budget keeps nothing: reporting more rows than the cap breaks the maxRows contract,
        // and the painter would clip the too-tall band whole anyway — an honest empty paragraph
        // (still flagged trimmed) beats a blank block that claims the space.
        int keptCount = 0;
        int keptRows = 0;

        foreach (var line in paragraph.Lines)
        {
            if (keptRows + line.Rows > budget) break;
            keptRows += line.Rows;
            keptCount++;
        }

        if (keptCount == 0)
        {
            return new FormattedParagraph(ImmutableArray<FormattedLine>.Empty, new Size(columns, 0),
                                          paragraph.Alignment, true)
                   {
                       VerticalAlignment = paragraph.VerticalAlignment
                   };
        }

        var kept = paragraph.Lines.Take(keptCount).ToImmutableArray();
        var last = LineDraft.FromFormatted(kept[^1]);
        var trimmed = TrimLine(last, columns, Trim, forceEllipsis: true);
        kept = kept.SetItem(keptCount - 1, trimmed.ToFormattedLine());

        // The forced trim can change the last band's height (a cut tall run) — recompute from
        // the final lines rather than trusting the pre-trim walk.
        int finalRows = 0;
        foreach (var line in kept) finalRows += line.Rows;

        return new FormattedParagraph(kept, new Size(columns, finalRows), paragraph.Alignment, true)
               {
                   VerticalAlignment = paragraph.VerticalAlignment
               };
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
                   // TextAlignment.Left    => line.ToFormattedLine(),
                   // TextAlignment.Right   => PadStart(line, columns - line.Width),
                   // TextAlignment.Center  => PadStart(line, Math.Max(0, (columns - line.Width) / 2)),
                   TextAlignment.Justify => JustifyLine(line, columns),
                   _                     => line.ToFormattedLine()
               };
    }

    // private static FormattedLine PadStart(LineDraft line, int padding)
    // {
    //     if (padding <= 0) return line.ToFormattedLine();
    //     var runs = ImmutableArray.CreateBuilder<FormattedRun>(line.Runs.Count + 1);
    //     runs.Add(new FormattedTextRun(new string(' ', padding), default, null));
    //     foreach (var run in line.Runs) runs.Add(run);
    //     return new FormattedLine(runs.ToImmutable(), line.Width + padding, line);
    // }

    private static FormattedLine JustifyLine(LineDraft line, int columns)
    {
        int slack = columns - line.Width;
        if (slack <= 0) return line.ToFormattedLine();
    
        // Inter-word gaps are space-only text runs sitting between non-space runs. Content runs
        // can't be gaps; their fixed width is treated as part of an adjacent "word."
        // Only IDENTITY-sourced gaps stretch: appending plain ' ' chars to a scaled/FIGlet gap
        // run adds source-width cells per char while the accounting assumes one — overflowing
        // the very budget justification is meant to fill exactly.
        var gapIndices = new List<int>();
        for (int i = 1; i < line.Runs.Count - 1; i++)
            if (line.Runs[i] is FormattedTextRun { Source.PaintsAsCells: true } text && IsAllSpaces(text.Text))
                gapIndices.Add(i);
    
        if (gapIndices.Count == 0) return line.ToFormattedLine();
    
        int extraPer = slack / gapIndices.Count;
        int remainder = slack % gapIndices.Count;
    
        var newRuns = ImmutableArray.CreateBuilder<FormattedRun>(line.Runs.Count);
        for (int i = 0; i < line.Runs.Count; i++)
        {
            int gapPosition = gapIndices.IndexOf(i);
            if (gapPosition >= 0 && line.Runs[i] is FormattedTextRun gapText)
            {
                int extra = extraPer + (gapPosition < remainder ? 1 : 0);
                newRuns.Add(gapText with { Text = gapText.Text + new string(' ', extra) });
            }
            else
            {
                newRuns.Add(line.Runs[i]);
            }
        }
    
        return new FormattedLine(newRuns.ToImmutable(), columns, line.Trimmed, line.Rows);
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
        int FragmentIndex, int WidthBefore, Style Style, string? Hyperlink, GlyphSource Source);

    /// <summary>
    /// Mutable line buffer used during layout. Converts to an immutable
    /// <see cref="FormattedLine"/> only at the alignment step.
    /// </summary>
    private sealed class LineDraft
    {
        public List<FormattedRun> Runs { get; } = [];

        /// <summary>The line's width in CELLS — atoms are measured through the glyph source's
        /// <see cref="GlyphMetrics"/> at tokenization, so layout runs in one unit end to end.</summary>
        public int Width { get; set; }

        /// <summary>Rows the line's band stands tall — the max of its runs' <see cref="FormattedRun.LineRows"/>
        /// (1 for all-identity lines). Never shrinks: a taller run already on the line keeps its band.</summary>
        public int Rows { get; private set; } = 1;

        public bool EndedByHardBreak { get; set; }
        public bool Trimmed { get; set; }

        public void AppendWord(WordAtom word)
        {
            foreach (var run in word.Runs)
            {
                Runs.Add(run);
                Rows = Math.Max(Rows, run.LineRows);
            }

            Width += word.Width;
        }

        public void AppendSpace(SpaceAtom space)
        {
            Runs.Add(space.Run);
            Rows = Math.Max(Rows, space.Run.LineRows);
            Width += space.Width;
        }

        public void Append(FormattedRun run, int width)
        {
            Runs.Add(run);
            Rows = Math.Max(Rows, run.LineRows);
            Width += width;
        }

        public void TrimTrailingSpaces()
        {
            bool removed = false;

            while (Runs.Count > 0 &&
                   Runs[^1] is FormattedTextRun { Hyperlink: null } text &&
                   IsAllSpaces(text.Text))
            {
                Width -= text.CellWidth;
                Runs.RemoveAt(Runs.Count - 1);
                removed = true;
            }

            if (!removed) return;

            // The removed trailing space may have been the band's tallest run (a scaled space at
            // a source boundary) — Rows only ever grows on append, so recompute it here.
            Rows = 1;
            foreach (var run in Runs) Rows = Math.Max(Rows, run.LineRows);
        }

        public WordAtom AsWord() => new([..Runs], Width, ImmutableArray<SoftBreakPoint>.Empty);

        public FormattedLine ToFormattedLine() => new([..Runs], Width, Trimmed, Rows);

        public static LineDraft FromWord(WordAtom word, bool trimmed = false)
        {
            var draft = new LineDraft { Trimmed = trimmed };
            draft.AppendWord(word); // maintains Width AND the band's Rows
            return draft;
        }

        public static LineDraft FromFormatted(FormattedLine line)
        {
            var draft = new LineDraft { Width = line.Columns };
            foreach (var run in line.Runs)
            {
                draft.Runs.Add(run);
                draft.Rows = Math.Max(draft.Rows, run.LineRows);
            }

            return draft;
        }
    }
}
