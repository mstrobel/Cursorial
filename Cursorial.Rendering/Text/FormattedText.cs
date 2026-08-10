using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Text;

// ReSharper disable RedundantCast

namespace Cursorial.Rendering.Text;

/// <summary>
/// The result of formatting a <see cref="RichText"/> document against a column budget. Immutable
/// — format once and paint many times; querying <see cref="Size"/> doesn't need a buffer.
/// </summary>
public sealed record FormattedText(ImmutableArray<FormattedBlock> Blocks, Size Size, int ProvidedColumns, in CellStyle DefaultStyle = default, bool FillEntireBounds = false) : IContent
{
    /// <summary>Empty formatted document — zero blocks, zero size.</summary>
    public static FormattedText Empty { get; } = new(ImmutableArray<FormattedBlock>.Empty, Size.Empty, 0);

    public bool HasTrimmedLines { get; init; } = AnyTrimmedLines(Blocks);

    private static bool AnyTrimmedLines(ImmutableArray<FormattedBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block.HasTrimmedLines)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Paint the formatted document into <paramref name="buffer"/> at the supplied
    /// <paramref name="bounds"/>. Blocks stack top-to-bottom inside the rect, observing their
    /// <see cref="FormattedBlock.Margin"/> top spacing (the first block's top is suppressed —
    /// the document anchors flush to the bounds). Content is clipped to the rect; returns the
    /// rectangle actually painted.
    /// </summary>
    /// <remarks>
    /// The geometry — margin stacking, the row budget, <see cref="FillEntireBounds"/> re-centring, per-block
    /// anchoring — belongs to <see cref="FormattedBlockWalker"/>. This method decides only what to DRAW at
    /// each placement the walk hands it, and reads the walk's terminal state for the rect it returns.
    /// </remarks>
    public Rect Paint(in CellBufferView buffer, in Rect bounds, OutputCapabilities capabilities,
                      BrushedTextResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (buffer.IsEmpty) return bounds.WithSize(Size.Empty);

        var fillEntireBounds = FillEntireBounds;

        if (fillEntireBounds/* && DefaultStyle.Background.IsDefault is false*/)
            buffer.ClearCells(bounds, DefaultStyle);

        var walker = new FormattedBlockWalker(this, bounds);

        while (walker.MoveNext())
            PaintBlock(walker.Current, buffer, bounds, capabilities, resolver);

        if (fillEntireBounds)
            return bounds;

        return new Rect(bounds.Column, bounds.Row,
                        Math.Min(walker.PaintedColumns, bounds.Columns),
                        walker.Row - bounds.Row);
    }

    /// <summary>
    /// The document's EXTENT inside <paramref name="bounds"/>: the smallest rect containing every block's
    /// cells, once each block has been anchored by its own <see cref="FormattedBlock.Alignment"/> and the
    /// stack has taken margins and any <see cref="FillEntireBounds"/> re-centring into account. Answers
    /// "where will this land in these bounds" without painting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not the widest block placed at the document's origin: a left-aligned block above a right-aligned one
    /// spans from the left block's start to the right block's end, and neither block's own rect covers that.
    /// Within one paragraph it does collapse to the widest line's rect, because all of a paragraph's lines
    /// share one alignment and the widest line's extent then contains the narrower ones.
    /// </para>
    /// <para>
    /// Anchored at <paramref name="bounds"/> with a zero size when nothing places — an empty document, or a
    /// rect with no rows for it.
    /// </para>
    /// </remarks>
    public Rect ComputeExtent(in Rect bounds)
    {
        var walker = new FormattedBlockWalker(this, bounds);
        var extent = Rect.Empty;

        while (walker.MoveNext())
            extent = extent.Union(walker.Current.Extent);

        return extent.IsEmpty ? bounds.WithSize(Size.Empty) : extent;
    }

    internal static int ComputeAnchorColumn(in Rect bounds, FormattedBlock block)
    {
        var alignment = block switch
                        {
                            FormattedParagraph            => TextAlignment.Left, // paragraphs already align their own lines internally
                            FormattedHorizontalRule hr    => hr.Alignment,
                            FormattedContentBlock content => content.Alignment,
                            _                             => TextAlignment.Left
                        };

        return ComputeAnchorColumn(bounds, block.Size.Columns, alignment);
    }

    internal static int ComputeAnchorColumn(in Rect bounds, int columns, TextAlignment alignment)
    {
        int slack = Math.Max(0, bounds.Columns - columns);

        return alignment switch
               {
                   TextAlignment.Right  => bounds.Column + slack,
                   TextAlignment.Center => bounds.Column + slack / 2,
                   _                    => bounds.Column
               };
    }

    private static void PaintBlock(
        in FormattedBlockPlacement placement, in CellBufferView buffer, in Rect bounds,
        OutputCapabilities capabilities, BrushedTextResolver? resolver)
    {
        var block = placement.Block;
        int column = placement.Column;
        int row = placement.Row;
        int maxRows = placement.Rows;

        // The block's 2-D rect — the sampling bounds for a block/document-scoped brush, from the walk. Text
        // and rules sample the resolver per cell; single-Style elements (figlet, sized text, block content)
        // sample one color at the block's center and hand it to their own painter (so a glyph an image/icon
        // degrades to picks up the brush — the fallback-glyph gradient).
        var blockRect = placement.SamplingRect;
        int centerColumn = column + block.Size.Columns / 2;
        int centerRow = row + maxRows / 2;

        switch (block)
        {
            case FormattedParagraph paragraph:
                PaintParagraph(paragraph, buffer, row, maxRows, blockRect, bounds, capabilities, resolver);
                break;
            case FormattedHorizontalRule rule:
                // The width comes from the placement, not from `bounds`, so the columns the rule DRAWS and
                // the columns the walk reports as its extent are one value read twice rather than two
                // expressions that have to be kept in step. Same number either way — the walk sets a rule's
                // PaintColumns to the rect's width precisely because that is what this painter covers.
                PaintHorizontalRule(rule, buffer, column, row, placement.PaintColumns, resolver);
                break;
            case FormattedContentBlock content:
                content.Content.Paint(buffer, new Rect(column, row, block.Size.Columns, maxRows),
                                      ResolveStyle(resolver, default, centerColumn, centerRow, blockRect, tag: null), capabilities);
                break;
        }
    }

    /// <summary>
    /// Resolve one cell's style: the optional brush resolver's delta, sampled at
    /// (<paramref name="column"/>, <paramref name="row"/>) and folded onto <paramref name="legacyBaseStyle"/>.
    /// Used by the single-Style elements (rule / sized / content), which resolve one style for a whole element
    /// rather than per grapheme; the FIGlet arm asks the same question through <see cref="Brushed"/> below.
    /// </summary>
    /// <remarks>
    /// <paramref name="tag"/> is the tag of the run this style belongs to, or null where there is no run to
    /// carry one: the sized arm passes <see cref="FormattedTextRun.Tag"/>, while the rule and the two content
    /// arms have no run and pass null. It is required rather than defaulted because a dropped tag is invisible
    /// at the call site — and dropping it is exactly how a run's own <c>ScopedBrush</c> stopped reaching the
    /// resolver on the sized and FIGlet arms, leaving the document brush to colour a run that had declared one.
    /// </remarks>
    private static CellStyle ResolveStyle(BrushedTextResolver? resolver, in CellStyle legacyBaseStyle, int column, int row, in Rect block, object? tag)
        => resolver is null ? legacyBaseStyle : Brushed(resolver, legacyBaseStyle, column, row, block, tag).ApplyTo(column, row, legacyBaseStyle);

    /// <summary>
    /// The same query, stopping one step earlier — for the FIGlet arm, which hands the unsampled STYLE to
    /// the face so a multi-cell glyph can sample per cell rather than per character.
    /// </summary>
    /// <remarks>
    /// These elements have no inline scope of their own, so their strip is the single cell being asked about:
    /// an inline-scoped brush over a one-wide strip resolves at offset 0, which is what the previous per-cell
    /// context's <c>logicalColumn: 0, scopeWidth: 0</c> pair meant. <paramref name="tag"/> is as
    /// <see cref="ResolveStyle"/>'s — here it is the FIGlet run's <see cref="FormattedTextRun.Tag"/>.
    /// </remarks>
    private static BrushedTextStyle Brushed(BrushedTextResolver resolver, in CellStyle legacyBaseStyle, int column, int row, in Rect block, object? tag)
        => resolver(new BrushedTextContext(legacyBaseStyle, block, new Rect(column, row, 1, 1), tag));

    /// <summary>Paints one paragraph's line bands, top-down, from a placement the walk produced.</summary>
    /// <remarks>
    /// <c>blockRect</c> is the paragraph's sampling rect for a block/document-scoped brush (6a.1), taken from
    /// the walk rather than rebuilt here. There is no block anchor COLUMN parameter: the loop below anchors
    /// every line for itself, against <paramref name="bounds"/> at the paragraph's own
    /// <see cref="FormattedParagraph.Alignment"/> and the LINE's width, so the block anchor reached this method
    /// to position that rect. <c>blockRect</c> is anchored LEFT even where the paragraph is centred or
    /// right-aligned; that answer and the un-diverged one are the walk's <c>SamplingRect</c> and
    /// <c>Extent</c>, meant to be told apart there rather than reconciled here.
    /// </remarks>
    private static void PaintParagraph(FormattedParagraph paragraph, in CellBufferView buffer, int row, int maxRows,
                                       in Rect blockRect, in Rect bounds,
                                       OutputCapabilities capabilities, BrushedTextResolver? resolver)
    {
        // Lines are BANDS (line.Rows tall — the max of the line's runs' LineRows); bands stack.
        // A band that doesn't fully fit the row budget is clipped whole — a half-painted sized
        // glyph is worse than a missing line, and matches the block painters' whole-band rule.
        int bandRow = row;
        int rowsLeft = maxRows;

        foreach (var line in paragraph.Lines)
        {
            if (line.Rows > rowsLeft) break;

            int cursor = ComputeAnchorColumn(bounds, line.Columns, paragraph.Alignment);

            // The band's baseline: the deepest baseline among its runs, counted from the band's
            // TOP row (a COUNT, like every Baseline in this stack — the row INDEX is one less).
            // One pass of integer maxima over runs the loop below walks anyway, so it is computed
            // unconditionally rather than lazily per alignment mode.
            int bandBaseline = 0;
            foreach (var run in line.Runs)
                bandBaseline = Math.Max(bandBaseline, run.LineBaseline);

            foreach (var run in line.Runs)
            {
                // Each run sits within its band per the paragraph's vertical text alignment
                // (proposal-glyph-runs; maintainer decision 2026-08-02 — block-level, default
                // Bottom to match OSC 66's default), unless the run carries its own override —
                // which only runs the FORMATTER synthesized ever do.
                int slack = line.Rows - run.LineRows;
                int runRow = bandRow + ((run.VerticalAlignment ?? paragraph.VerticalAlignment) switch
                                        {
                                            VerticalTextAlignment.Top    => 0,
                                            VerticalTextAlignment.Center => slack - slack / 2, // rounds toward the bottom, per the enum's contract
                                            // Baselines coincide: drop the run by however much
                                            // shallower its baseline is than the band's. Clamped
                                            // into [0, slack] — when the band cannot hold every
                                            // run's descent (two faces of equal height but
                                            // different descents), imperfect alignment beats ink
                                            // bleeding into the neighbouring band.
                                            VerticalTextAlignment.Baseline
                                                => Math.Max(0, Math.Min(bandBaseline - run.LineBaseline, slack)),
                                            _                            => slack
                                        });

                switch (run)
                {
                    case FormattedTextRun { Source.PaintsAsCells: false } glyphText:
                    {
                        int pieceWidth = glyphText.CellWidth;

                        if (glyphText.Source is { Font: { } face, Sizing.IsNormal: true })
                        {
                            // A FIGlet-sourced piece: the face paints DIRECTLY at the piece rect
                            // (per-cell brush sampling, like the old block painter). Never route
                            // a font piece through ScaledText — its placeholder path formats a
                            // figlet block, which is itself a font-sourced run: infinite
                            // recursion by construction.
                            if (resolver is null)
                            {
                                // A run carries a whole CellStyle, so its background arrives through the very
                                // sentinel the delta retires, and the run has no other way to say which it
                                // meant. Read it out loud, here, once: a stated background is the run's own
                                // and BOXES the glyphs, while Color.Default is "nothing to say" and the face
                                // STAMPS — a FIGlet run showing whatever the block sits on through the holes
                                // in its glyphs, which is what it did before there was a way to ask.
                                face.Paint(buffer, cursor, runRow, glyphText.Text,
                                           glyphText.Style.Background.IsDefault
                                               ? PartialStyle.FromInk(glyphText.Style)
                                               : PartialStyle.From(glyphText.Style));
                            }
                            else
                            {
                                // The style goes to the face UNSAMPLED: one FIGlet character covers many
                                // cells, so the face is the only thing that knows which cells exist to sample.
                                var brushed = Brushed(resolver, glyphText.Style, cursor, runRow, blockRect, glyphText.Tag);
                                face.Paint(buffer, cursor, runRow, glyphText.Text, glyphText.Style,
                                           brushed.Style, brushed.Bounds);
                            }
                        }
                        else
                        {
                            // A sized piece paints through ScaledText at the piece's own rect —
                            // normally the OSC 66 fragment (layout resolved the source against
                            // the terminal, so a sized source at paint means the protocol is
                            // supported); when paint-time capabilities are LOWER than layout's
                            // (ToPlainText renders with None), the fallback tree still bottoms
                            // out in the direct font arm above via the placeholder's figlet
                            // block, one level deep.
                            var pieceRect = new Rect(cursor, runRow, pieceWidth, run.LineRows);
                            var style = ResolveStyle(resolver, glyphText.Style,
                                                     cursor + pieceWidth / 2, runRow, blockRect, glyphText.Tag);
                            var scaled = new ScaledText(glyphText.Text, glyphText.Source.Sizing, glyphText.Source.Font)
                                         {
                                             BrushResolver = resolver
                                         };
                            scaled.Paint(buffer, pieceRect, style, capabilities);
                        }

                        cursor += pieceWidth;
                        break;
                    }
                    case FormattedTextRun text:
                    {
                        // Wrap-invariant inline sampling, expressed as a REBASED RECT: a grapheme's logical
                        // offset within its source run is its column minus the column at which the run's
                        // logical offset 0 would sit, and W is the run's total width — so an inline brush
                        // samples the same 1-D strip no matter where the run wrapped, and the sampling call
                        // takes the cell's own coordinates like every other scope.
                        int scopeWidth = text.Scope?.TotalWidth ?? Math.Max(1, text.CellWidth);
                        var inlineScope = new Rect(cursor - text.LogicalStart, runRow, Math.Max(1, scopeWidth), 1);

                        // ONE resolver call per run: which brush wins, at what scope, and which inherited
                        // attributes merge are all run-level facts. Only the sampling is per cell, and the
                        // style does that itself — hoisted entirely when it cannot vary.
                        var brushed = resolver is null
                                          ? BrushedTextStyle.None
                                          : resolver(new BrushedTextContext(text.Style, blockRect, inlineScope, text.Tag));

                        var uniformStyle = brushed.Style.IsUniform
                                               ? brushed.ApplyTo(cursor, runRow, text.Style)
                                               : default;

                        var enumerator = text.Text.GetGraphemeEnumerator();
                        while (enumerator.MoveNext())
                        {
                            var grapheme = enumerator.Current;

                            // The cursor advances by the grapheme's own LAYOUT width, never by what the
                            // surface accepted. Set returns 0 for a cell outside the view, and a
                            // re-based view (WithOrigin, i.e. any negative push translate — a scrolled
                            // document, a negatively-margined element painting inline in its parent's
                            // zone) puts the run's leading cells outside it. Advancing by the return
                            // there stalls the cursor on the first clipped cell and every remaining
                            // grapheme retries that same cell, swallowing the entire run instead of
                            // painting its visible tail. It also decorrelates the brush resolver's
                            // logical offset below from the actual column.
                            int width = GraphemeWidth.ClusterWidth(grapheme);
                            if (width < 1) width = 1;

                            // The run's style, resolved for THIS cell and folded onto the run's own style —
                            // so a brush that owns only a foreground leaves the rest alone. Width is
                            // grapheme-driven, so a substituted style is layout-safe.
                            var style = brushed.Style.IsUniform
                                            ? uniformStyle
                                            : brushed.ApplyTo(cursor, runRow, text.Style);

                            // The one case where the surface knows better: a wide glyph at the window's
                            // right edge degrades to a blank single, and the next grapheme belongs in the
                            // column it did not occupy.
                            int written = buffer.Set(cursor, runRow, grapheme.ToString(), style);
                            if (written > 0) width = written;

                            cursor += width;
                        }
                        break;
                    }
                    case FormattedContentRun content:
                    {
                        var contentBounds = new Rect(cursor, runRow, content.Width, 1);
                        // Inline content samples one color at its center against the block rect — so a fallback
                        // glyph (when no graphics protocol) is brush-colored; a real image ignores the style.
                        var style = ResolveStyle(resolver, content.Style, cursor + content.Width / 2, runRow, blockRect, tag: null);
                        content.Content.Paint(buffer, contentBounds, style, capabilities);
                        cursor += content.Width;
                        break;
                    }
                }
            }

            bandRow += line.Rows;
            rowsLeft -= line.Rows;
            if (rowsLeft <= 0) break;
        }
    }

    private static void PaintHorizontalRule(
        FormattedHorizontalRule rule, in CellBufferView buffer, int column, int row, int width,
        BrushedTextResolver? resolver)
    {
        int glyphWidth = GraphemeWidth.StringWidth(rule.Glyph);
        if (glyphWidth <= 0) return;

        var ruleRect = new Rect(column, row, Math.Max(1, width), 1);   // gradient spans the rule's painted extent
        int cursor = column;
        int end = column + width;
        while (cursor + glyphWidth <= end)
        {
            buffer.Set(cursor, row, rule.Glyph, ResolveStyle(resolver, rule.Style, cursor, row, ruleRect, tag: null));
            cursor += glyphWidth;
        }
    }

    private void BufferToString(in CellBufferView buffer, StringBuilder sb, bool includeWhitespace)
    {
        if (buffer.IsEmpty)
            return;

        int firstNonEmptyRow = -1;
        int lastNonEmptyRow = -1;

        if (!includeWhitespace)
        {
            // Find the first and last rows with content
            for (int row = 0; row < buffer.Rows; row++)
            {
                bool hasContent = false;
                for (int col = 0; col < buffer.Columns; col++)
                {
                    var cell = buffer[col, row];
                    if (!string.IsNullOrWhiteSpace(cell.Grapheme))
                    {
                        hasContent = true;
                        break;
                    }
                }
                if (hasContent)
                {
                    if (firstNonEmptyRow == -1)
                        firstNonEmptyRow = row;
                    lastNonEmptyRow = row;
                }
            }

            // If no content found, return empty
            if (firstNonEmptyRow == -1)
                return;
        }
        else
        {
            firstNonEmptyRow = 0;
            lastNonEmptyRow = buffer.Rows - 1;
        }

        for (int row = firstNonEmptyRow; row <= lastNonEmptyRow; row++)
        {
            int lastNonWhitespaceCol = -1;

            if (!includeWhitespace)
            {
                // Find the last non-whitespace column in this row
                for (int col = buffer.Columns - 1; col >= 0; col--)
                {
                    var cell = buffer[col, row];
                    if (!string.IsNullOrWhiteSpace(cell.Grapheme))
                    {
                        lastNonWhitespaceCol = col;
                        break;
                    }
                }
            }
            else
            {
                lastNonWhitespaceCol = buffer.Columns - 1;
            }

            // Append cells up to the last non-whitespace column (or all if includeWhitespace)
            for (int col = 0; col <= lastNonWhitespaceCol; col++)
            {
                var cell = buffer[col, row];
                if (cell.Kind is not (CellKind.Single or CellKind.WideLeft)) continue;
                sb.Append(string.IsNullOrEmpty(cell.Grapheme) ? " " : cell.Grapheme);
            }

            // Add newline if not the last row
            if (row < lastNonEmptyRow)
                sb.AppendLine();
        }
    }
    public string ToPlainText(in Rect? bounds = null, bool? fillEntireBounds = null)
    {
        var sb = new StringBuilder();

        var effectiveBounds = bounds ?? new Rect(bounds?.Column ?? 0,
                                                 bounds?.Row ?? 0,
                                                 bounds?.ColumnEnd ?? ProvidedColumns,
                                                 bounds?.RowEnd ?? Size.Rows);

        var buffer = new CellBuffer(effectiveBounds.ColumnEnd, effectiveBounds.RowEnd);
        var bufferView = buffer.AsView();

        Paint(bufferView, effectiveBounds, OutputCapabilities.None);

        BufferToString(bufferView, sb, fillEntireBounds ?? FillEntireBounds);
        
        return sb.ToString();
    }

    Size IContent.Measure(Size availableSpace, OutputCapabilities capabilities)
        => Size.ClampTo(availableSpace);

    Rect IContent.Paint(in CellBufferView buffer, in Rect bounds, in CellStyle style, OutputCapabilities capabilities)
        => Paint(buffer, bounds, capabilities);
}


/// <summary>
/// Base type for an individually formatted block. Concrete subtypes mirror the
/// <see cref="Block"/> hierarchy. <see cref="Size"/> is the block's content footprint (excluding
/// <see cref="Margin"/>); <see cref="Margin"/> is the inter-block spacing the formatter requested.
/// </summary>
public abstract record FormattedBlock(Size Size, TextAlignment Alignment, bool HasTrimmedLines)
{
    /// <summary>Inter-block top/bottom margin applied during stacking; horizontal margins are ignored at block level.</summary>
    public Margins Margin { get; init; }
}

/// <summary>
/// A formatted text paragraph: an ordered list of fully-laid-out, aligned lines. Each line's
/// runs have already had glyph maps applied and trimming / alignment padding inserted as
/// regular space runs, so paint can be a straight walk.
/// </summary>
public sealed record FormattedParagraph(ImmutableArray<FormattedLine> Lines, Size Size, TextAlignment Alignment, bool TrimmedLines)
    : FormattedBlock(Size, Alignment, TrimmedLines || AnyTrimmedLines(Lines))
{
    /// <summary>Where shorter runs sit within a taller line band — see
    /// <see cref="TextParagraph.VerticalAlignment"/>.</summary>
    public VerticalTextAlignment VerticalAlignment { get; init; }

    private static bool AnyTrimmedLines(ImmutableArray<FormattedLine> lines)
    {
        foreach (var line in lines)
        {
            if (line.Trimmed)
                return true;
        }

        return false;
    }
}

/// <summary>
/// A formatted horizontal rule. The painter repeats <see cref="Glyph"/> across the line, up to
/// <see cref="Size"/>.Columns, applying <see cref="Style"/> and respecting <see cref="FormattedBlock.Alignment"/>
/// when the rule is narrower than the available column budget (rare for HRs but supported).
/// </summary>
public sealed record FormattedHorizontalRule(
    string Glyph, CellStyle Style, TextAlignment Alignment, Size Size) : FormattedBlock(Size, Alignment, false);

/// <summary>
/// A formatted block-level <see cref="IContent"/> embedding. The painter delegates to
/// <see cref="IContent.Paint"/> at the block's anchor with a rect of <see cref="Size"/>.
/// </summary>
public sealed record FormattedContentBlock(
    IContent Content, TextAlignment Alignment, Size Size) : FormattedBlock(Size, Alignment, false);

/// <summary>
/// A single line of formatted content. <see cref="Columns"/> is the line's visible cell width
/// (after alignment padding); <see cref="Runs"/> is the ordered sequence of styled text
/// fragments that compose it. Lines never contain trailing whitespace.
/// </summary>
public sealed record FormattedLine(ImmutableArray<FormattedRun> Runs, int Columns, bool Trimmed, int Rows = 1);

/// <summary>
/// The atomic unit of paintable content inside a <see cref="FormattedLine"/>. Concrete
/// subtypes carry either visible text (<see cref="FormattedTextRun"/>) or an embedded
/// <see cref="IContent"/> placement (<see cref="FormattedContentRun"/>). The painter walks the
/// line's runs in order and dispatches per subtype.
/// </summary>
public abstract record FormattedRun
{
    /// <summary>The cell footprint this run occupies on its line.</summary>
    public abstract int CellWidth { get; }

    /// <summary>Rows this run's glyphs stand tall — its line's band is at least this many rows.
    /// 1 for everything except sized/FIGlet-sourced text runs.</summary>
    public virtual int LineRows => 1;

    /// <summary>
    /// Rows from the top of this run's box down to and INCLUDING its baseline row — a COUNT in
    /// <c>[1, LineRows]</c>, not a 0-based index (see <see cref="Fonts.GlyphMetrics.Baseline"/>).
    /// The default is the box's bottom row: no descent, which is exactly right for a one-row run.
    /// </summary>
    public virtual int LineBaseline => LineRows;

    /// <summary>
    /// Per-run override of the paragraph's <see cref="TextParagraph.VerticalAlignment"/>, or
    /// <see langword="null"/> (the norm) to follow it. The formatter sets this only on runs IT
    /// synthesizes whose glyph source differs from the run they visually join — today, the
    /// last-resort trim indicator painted by the terminal's own font beside a face that can draw
    /// no indicator of its own. Author content never carries one: the paragraph rule is the
    /// author's rule for the author's runs.
    /// </summary>
    public VerticalTextAlignment? VerticalAlignment { get; init; }
}

/// <summary>
/// A text run — final visible text (post-glyph-map), an SGR style, and an optional OSC&#x202F;8
/// hyperlink target. The painter walks graphemes and writes cells through
/// <see cref="CellBufferView.Set(int, int, string?, in CellStyle)"/>.
/// </summary>
public sealed record FormattedTextRun : FormattedRun
{
    /// <summary>
    /// A text run — final visible text (post-glyph-map), an SGR style, and an optional OSC&#x202F;8
    /// hyperlink target. The painter walks graphemes and writes cells through
    /// <see cref="CellBufferView.Set(int, int, string?, in CellStyle)"/>.
    /// </summary>
    [SetsRequiredMembers]
    public FormattedTextRun(string Text, CellStyle Style, string? Hyperlink = null)
    {
        this.Text = Text;
        this.Style = Style;
        this.Hyperlink = Hyperlink;
        
        if (this.Hyperlink is {} link)
            this.Style = this.Style.WithHyperlink(link);
    }

    /// <inheritdoc/>
    public override int CellWidth => Source.Metrics.StringWidth(Text);

    public required string Text { get; init; }
    public required CellStyle Style { get; init; }

    public string? Hyperlink { get; init; }

    /// <summary>
    /// The run's glyph source (proposal-glyph-runs): how its clusters measure and paint. The
    /// monospace identity by default. Preserved across wrap-splits — every piece of a sized or
    /// FIGlet run measures and paints through the same source.
    /// </summary>
    public GlyphSource Source { get; init; } = GlyphSource.Default;

    /// <inheritdoc/>
    public override int LineRows => Source.Metrics.LineRows;

    /// <inheritdoc/>
    public override int LineBaseline => Source.Metrics.Baseline;

    /// <summary>
    /// Opaque metadata carried over from the source <see cref="TextRun.Tag"/> (preserved across wrap-splits).
    /// Rendering never interprets it; a higher layer (Drawing) reads it to brush-color the run. Null for
    /// ordinary runs.
    /// </summary>
    public object? Tag { get; init; }

    /// <summary>
    /// The cumulative logical (column) offset of this piece's first grapheme within its source inline run.
    /// A higher layer (Drawing) uses it for wrap-invariant 1-D brush sampling, so a grapheme's color is
    /// independent of where the run wrapped. 0 for the first piece / standalone runs. Brush-agnostic geometry.
    /// </summary>
    public int LogicalStart { get; init; }

    /// <summary>
    /// Shared metrics for the source inline run this piece was split from — carries the run's total logical
    /// width, back-filled by the tokenizer once the whole run is emitted (W isn't known until then). All
    /// wrapped pieces of one run reference the same instance. Internal: Drawing reads the width via
    /// <c>BrushedTextContext.ScopeWidth</c>, not this carrier. Null for runs without a brush tag.
    /// </summary>
    internal InlineRunScope? Scope { get; init; }

    public void Deconstruct(out string text, out CellStyle style, out string? hyperlink)
    {
        text = Text;
        style = Style;
        hyperlink = Hyperlink;
    }
}

/// <summary>
/// Shared, mutable carrier for an inline source run's total logical width. All wrapped pieces of one run
/// reference the same instance; the tokenizer fills <see cref="TotalWidth"/> once the run is fully emitted
/// (the width isn't known until then). Pure column geometry — no brush concept — so Rendering stays brush-blind.
/// </summary>
internal sealed class InlineRunScope
{
    public int TotalWidth { get; set; }
}

/// <summary>
/// An inline <see cref="IContent"/> placement laid out atomically inside a paragraph flow.
/// Width is known at format time (from <see cref="IContent.Measure"/>); the painter calls
/// <see cref="IContent.Paint"/> with a rect of (<see cref="Width"/>, 1) anchored at the run's
/// cell position, with the captured <see cref="Style"/> as backdrop.
/// </summary>
public sealed record FormattedContentRun(IContent Content, int Width, CellStyle Style = default) : FormattedRun
{
    /// <inheritdoc/>
    public override int CellWidth => Width;
}
