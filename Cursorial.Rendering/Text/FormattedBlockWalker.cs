using System.Collections.Immutable;

namespace Cursorial.Rendering.Text;

/// <summary>
/// Where one <see cref="FormattedBlock"/> lands when its document is painted into a given rect.
/// Produced by <see cref="FormattedBlockWalker"/>; consumed by <see cref="FormattedText.Paint"/> and
/// <see cref="FormattedText.ComputeExtent"/>.
/// </summary>
/// <param name="Block">The block this placement is for.</param>
/// <param name="Column">
/// The anchor column the PAINTER uses — see <see cref="SamplingRect"/> for why this is not always
/// where the block's cells end up.
/// </param>
/// <param name="Row">The block's top row, after inter-block margins and any
/// <see cref="FormattedText.FillEntireBounds"/> re-centring.</param>
/// <param name="Rows">The block's height CLIPPED to the rows still available — what the painter is
/// allowed to draw, which can be fewer rows than <c>Block.Size.Rows</c>.</param>
/// <param name="PaintColumns">
/// How many columns this block's painter covers: <c>Block.Size.Columns</c> for a paragraph or a content
/// block, and the RECT's width for a horizontal rule, whose painter repeats its glyph to the end of the
/// rect it was handed rather than to the end of its own box — so a rule formatted against one budget and
/// painted into a wider or narrower rect draws the rect's width, not <c>Size.Columns</c>. Distinct from
/// <see cref="FormattedBlockWalker.PaintedColumns"/>, which stays on <c>Size.Columns</c> because it feeds
/// the rect <see cref="FormattedText.Paint"/> returns.
/// </param>
/// <param name="SamplingRect">
/// The rect a block- or document-scoped brush samples against. Anchored at <see cref="Column"/> and
/// clamped to at least 1×1 so a degenerate block cannot throw. Two things about it are worth knowing
/// before it is reused:
/// <list type="bullet">
/// <item>for a paragraph it is anchored LEFT regardless of the paragraph's alignment (see
/// <see cref="FormattedText.ComputeAnchorColumn(in Rect, FormattedBlock)"/>), while the paragraph painter
/// lays its lines out at the paragraph's OWN alignment — so for a centred or right-aligned paragraph
/// inside a wider rect this rect does not cover the cells it is sampled for. That divergence is
/// reproduced here deliberately: the corpus case <c>align-center-brushed-wider-than-budget</c> pins it,
/// and correcting it is a separate change from moving the arithmetic.</item>
/// <item>it is <c>Size.Columns</c> wide for a rule as well, while
/// <see cref="FormattedText.PaintHorizontalRule"/> builds the rect its own gradient spans from
/// <see cref="PaintColumns"/> — the two agree only when the rect's width is the budget the rule was
/// formatted against.</item>
/// </list>
/// <see cref="Extent"/> is the un-diverged answer to both.
/// </param>
/// <param name="Extent">
/// Where the block's cells land: anchored by the block's own <see cref="FormattedBlock.Alignment"/>,
/// <see cref="PaintColumns"/> wide (floored at 0, not at 1 — a zero-width block yields an empty rect, which
/// <see cref="Rect.Union"/> treats as the identity) and <see cref="Rows"/> tall.
/// <para>
/// The height is what the painter is BUDGETED, which is not always what it inks. A paragraph clips a band
/// WHOLE when the band is taller than the rows left (<see cref="FormattedText.PaintParagraph"/>'s band loop
/// breaks rather than painting a partial band), so a trailing clipped band leaves rows inside this rect that
/// carry nothing — and its columns inside <c>Size.Columns</c> likewise. Both halves of that over-report come
/// from the same missing fact: which of a paragraph's BANDS placed, which this block-granular walk does not
/// ask. It is bounded by the block's own box and arises where the rect has already truncated the document.
/// </para>
/// <para>
/// Kept rather than narrowed, on the strength of what this rect is FOR — the sampling bounds a document- or
/// preference-scoped brush ramps across. Two reasons. Narrowing it needs line-granular placement, which
/// means either a second copy of the band stacking the paragraph painter already runs or moving that
/// painter onto this walk — a larger change than the extent's consumer asks for. And the budgeted height
/// degrades the more gently of the two under a shrinking rect: for a 1-row line above a 3-row band it walks
/// 4, 3, 2, 1 as rows are taken away, where an ink-exact height would drop 4 → 1 the moment the band stops
/// fitting and re-colour every surviving row at once. A gradient that runs off the bottom of a document the
/// rect has already cut off is the same thing the cut-off text is.
/// </para>
/// </param>
internal readonly record struct FormattedBlockPlacement(
    FormattedBlock Block,
    int Column,
    int Row,
    int Rows,
    int PaintColumns,
    Rect SamplingRect,
    Rect Extent);

/// <summary>
/// The single walk of a <see cref="FormattedText"/>'s blocks against a paint rect: inter-block margin
/// stacking, <see cref="FormattedText.FillEntireBounds"/> vertical re-centring, the row budget and its
/// clipping, per-block anchoring, and the painted-width accumulation that shapes the rect
/// <see cref="FormattedText.Paint"/> returns.
/// </summary>
/// <remarks>
/// <para>
/// A mutable struct enumerator rather than a materialised plan: <see cref="FormattedText.Paint"/> runs
/// per frame per text element, and a per-paint array of placements would be an allocation the walk does
/// not need. Drive it with an explicit <c>while (walker.MoveNext())</c> loop, not <c>foreach</c> — the
/// terminal state (<see cref="Row"/>, <see cref="PaintedColumns"/>) is read AFTER the walk, and
/// <c>foreach</c> iterates a copy.
/// </para>
/// <para>
/// Blocks that clip to zero rows are STEPPED OVER, not yielded: they contribute their margin and their
/// (zero) height to the stack but there is nothing to paint and nothing to sample, exactly as the
/// <c>blockHeight &gt; 0</c> guard in the paint loop this was extracted from.
/// </para>
/// </remarks>
internal struct FormattedBlockWalker
{
    private readonly ImmutableArray<FormattedBlock> _blocks;
    private readonly Rect _bounds;

    private int _index;
    private int _row;
    private int _rowsAvailable;
    private bool _first;
    private Margins _previousMargin;
    private int _paintedColumns;

    internal FormattedBlockWalker(FormattedText text, in Rect bounds)
    {
        _blocks = text.Blocks;
        _bounds = bounds;

        // FillEntireBounds centres the document's box vertically within the rect (the surround is the
        // caller's to clear). The row BUDGET is deliberately left at the full height: re-centring moves
        // where the stack starts without telling it it has fewer rows to work with, which is what the
        // loop this was extracted from did.
        _row = text.FillEntireBounds
                   ? bounds.Row + (bounds.Rows - text.Size.Rows) / 2
                   : bounds.Row;

        _rowsAvailable = bounds.Rows;
        _first = true;
        _previousMargin = Margins.Zero;
    }

    /// <summary>The placement produced by the most recent successful <see cref="MoveNext"/>.</summary>
    public FormattedBlockPlacement Current { get; private set; }

    /// <summary>
    /// The row cursor: the first row BELOW everything placed so far. After the walk finishes this is the
    /// document's bottom edge, including any margin consumed by a block that then ran out of rows.
    /// </summary>
    public int Row => _row;

    /// <summary>
    /// The widest placed block's <c>Size.Columns</c>, unclipped. Blocks stepped over for want of rows do
    /// not contribute.
    /// </summary>
    public int PaintedColumns => _paintedColumns;

    /// <summary>Advances to the next block that has rows to occupy; false once the walk is done.</summary>
    public bool MoveNext()
    {
        while (_index < _blocks.Length)
        {
            if (_rowsAvailable <= 0) return false;

            var block = _blocks[_index++];

            // The first block's top margin is suppressed — the document anchors flush to the rect —
            // and between blocks the two facing margins collapse to the larger of the pair.
            int marginTop = _first ? 0 : Math.Max(block.Margin.Top, _previousMargin.Bottom);
            _row += marginTop;
            _rowsAvailable -= marginTop;
            if (_rowsAvailable <= 0) return false;

            int rows = Math.Min(block.Size.Rows, _rowsAvailable);

            if (rows > 0)
            {
                int column = FormattedText.ComputeAnchorColumn(_bounds, block);
                int inkColumn = FormattedText.ComputeAnchorColumn(_bounds, block.Size.Columns, block.Alignment);

                // A rule's painter runs to the end of the RECT it was handed rather than to the end of its
                // own box, so its ink is the rect's width wherever that differs from the budget it was
                // formatted against. The other two block kinds paint inside their box: a content block is
                // handed Rect(…, Size.Columns, …), and a paragraph anchors each line by line.Columns, which
                // FormatParagraphCore never lets exceed Size.Columns.
                int paintColumns = block is FormattedHorizontalRule ? _bounds.Columns : block.Size.Columns;

                Current = new FormattedBlockPlacement(
                    block,
                    column,
                    _row,
                    rows,
                    paintColumns,
                    new Rect(column, _row, Math.Max(1, block.Size.Columns), Math.Max(1, rows)),
                    // Floored at 0, not 1: a zero-width block has no extent to contribute, and Size does not
                    // enforce its own "non-negative" documentation — a negative width reaches here from an
                    // IContent that mis-measures, and Rect's ctor would throw on it. The sampling rect's
                    // Math.Max(1, …) absorbed that before this walk existed; the extent must not un-absorb it.
                    new Rect(inkColumn, _row, Math.Max(0, paintColumns), rows));

                _paintedColumns = Math.Max(_paintedColumns, block.Size.Columns);
            }

            _row += rows;
            _rowsAvailable -= rows;
            _first = false;
            _previousMargin = block.Margin;

            if (rows > 0) return true;
        }

        return false;
    }
}
