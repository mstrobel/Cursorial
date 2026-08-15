using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Rendering.Text;

/// <summary>
/// The result of formatting a <see cref="RichText"/> document against a column budget. Immutable
/// — format once and paint many times; querying <see cref="Size"/> doesn't need a buffer.
/// </summary>
public sealed record FormattedText(ImmutableArray<FormattedBlock> Blocks, Size Size, int ProvidedColumns, bool FillEntireBounds = false) : IContent
{
    /// <summary>Empty formatted document — zero blocks, zero size.</summary>
    public static FormattedText Empty { get; } = new(ImmutableArray<FormattedBlock>.Empty, Size.Empty, 0);

    /// <summary>
    /// The document default's CARRIER — <see cref="RichText.DefaultStyle"/> carried through layout.
    /// The painter folds it under every run and rule carrier (the fall-through: a channel the carrier
    /// states wins over the same channel here), so text that declares nothing takes the document
    /// default without any producer restating it. The rung samples the document's derived extent
    /// (<see cref="ComputeExtent"/>) — where the document lands in the painted bounds, not the box it
    /// was handed. A brush resolver reads <see cref="BrushedStyle.Foreground"/> off it for the
    /// ladder's document leg, and the <see cref="FillEntireBounds"/> surround fill reads
    /// <see cref="BrushedStyle.Background"/> off it — the fall-through rung of the fill's own
    /// source ladder, under the caller's preference.
    /// </summary>
    public BrushedStyle DefaultCarrier { get; init; }

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
    /// <para>
    /// The geometry — margin stacking, the row budget, <see cref="FillEntireBounds"/> re-centring, per-block
    /// anchoring — belongs to <see cref="FormattedBlockWalker"/>. This method decides only what to DRAW at
    /// each placement the walk hands it, and reads the walk's terminal state for the rect it returns.
    /// </para>
    /// <para>
    /// <see cref="FillEntireBounds"/> paints the surround as a BACKGROUND FILL sampling
    /// <paramref name="bounds"/> (docs/text-carrier-design.md, "the clear becomes a background fill").
    /// Its source reads down a ladder: <paramref name="background"/> — the caller's preference — first,
    /// then the document default's <see cref="BrushedStyle.Background"/>; no resolvable source means the
    /// fill contributes nothing (≡ filling <see cref="Color.Transparent"/> — the guard is channel
    /// absence, not a flag). Blank-vs-tint derives from the SAMPLED color per cell
    /// (<see cref="Color.IsOpaque"/>): a transparent sample is a tint no-op, a translucent sample tints
    /// the cell verbatim and its glyph survives for the compositor, and an opaque sample
    /// (<see cref="Color.Default"/>/palette kinds included) blanks and owns the cell as the fill
    /// family's durable occluder.
    /// </para>
    /// </remarks>
    public Rect Paint(in CellBufferView buffer, in Rect bounds, OutputCapabilities capabilities,
                      BrushedTextResolver? resolver = null, IBrush? background = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (buffer.IsEmpty) return bounds.WithSize(Size.Empty);

        var fillEntireBounds = FillEntireBounds;

        if (fillEntireBounds && (background ?? DefaultCarrier.Background) is { } fill)
            FillBackground(buffer, bounds, fill);

        // The fold's document rect — the rung samples where the document LANDS in the rect, not the
        // box it was handed. The gate is a pure optimization: a uniform carrier is rect-independent,
        // so `bounds` is byte-identical there and the extra walk is skipped on the overwhelmingly
        // common path; an unconditional ComputeExtent would be equally correct.
        var documentRect = DefaultCarrier.IsUniform ? bounds : ComputeExtent(bounds);

        var walker = new FormattedBlockWalker(this, bounds);

        while (walker.MoveNext())
            PaintBlock(walker.Current, buffer, bounds, documentRect, capabilities, resolver, DefaultCarrier);

        if (fillEntireBounds)
            return bounds;

        return new Rect(bounds.Column, bounds.Row,
                        Math.Min(walker.PaintedColumns, bounds.Columns),
                        walker.Row - bounds.Row);
    }

    /// <summary>
    /// The <see cref="FillEntireBounds"/> surround fill: <paramref name="background"/> sampled per cell
    /// over <paramref name="bounds"/>, blank-vs-tint decided by the SAMPLED color
    /// (<see cref="Color.IsOpaque"/> — never the brush-level bit, which speaks for the Opacity knob
    /// alone and cannot know a positional brush's color alpha). A mixed-alpha brush therefore owns
    /// where it samples opaque and tints where it samples translucent, per cell.
    /// </summary>
    /// <remarks>
    /// The opaque arm writes the fill family's occluder — the DURABLE whitespace cell over the
    /// <see cref="CellStyle.Default"/> ground (<c>FillOpaque</c>'s cell shape, with durable derived
    /// from sampled alpha instead of a caller flag). The kind is forced by the compositor: a
    /// non-durable styled blank takes the merging path there, and a lower layer's glyph rides into
    /// the target cell — visibly, under a <see cref="Color.Default"/> background — so "owns the
    /// rect" only holds at composite with the durable cell (<c>FillEntireBoundsGroupTests</c>' probe).
    /// The tint arm writes through the raw indexer so the sample's alpha is stored VERBATIM for the
    /// compositor to blend; the cell's glyph and its other channels ride through untouched.
    /// </remarks>
    private static void FillBackground(in CellBufferView buffer, in Rect bounds, IBrush background)
    {
        int colStart = Math.Max(bounds.Column, buffer.LocalColumnStart);
        int rowStart = Math.Max(bounds.Row, buffer.LocalRowStart);
        int colEnd = Math.Min(bounds.ColumnEnd, buffer.LocalColumnEnd);
        int rowEnd = Math.Min(bounds.RowEnd, buffer.LocalRowEnd);
        if (colStart >= colEnd || rowStart >= rowEnd) return;

        // IsUniform hoists the per-cell test for the common case — one sample decides the arm for
        // the whole rect (sampled at the rect's own anchor, like the fill primitives do).
        bool uniform = background.IsUniform;
        var uniformSample = uniform ? background.ColorAt(bounds.Column, bounds.Row, bounds) : default;

        if (uniform && uniformSample.IsTransparent)
            return;   // a tint no-op over every cell — observably untouched

        for (int row = rowStart; row < rowEnd; row++)
        for (int col = colStart; col < colEnd; col++)
        {
            var sample = uniform ? uniformSample : background.ColorAt(col, row, bounds);

            if (sample.IsTransparent)
                continue;

            if (sample.IsOpaque)
            {
                buffer[col, row] = new Cell(CellBuffer.DurableEmptyGrapheme, CellKind.Single,
                                            CellStyle.Default with { Background = sample });
            }
            else
            {
                var cell = buffer[col, row];
                buffer[col, row] = cell with { Style = cell.Style.WithBackground(sample) };
            }
        }
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
        in FormattedBlockPlacement placement, in CellBufferView buffer, in Rect bounds, in Rect documentRect,
        OutputCapabilities capabilities, BrushedTextResolver? resolver, in BrushedStyle documentCarrier)
    {
        var block = placement.Block;
        int column = placement.Column;
        int row = placement.Row;
        int maxRows = placement.Rows;

        // The block's 2-D rect — the sampling bounds for a block-scoped brush, from the walk. This is the
        // walk's EXTENT: anchored by the block's own alignment, so a block-declared gradient ramps across
        // the cells the block actually inks (a centered paragraph in a wider rect samples where its glyphs
        // land, not a Left-forced copy of its box). Text and rules sample the resolver per cell;
        // single-Style elements (figlet, sized text, block content) sample one color at the block's center
        // and hand it to their own painter (so a glyph an image/icon degrades to picks up the brush — the
        // fallback-glyph gradient).
        var blockRect = placement.Extent;
        int centerColumn = column + block.Size.Columns / 2;
        int centerRow = row + maxRows / 2;

        switch (block)
        {
            case FormattedParagraph paragraph:
                PaintParagraph(paragraph, buffer, row, maxRows, blockRect, bounds, documentRect, capabilities, resolver, documentCarrier);
                break;
            case FormattedHorizontalRule rule:
                // The width comes from the placement, not from `bounds`, so the columns the rule DRAWS and
                // the columns the walk reports as its extent are one value read twice rather than two
                // expressions that have to be kept in step. Same number either way — the walk sets a rule's
                // PaintColumns to the rect's width precisely because that is what this painter covers.
                PaintHorizontalRule(rule, buffer, column, row, placement.PaintColumns, documentRect, resolver, documentCarrier);
                break;
            case FormattedContentBlock content:
                // The document rung folds under content now (the content-rung ruling this phase
                // settles): the sized, FIGlet and rule arms all fold it already, and the resolver's
                // generic ladder was giving content the rung's declared BRUSH while the fold withheld
                // its VALUES — one declaration, two answers depending on whether a resolver happened
                // to be installed. The content still takes ONE style sampled at its center; the
                // sampled VALUE is restated as the carrier the content lane speaks (FromStated), so
                // the sample point stays this caller's.
                content.Content.Paint(buffer, new Rect(column, row, block.Size.Columns, maxRows),
                                      AsBrushed(ResolveStyle(resolver, documentCarrier, block: default, default, centerColumn, centerRow, documentRect, blockRect)), capabilities);
                break;
        }
    }

    /// <summary>
    /// Resolve one cell's style: the document rung, then the block rung, then the run's own carrier
    /// resolved at (<paramref name="column"/>, <paramref name="row"/>) on top (<see cref="ResolveBaseDelta"/>),
    /// then the optional brush resolver's delta sampled there and folded on top of all three.
    /// Used by the single-Style elements (rule / content), which resolve one style for a whole element
    /// rather than per grapheme — their carrier's strip is the single cell being asked about; the sized
    /// arm asks through the strip overload below, and the FIGlet arm through <see cref="Brushed"/>.
    /// </summary>
    private static PartialStyle ResolveStyle(BrushedTextResolver? resolver, in BrushedStyle document, in BrushedStyle block, in BrushedStyle style, int column, int row, in Rect docRect, in Rect blockRect)
        => ResolveStyle(resolver, document, block, style, column, row, docRect, blockRect, new Rect(column, row, 1, 1));

    /// <summary>
    /// The same query with the carrier's sampling rect stated by the caller — the sized arm hands the
    /// run's wrap-invariant strip (task #15: a run-declared brush on a glyph run samples the run's
    /// reading-order strip, exactly as text runs do), while the per-cell elements keep the single cell.
    /// </summary>
    private static PartialStyle ResolveStyle(BrushedTextResolver? resolver, in BrushedStyle document, in BrushedStyle block, in BrushedStyle style, int column, int row, in Rect docRect, in Rect blockRect, in Rect carrierRect)
    {
        // Compose the ladder as a value DELTA and keep it one: the resolver's delta rides ON TOP of the
        // rungs (Then), the whole thing stays a PartialStyle. Callers that want a carrier lift it losslessly
        // with AsBrushed — which preserves the Mode, attribute removals and underline shape that a CellStyle
        // round-trip (ApplyTo(CellStyle.Default) then FromStated) would drop; the one caller that writes it
        // resolves at the buffer boundary.
        var baseDelta = ResolveBaseDelta(document, block, style, column, row, docRect, blockRect, carrierRect);
        return resolver is null
                   ? baseDelta
                   : baseDelta.Then(Brushed(resolver, style, baseDelta.UnderlineShape ?? CellStyle.Default.UnderlineStyle, carrierRect, blockRect, block.Foreground).Resolve(column, row));
    }

    /// <summary>
    /// The fall-through fold as a value DELTA — the three rungs each resolved at their own rect and composed
    /// (<see cref="PartialStyle.Then"/>), left UNRESOLVED. Consumers lift it to a carrier with
    /// <see cref="AsBrushed"/> or fold it at the write boundary, resolving to a whole <c>CellStyle</c> only
    /// where one is genuinely needed. The plain-text arm folds this onto the destination cell at
    /// <see cref="CellBufferView.Set(int, int, string?, in PartialStyle)"/> — so the value lands at the write
    /// boundary rather than being resolved to a whole <see cref="CellStyle"/> here.
    /// </summary>
    private static PartialStyle ResolveBaseDelta(in BrushedStyle document, in BrushedStyle block, in BrushedStyle carrier, int column, int row, in Rect documentRect, in Rect blockRect, in Rect carrierRect)
        => document.Resolve(column, row, documentRect)
                   .Then(block.Resolve(column, row, blockRect))
                   .Then(carrier.Resolve(column, row, carrierRect));

    /// <summary>
    /// Owns the plain-text run's INK before the delta write: a FOREGROUND no rung stated resolves to
    /// <see cref="Color.Default"/> — the terminal-default sentinel — rather than falling through to whatever the
    /// destination cell held. This is the delta path's equivalent of the old <c>ApplyTo(CellStyle.Default)</c>
    /// stamp and the FIGlet arm's <see cref="InkBase"/>: the piece's ink is owned, so an unstated colour is the
    /// terminal default, not the board underneath. (A cleared cell already carries that default in RGB form, so
    /// inheriting it would be incidental — and would pin an arbitrary colour on a cell that had been written.)
    /// Every other channel legitimately folds onto the cell: the background falls through by design, and on a
    /// blank the rest already equal <see cref="CellStyle.Default"/>'s, so the write lands byte-identical to the
    /// resolved stamp.
    /// </summary>
    private static PartialStyle OwnInk(in PartialStyle style) => style with { Foreground = style.Foreground ?? Color.Default };

    /// <summary>
    /// The base a glyph face's fold rides over: every ink channel of <see cref="CellStyle.Default"/>
    /// stated, the background left absent so its presence still picks stamp vs box. A face folds its
    /// delta onto the DESTINATION cells (see <see cref="Fonts.IGlyphFont"/>), but the piece's ink is
    /// owned — a channel no rung states must resolve to the terminal default, not to whatever the board
    /// underneath held, which is the answer the restated carriers used to state outright.
    /// </summary>
    private static readonly BrushedStyle InkBase = BrushedStyle.FromInk(CellStyle.Default);

    /// <summary>
    /// A resolved value delta lifted back to carrier form — solid brushes for its stated colors, the
    /// attribute masks copied — so a rung already sampled at its own rect can compose UNDER a carrier
    /// that must reach a glyph face unsampled.
    /// </summary>
    private static BrushedStyle AsBrushed(in PartialStyle delta) => new()
    {
        Foreground     = delta.Foreground     is { } fg ? new SolidColorBrush(fg) : null,
        Background     = delta.Background     is { } bg ? new SolidColorBrush(bg) : null,
        UnderlineColor = delta.UnderlineColor is { } uc ? new SolidColorBrush(uc) : null,
        Hyperlink      = delta.Hyperlink,
        UnderlineShape = delta.UnderlineShape,
        Mode           = delta.Mode,
        Clear          = delta.Clear,
        Xor            = delta.Xor,
    };

    /// <summary>
    /// The same query, stopping one step earlier — for the FIGlet arm, which hands the unsampled STYLE to
    /// the face so a multi-cell glyph can sample per cell rather than per character.
    /// </summary>
    /// <remarks>
    /// <paramref name="strip"/> is the run's wrap-invariant reading-order strip — the geometry a
    /// run-declared brush samples (<see cref="BrushedTextContext.InlineScope"/>). Glyph runs pass the
    /// strip <see cref="GlyphRunStrip"/> builds; the per-cell elements (rule / content), which have no
    /// inline scope of their own, pass the single cell being asked about — an inline-scoped brush over a
    /// one-wide strip resolves that cell at its midpoint, which is what the previous per-cell context's
    /// <c>logicalColumn: 0, scopeWidth: 0</c> pair meant.
    /// </remarks>
    private static BrushedTextStyle Brushed(BrushedTextResolver resolver, in BrushedStyle style, UnderlineStyle resolvedUnderlineShape, in Rect strip, in Rect block, IBrush? blockForeground)
        => resolver(new BrushedTextContext(style, blockForeground, block, strip, resolvedUnderlineShape));

    /// <summary>
    /// A glyph-run piece's wrap-invariant reading-order strip — the text arm's <c>inlineScope</c>
    /// construction with the piece's own BAND as the vertical axis (task #15, maintainer rulings
    /// 2026-08-11). The horizontal coordinate is wrap-invariant: the rect starts where the source run's
    /// logical offset 0 would sit and spans the run's TOTAL width, so a horizontal ramp continues across
    /// a wrap instead of restarting per piece. The vertical coordinate is band-local: the rect's rows are
    /// the piece's own band, so a vertical ramp spans the glyph's rows and REPEATS per wrapped band.
    /// Text runs are this same shape with <c>LineRows == 1</c> — no brush classification anywhere, and a
    /// degenerate 1-cell strip needs no special case (its one cell samples t = 0.5 by the same
    /// per-cell arithmetic).
    /// </summary>
    private static Rect GlyphRunStrip(FormattedTextRun piece, int cursor, int runRow, int lineRows)
    {
        int scopeWidth = piece.Scope?.TotalWidth ?? Math.Max(1, piece.CellWidth);
        return new Rect(cursor - piece.LogicalStart, runRow, Math.Max(1, scopeWidth), lineRows);
    }

    /// <summary>Paints one paragraph's line bands, top-down, from a placement the walk produced.</summary>
    /// <remarks>
    /// <c>blockRect</c> is the paragraph's sampling rect for a block-scoped brush (6a.1) — the walk's
    /// <c>Extent</c>, ink-anchored by the paragraph's own alignment. There is no block anchor COLUMN
    /// parameter: the loop below anchors every line for itself, against <paramref name="bounds"/> at the
    /// paragraph's own <c>Alignment</c> and the LINE's width. <c>documentRect</c> is the document rung's
    /// sampling rect — the document's derived extent, computed once in <see cref="Paint"/>;
    /// <paramref name="bounds"/> stays alongside because line anchoring still needs the PAINT rect.
    /// </remarks>
    private static void PaintParagraph(FormattedParagraph paragraph, in CellBufferView buffer, int row, int maxRows,
                                       in Rect blockRect, in Rect bounds, in Rect documentRect,
                                       OutputCapabilities capabilities, BrushedTextResolver? resolver,
                                       in BrushedStyle documentCarrier)
    {
        // Lines are BANDS (line.Rows tall — the max of the line's runs' LineRows); bands stack.
        // A band that doesn't fully fit the row budget is clipped whole — a half-painted sized
        // glyph is worse than a missing line, and matches the block painters' whole-band rule.
        int bandRow = row;
        int rowsLeft = maxRows;

        // The paragraph's own declarations — the ladder's block rung. It folds between the document
        // rung and every run's carrier, sampling the block rect (the declaration site's geometry).
        var blockStyle = paragraph.Style;

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
                                            // bleeding into the neighboring band.
                                            VerticalTextAlignment.Baseline
                                                => Math.Max(0, Math.Min(bandBaseline - run.LineBaseline, slack)),
                                            _                            => slack
                                        });

                switch (run)
                {
                    case FormattedTextRun { Source.PaintsAsCells: false } glyphText:
                    {
                        int pieceWidth = glyphText.CellWidth;

                        // The run's wrap-invariant strip, glyph-run form (task #15): the sampling
                        // geometry a run-declared brush gets on BOTH glyph arms and on both of the
                        // FIGlet arm's paths — reading-order column, band top, total run width, band
                        // height. For an unwrapped run it coincides with the piece rect.
                        var strip = GlyphRunStrip(glyphText, cursor, runRow, run.LineRows);

                        if (glyphText.Source is { Font: { } face, Sizing.IsNormal: true })
                        {
                            // A FIGlet-sourced piece: the face paints DIRECTLY at the piece rect
                            // (per-cell brush sampling, like the old block painter). Never route
                            // a font piece through ScaledText — its placeholder path formats a
                            // figlet block, which is itself a font-sourced run: infinite
                            // recursion by construction.
                            //
                            // The document and block rungs fold UNDER the piece's carrier on both arms,
                            // each resolved at its own rect FIRST (a value delta, per ResolveBaseDelta's rule) so
                            // the fold cannot re-aim their brushes at the piece's box. Unlike the buffer.Set
                            // arms, a face FOLDS its delta onto the destination cells — so the whole fold
                            // rides over InkBase: the piece's ink is owned, and a channel no rung states
                            // resolves to the terminal default rather than to whatever the board underneath
                            // held.
                            var documentDelta = documentCarrier.Resolve(cursor, runRow, documentRect)
                                                              .Then(blockStyle.Resolve(cursor, runRow, blockRect));
                            var pieceCarrier = InkBase.Then(AsBrushed(documentDelta)).Then(glyphText.Style);

                            if (resolver is null)
                            {
                                // The composed delta answers stamp-vs-box by its background's PRESENCE —
                                // InkBase leaves it absent, so only a rung that STATES one boxes. A
                                // composition that cannot vary resolves once and takes the flat overload,
                                // whose box arm fills the glyph gaps; one that can (a run-declared gradient)
                                // goes to the face unsampled with the run's STRIP as its geometry — the
                                // same strip the resolver path hands over, so the two paths agree about
                                // one document (task #15's ruling: the null arm changes to match).
                                if (pieceCarrier.IsUniform)
                                    face.Paint(buffer, cursor, runRow, glyphText.Text,
                                               pieceCarrier.Resolve(cursor, runRow, strip));
                                else
                                    face.Paint(buffer, cursor, runRow, glyphText.Text,
                                               pieceCarrier, strip);
                            }
                            else
                            {
                                // The style goes to the face UNSAMPLED: one FIGlet character covers many
                                // cells, so the face is the only thing that knows which cells exist to sample.
                                // The run's carrier is authored over arbitrary cell content, so it cannot be
                                // left to fall through to the cells — it rides UNDER the resolver's delta,
                                // and the document rung and ink base ride under both. The context's inline
                                // scope is the run's strip, so the run leg's brush gets its GEOMETRY —
                                // wrap-invariant columns, band-local rows — not just its color.
                                var resolvedBase = documentDelta.Then(glyphText.Style.Resolve(cursor, runRow, strip)).ApplyTo(CellStyle.Default);
                                var brushed = Brushed(resolver, glyphText.Style, resolvedBase.UnderlineStyle, strip, blockRect, blockStyle.Foreground);
                                face.Paint(buffer, cursor, runRow, glyphText.Text,
                                           pieceCarrier.Then(brushed.Style), brushed.Bounds);
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
                            // The single-style arm keeps its center sample — one color for the whole
                            // piece, which is what reaches the OSC 66 backdrop SGR — and restates the
                            // sampled VALUE as the content lane's carrier (FromStated: stated channels
                            // become the delta, Default-valued ones stay absent, so ScaledText's
                            // figlet bake keeps the same stamp/box answer the CellStyle hand-off had).
                            // The sample POINT is the piece's center; the GEOMETRY a run-declared brush
                            // resolves against is the run's strip (task #15) — the same rect the FIGlet
                            // arm hands its face — so the one sample reads the center cell's
                            // wrap-invariant position on the run's own ramp.
                            var style = ResolveStyle(resolver, documentCarrier, blockStyle, glyphText.Style,
                                                     cursor + pieceWidth / 2, runRow, documentRect, blockRect, strip);
                            var scaled = new ScaledText(glyphText.Text, glyphText.Source.Sizing, glyphText.Source.Font)
                                         {
                                             BrushResolver = resolver
                                         };
                            scaled.Paint(buffer, pieceRect, AsBrushed(style), capabilities);
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
                        // takes the cell's own coordinates like every other scope. One construction shared
                        // with the glyph arms (GlyphRunStrip); a text run's band is 1 row, so the strip's
                        // band-local vertical axis is the run's row — the pre-task-#15 shape, unchanged.
                        var inlineScope = GlyphRunStrip(text, cursor, runRow, run.LineRows);

                        // The run's carrier resolves against its own strip, over the block and document
                        // rungs resolved at THEIR rects — the fall-through fold (ResolveBaseDelta); a
                        // composition that cannot vary by cell resolves once for the run.
                        var carrier = text.Style;
                        // The run's fall-through fold as a DELTA (ResolveBaseDelta) — it stays a PartialStyle all
                        // the way to buffer.Set, which folds it onto the destination cell. OwnInk below restores
                        // the ink-ownership the old ApplyTo(CellStyle.Default) gave, so the resolution to a whole
                        // CellStyle happens at the write boundary and nowhere earlier.
                        var runBase = ResolveBaseDelta(documentCarrier, blockStyle, carrier, cursor, runRow, documentRect, blockRect, inlineScope);

                        // ONE resolver call per run: which declaration wins, at what scope, and which
                        // inherited attributes merge are all run-level facts. Only the sampling is per
                        // cell, and the style does that itself — hoisted entirely when it cannot vary. The
                        // context's Style stays the run's OWN carrier: the fold never turns inherited
                        // channels into declarations. The context's underline SHAPE is the resolved one —
                        // the delta's, or CellStyle.Default's when no rung states it.
                        var brushed =
                            resolver?.Invoke(new BrushedTextContext(carrier, blockStyle.Foreground, blockRect,
                                                                    inlineScope, runBase.UnderlineShape ?? CellStyle.Default.UnderlineStyle))
                            ?? BrushedTextStyle.None;

                        bool baseUniform = documentCarrier.IsUniform && blockStyle.IsUniform && carrier.IsUniform;
                        bool uniform = baseUniform && brushed.Style.IsUniform;
                        var uniformStyle = uniform
                                               ? OwnInk(runBase.Then(brushed.Resolve(cursor, runRow)))
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

                            // The resolver's delta, resolved for THIS cell and composed OVER the run's own base
                            // delta — so a brush that owns only a foreground leaves the rest alone — then ink-owned
                            // (OwnInk) and left a PartialStyle down to buffer.Set. Width is grapheme-driven, so a
                            // substituted style is layout-safe.
                            var style = uniform
                                            ? uniformStyle
                                            : OwnInk((baseUniform
                                                          ? runBase
                                                          : ResolveBaseDelta(documentCarrier, blockStyle, carrier, cursor, runRow, documentRect, blockRect, inlineScope))
                                                     .Then(brushed.Resolve(cursor, runRow)));

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
                        // Inline content samples one color at its center — so a fallback glyph (when no
                        // graphics protocol) is brush-colored; a real image ignores the style. The
                        // document rung folds under the content's carrier (the content-rung ruling —
                        // see the block-content arm); the paragraph's block rung deliberately does
                        // not yet, which is the recorded follow-on. The center-sampled VALUE is
                        // restated as the lane's carrier (FromStated) so the sample point stays here.
                        var style = ResolveStyle(resolver, documentCarrier, block: default, content.Style, cursor + content.Width / 2, runRow, documentRect, blockRect);
                        content.Content.Paint(buffer, contentBounds, AsBrushed(style), capabilities);
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
        FormattedHorizontalRule rule, in CellBufferView buffer, int column, int row, int width, in Rect documentRect,
        BrushedTextResolver? resolver, in BrushedStyle documentCarrier)
    {
        int glyphWidth = GraphemeWidth.StringWidth(rule.Glyph);
        if (glyphWidth <= 0) return;

        var ruleRect = new Rect(column, row, Math.Max(1, width), 1);   // gradient spans the rule's painted extent
        int cursor = column;
        int end = column + width;
        while (cursor + glyphWidth <= end)
        {
            buffer.Set(cursor, row, rule.Glyph, OwnInk(ResolveStyle(resolver, documentCarrier, block: default, rule.Style, cursor, row, documentRect, ruleRect)));
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

    // The style is IGNORED, deliberately — under the CellStyle signature and under the delta
    // alike. An embedded document paints its own runs over its own document rung; folding the
    // caller's delta in as an outer preference here would be new behavior on a route whose
    // callers (FragmentContent's base placeholder hand-off) rely on the drop today, and
    // ScaledText bypasses this route precisely because the drop exists. A preference-carrying
    // embedded paint is Paint(buffer, bounds, capabilities, resolver)'s job.
    Rect IContent.Paint(in CellBufferView buffer, in Rect bounds, in BrushedStyle style, OutputCapabilities capabilities)
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

    /// <summary>
    /// The paragraph's own declarations — <see cref="TextParagraph.Style"/> carried through layout.
    /// The ladder's block rung: the painter folds it between the document rung and every run's
    /// carrier, sampling the block's rect, so a block-declared gradient spans the block and a run's
    /// position within it decides the color. The identity for a paragraph that declares nothing.
    /// </summary>
    public BrushedStyle Style { get; init; }

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
    string Glyph, BrushedStyle Style, TextAlignment Alignment, Size Size) : FormattedBlock(Size, Alignment, false);

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
/// A text run — final visible text (post-glyph-map), a <see cref="BrushedStyle"/> carrier, and an
/// optional OSC&#x202F;8 hyperlink target. The painter resolves the carrier per cell, walks
/// graphemes, and writes cells through
/// <see cref="CellBufferView.Set(int, int, string?, in CellStyle)"/>.
/// </summary>
public sealed record FormattedTextRun : FormattedRun
{
    /// <summary>
    /// A text run — final visible text (post-glyph-map), a <see cref="BrushedStyle"/> carrier, and an
    /// optional OSC&#x202F;8 hyperlink target. The painter resolves the carrier per cell, walks
    /// graphemes, and writes cells through
    /// <see cref="CellBufferView.Set(int, int, string?, in CellStyle)"/>.
    /// </summary>
    [SetsRequiredMembers]
    public FormattedTextRun(string Text, BrushedStyle Style, string? Hyperlink = null)
    {
        this.Text = Text;
        this.Style = Style;
        this.Hyperlink = Hyperlink;

        if (this.Hyperlink is {} link)
            this.Style = this.Style.WithHyperlink(new Hyperlink(link));
    }

    /// <inheritdoc/>
    public override int CellWidth => Source.Metrics.StringWidth(Text);

    public required string Text { get; init; }
    public required BrushedStyle Style { get; init; }

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
    /// The cumulative logical (column) offset of this piece's first grapheme within its source inline run.
    /// A higher layer (Drawing) uses it for wrap-invariant 1-D brush sampling, so a grapheme's color is
    /// independent of where the run wrapped. 0 for the first piece / standalone runs. Brush-agnostic geometry.
    /// </summary>
    public int LogicalStart { get; init; }

    /// <summary>
    /// Whether this piece came from an INDICATOR run (<see cref="TextRun.Indicator"/> — an
    /// access-key mnemonic or similar cue). Its <see cref="Style"/> marks exactly its own
    /// clusters: the formatter's trim paths never let a synthesized ellipsis join an indicator
    /// run's style (maintainer ruling 2026-08-11 #3) — the ellipsis takes the nearest preceding
    /// non-indicator run's style instead, falling back to the document default.
    /// </summary>
    public bool Indicator { get; init; }

    /// <summary>
    /// Shared metrics for the source inline run this piece was split from — carries the run's total logical
    /// width, back-filled by the tokenizer once the whole run is emitted (W isn't known until then). All
    /// wrapped pieces of one run reference the same instance. Internal: Drawing reads the width via
    /// <c>BrushedTextContext.InlineScope</c>, not this carrier. Null for runs that state no foreground
    /// brush.
    /// </summary>
    internal InlineRunScope? Scope { get; init; }

    public void Deconstruct(out string text, out BrushedStyle style, out string? hyperlink)
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
public sealed record FormattedContentRun(IContent Content, int Width, BrushedStyle Style = default) : FormattedRun
{
    /// <inheritdoc/>
    public override int CellWidth => Width;
}
