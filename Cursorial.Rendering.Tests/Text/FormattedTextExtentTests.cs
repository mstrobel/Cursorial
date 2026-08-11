using System.Collections.Immutable;

using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Rendering.Text;

/// <summary>
/// <see cref="FormattedText.ComputeExtent"/> and the walk behind it.
/// </summary>
/// <remarks>
/// <para>
/// The extent is what the brush ladder samples (docs/text-carrier-design.md): both the document and
/// preference rungs ramp across <see cref="FormattedText.ComputeExtent"/>, the painter's own fold uses
/// the same rect for the document rung, and the block rung samples the walk's per-block
/// <c>Extent</c> — the rect the declaration's cells actually occupy, not the element box the paint was
/// handed. A wrong extent now moves rendered frames, and most of these still check it against a PAINTED
/// buffer rather than against re-derived arithmetic: if the walk and the painter ever drift, the ink
/// moves and the assertion fails.
/// </para>
/// <para>
/// It was not always the sampled rect: the walk used to carry a second, Left-forced <c>SamplingRect</c>
/// beside it (every paragraph's rect anchored at the rect's left edge while the paragraph's lines anchor
/// at the paragraph's own alignment), and a test here pinned the disagreement so that correcting it would
/// read as a change in behaviour. It did: the block rung's re-aim is pinned behaviourally in
/// Cursorial.Drawing.Tests (<c>BlockStyleBrush_CentredParagraph_SamplesTheInk</c>) and by the corpus case
/// <c>brush-block-style-scope-centred</c>; what remains here is the walker-level half that stayed true.
/// </para>
/// </remarks>
public class FormattedTextExtentTests
{
    private static FormattedText Format(RichText doc, int columns, bool fillEntireBounds = false)
        => new TextFormatter().Format(doc, columns, fillEntireBounds: fillEntireBounds);

    /// <summary>Paints into a buffer that ends where <paramref name="bounds"/> does and returns the smallest
    /// rect containing every non-blank cell — <see cref="Rect.Empty"/> when nothing was drawn. Pass
    /// <paramref name="surface"/> to paint into a LARGER buffer, which is how a painter that writes outside
    /// the rect it was handed becomes visible rather than being swallowed by the buffer's edge.</summary>
    private static Rect PaintedInk(FormattedText text, in Rect bounds, Size? surface = null)
    {
        var buffer = new CellBuffer(surface?.Columns ?? bounds.ColumnEnd, surface?.Rows ?? bounds.RowEnd);
        text.Paint(buffer, bounds, OutputCapabilities.None);

        int minColumn = int.MaxValue, minRow = int.MaxValue, maxColumn = int.MinValue, maxRow = int.MinValue;

        for (int row = 0; row < buffer.Rows; row++)
        for (int column = 0; column < buffer.Columns; column++)
        {
            if (string.IsNullOrWhiteSpace(buffer[column, row].Grapheme))
                continue;

            minColumn = Math.Min(minColumn, column);
            minRow    = Math.Min(minRow, row);
            maxColumn = Math.Max(maxColumn, column);
            maxRow    = Math.Max(maxRow, row);
        }

        return minColumn == int.MaxValue
                   ? Rect.Empty
                   : new Rect(minColumn, minRow, maxColumn - minColumn + 1, maxRow - minRow + 1);
    }

    private static RichText OneParagraph(string text, TextAlignment alignment)
        => new RichTextBuilder().Paragraph(alignment: alignment, margin: Margins.Zero).Run(text).Build();

    // ---- The extent follows the block's own alignment ----

    [Theory]
    [InlineData(TextAlignment.Left,   0)]
    [InlineData(TextAlignment.Center, 4)]   // slack 8, halved
    [InlineData(TextAlignment.Right,  8)]
    public void Extent_OneParagraph_AnchorsAtItsOwnAlignment(TextAlignment alignment, int expectedColumn)
    {
        var text = Format(OneParagraph("abc", alignment), 11);
        var bounds = new Rect(0, 0, 11, 1);

        Assert.Equal(new Rect(expectedColumn, 0, 3, 1), text.ComputeExtent(bounds));
        Assert.Equal(text.ComputeExtent(bounds), PaintedInk(text, bounds));
    }

    [Fact]
    public void Extent_HonoursTheBoundsOrigin()
    {
        var text = Format(OneParagraph("abc", TextAlignment.Center), 11);

        // Anchored inside the rect, not at the buffer origin: slack 8 halved, plus the rect's own column.
        Assert.Equal(new Rect(9, 2, 3, 1), text.ComputeExtent(new Rect(5, 2, 11, 1)));
    }

    [Fact]
    public void Extent_CentredParagraph_IsWhereTheGlyphsLand()
    {
        // The walker-level half of the old divergence pin. The walk used to yield a Left-forced
        // SamplingRect ((0,0,3,1) here) beside this rect; that rect is deleted and a block-scoped brush
        // samples THIS one. The behavioural half — a block-declared gradient ramping across the centred
        // ink — lives in Cursorial.Drawing.Tests (BlockStyleBrush_CentredParagraph_SamplesTheInk).
        var text = Format(OneParagraph("abc", TextAlignment.Center), 11);
        var bounds = new Rect(0, 0, 11, 1);

        var walker = new FormattedBlockWalker(text, bounds);
        Assert.True(walker.MoveNext());

        Assert.Equal(new Rect(4, 0, 3, 1), walker.Current.Extent);         // where the glyphs land
        Assert.Equal(walker.Current.Extent, PaintedInk(text, bounds));
    }

    // ---- Within a paragraph the union collapses to the widest line ----

    [Theory]
    [InlineData(TextAlignment.Left)]
    [InlineData(TextAlignment.Center)]
    [InlineData(TextAlignment.Right)]
    public void Extent_WithinOneParagraph_IsTheUnionOfItsLines(TextAlignment alignment)
    {
        // Wraps to "wwww" (4) and "aa" (2) — one alignment, two widths, so the widest line's extent is
        // expected to contain the narrower one. Re-derived here from the LINES rather than the block, so
        // the claim is checked rather than restated.
        var text = Format(OneParagraph("wwww aa", alignment), 4);
        var bounds = new Rect(0, 0, 12, 4);
        var paragraph = Assert.IsType<FormattedParagraph>(Assert.Single(text.Blocks));

        Assert.Equal(2, paragraph.Lines.Length);

        var union = Rect.Empty;
        int row = 0;

        foreach (var line in paragraph.Lines)
        {
            int column = FormattedText.ComputeAnchorColumn(bounds, line.Columns, alignment);
            union = union.Union(new Rect(column, row, line.Columns, line.Rows));
            row += line.Rows;
        }

        var widest = new Rect(FormattedText.ComputeAnchorColumn(bounds, 4, alignment), 0, 4, 2);

        Assert.Equal(union, widest);                        // collapses to the widest line
        Assert.Equal(union, text.ComputeExtent(bounds));
        Assert.Equal(union, PaintedInk(text, bounds));
    }

    // ---- Across blocks it does not ----

    [Fact]
    public void Extent_MixedAlignmentBlocks_SpansLeftBlockStartToRightBlockEnd()
    {
        // Neither block's own rect covers this: the left block ends at column 3 and the right one starts
        // at column 6, so the document spans strictly more than the widest block (4).
        var doc = new RichTextBuilder()
                  .Paragraph(alignment: TextAlignment.Left, margin: Margins.Zero).Run("abc")
                  .Paragraph(alignment: TextAlignment.Right, margin: Margins.Zero).Run("wxyz")
                  .Build();

        var text = Format(doc, 10);
        var bounds = new Rect(0, 0, 10, 2);
        var extent = text.ComputeExtent(bounds);

        Assert.Equal(new Rect(0, 0, 10, 2), extent);
        Assert.Equal(new Size(4, 2), text.Size);            // the document's own footprint is narrower
        Assert.Equal(extent, PaintedInk(text, bounds));
    }

    [Fact]
    public void Extent_MixedAlignmentBlocks_CoversTheMarginRowBetweenThem()
    {
        // Default paragraph margins put a blank row between the two blocks. It carries no ink, but a
        // bounding box that skipped it would not be a rectangle.
        var doc = new RichTextBuilder()
                  .Paragraph(alignment: TextAlignment.Left).Run("abc")
                  .Paragraph(alignment: TextAlignment.Right).Run("wxyz")
                  .Build();

        var text = Format(doc, 10);
        var bounds = new Rect(0, 0, 10, 3);

        Assert.Equal(new Rect(0, 0, 10, 3), text.ComputeExtent(bounds));
        Assert.Equal(new Rect(0, 0, 10, 3), PaintedInk(text, bounds));
    }

    [Fact]
    public void Extent_CentredBlocksOfDifferentWidths_IsTheWiderOne()
    {
        // Same alignment across blocks: the union does collapse, and the collapse is worth pinning next to
        // the mixed-alignment case so the two shapes are not confused for one rule.
        var doc = new RichTextBuilder()
                  .Paragraph(alignment: TextAlignment.Center, margin: Margins.Zero).Run("ab")
                  .Paragraph(alignment: TextAlignment.Center, margin: Margins.Zero).Run("wxyz")
                  .Build();

        var text = Format(doc, 10);
        var bounds = new Rect(0, 0, 10, 2);

        Assert.Equal(new Rect(3, 0, 4, 2), text.ComputeExtent(bounds));
        Assert.Equal(new Rect(3, 0, 4, 2), PaintedInk(text, bounds));
    }

    // ---- A rule is as wide as the rect it is painted into ----

    [Fact]
    public void Extent_HorizontalRule_PaintedWiderThanItsBudget_SpansTheRect()
    {
        // A rule's Size is its FORMAT budget (TextFormatter.cs:190), but the painter repeats its glyph
        // across the PAINT rect (FormattedText.PaintHorizontalRule takes the rect's columns). Formatted
        // at 4 and painted into 10, the ink runs 6 columns past Size.Columns, so an extent taken from
        // Size would under-report the rule by that much.
        var doc = new RichTextBuilder().HorizontalRule(glyph: "-", margin: Margins.Zero).Build();
        var text = new TextFormatter().Format(doc, 4);
        var bounds = new Rect(0, 0, 10, 1);

        Assert.Equal(new Size(4, 1), text.Blocks[0].Size);
        Assert.Equal(new Rect(0, 0, 10, 1), PaintedInk(text, bounds));
        Assert.Equal(new Rect(0, 0, 10, 1), text.ComputeExtent(bounds));
    }

    [Fact]
    public void Extent_HorizontalRule_PaintedNarrowerThanItsBudget_StopsAtTheRect()
    {
        // The other direction of the same rule: the painter's end is the rect's, so a rule formatted at
        // 10 and painted into 4 draws four glyphs — an extent taken from Size would claim six columns
        // outside the rect entirely.
        var doc = new RichTextBuilder().HorizontalRule(glyph: "-", margin: Margins.Zero).Build();
        var text = new TextFormatter().Format(doc, 10);
        var bounds = new Rect(0, 0, 4, 1);

        Assert.Equal(new Size(10, 1), text.Blocks[0].Size);
        Assert.Equal(new Rect(0, 0, 4, 1), PaintedInk(text, bounds));
        Assert.Equal(new Rect(0, 0, 4, 1), text.ComputeExtent(bounds));
    }

    [Fact]
    public void Extent_CentredHorizontalRule_ReportsTheOverflowThePainterProduces()
    {
        // Alignment moves a rule's start column but not its length, so a rule anchored anywhere other
        // than the rect's left edge runs off the rect's right edge — 3 columns of slack here, and the
        // glyphs land in columns 3..12 of a 10-column rect. The extent reports where the cells go
        // rather than clamping, which is why the surface below is wider than the rect: clamped to the
        // rect the assertion would pass while hiding that the painter wrote outside it.
        var doc = new RichTextBuilder()
                  .HorizontalRule(glyph: "-", alignment: TextAlignment.Center, margin: Margins.Zero)
                  .Build();

        var text = new TextFormatter().Format(doc, 4);
        var bounds = new Rect(0, 0, 10, 1);

        Assert.Equal(new Rect(3, 0, 10, 1), PaintedInk(text, bounds, surface: new Size(16, 1)));
        Assert.Equal(new Rect(3, 0, 10, 1), text.ComputeExtent(bounds));
    }

    // ---- Vertical placement ----

    [Fact]
    public void Extent_FillEntireBounds_IsVerticallyCentred()
    {
        // FillEntireBounds background-fills the whole rect and centres the document's box within it; the
        // extent has to follow the centring, or a document brush would sample rows the text does not occupy.
        var text = Format(OneParagraph("abc", TextAlignment.Center), 11, fillEntireBounds: true);
        var bounds = new Rect(0, 0, 11, 5);

        Assert.Equal(new Rect(4, 2, 3, 1), text.ComputeExtent(bounds));
        Assert.Equal(new Rect(4, 2, 3, 1), PaintedInk(text, bounds));
    }

    [Fact]
    public void Extent_ClipsRowsToTheRowBudget()
    {
        // Three one-row lines, room for two. The third line falls outside the budget, and with every
        // band one row tall the two the painter reaches are exactly the two the extent reports.
        var text = Format(OneParagraph("aa bb cc", TextAlignment.Left), 2);
        var bounds = new Rect(0, 0, 6, 2);

        Assert.Equal(3, ((FormattedParagraph) text.Blocks[0]).Lines.Length);
        Assert.Equal(new Rect(0, 0, 2, 2), text.ComputeExtent(bounds));
        Assert.Equal(new Rect(0, 0, 2, 2), PaintedInk(text, bounds));
    }

    /// <summary>A one-row line above a three-row band — the shape a sized or FIGlet run produces, built by
    /// hand so the test needs no font. The band's run is one row of ink sitting on the band's bottom row
    /// (<see cref="VerticalTextAlignment.Bottom"/>, the default).</summary>
    private static FormattedText TallBandParagraph()
    {
        var first = new FormattedLine([new FormattedTextRun("abc", default)], 3, false);
        var band = new FormattedLine([new FormattedTextRun("wxyz", default)], 4, false, Rows: 3);
        var paragraph = new FormattedParagraph([first, band], new Size(4, 4), TextAlignment.Left, false);

        return new FormattedText([paragraph], new Size(4, 4), 4);
    }

    [Fact]
    public void Extent_WholeBandsThatFit_AgreesWithTheInk()
    {
        // 1 + 3 rows into a budget of 4: both bands place, and the band's run bottom-aligns onto row 3.
        var text = TallBandParagraph();
        var bounds = new Rect(0, 0, 6, 4);

        Assert.Equal(new Rect(0, 0, 4, 4), text.ComputeExtent(bounds));
        Assert.Equal(new Rect(0, 0, 4, 4), PaintedInk(text, bounds));
    }

    [Fact]
    public void Extent_TrailingClippedBand_OverReportsTheRowsAndColumnsItWouldHaveFilled()
    {
        // One row short of the band. The painter clips a band WHOLE (FormattedText.PaintParagraph breaks on
        // `line.Rows > rowsLeft`), so rows 1-2 are budgeted, entered in the block's height, and never
        // written to; the block's Size.Columns still counts the clipped band's 4 columns as well.
        //
        // Pinned rather than fixed: the extent is block-granular, and both halves of the over-report come
        // from the same missing fact — which of a paragraph's BANDS placed. Narrowing it needs a
        // line-granular walk, which would be a second implementation of the band stacking the painter
        // already runs unless the painter consumes it too. Bounded meanwhile: the over-report never exceeds
        // the block's own box, and it only fires on a document the rect has already truncated.
        var text = TallBandParagraph();
        var bounds = new Rect(0, 0, 6, 3);

        Assert.Equal(new Rect(0, 0, 4, 3), text.ComputeExtent(bounds));
        Assert.Equal(new Rect(0, 0, 3, 1), PaintedInk(text, bounds));
    }

    [Fact]
    public void Extent_BlockWithNoRoomLeft_DoesNotContribute()
    {
        // The second block never places, so a right-aligned block that would have widened the document to
        // the full rect leaves the extent at the first block's own 3 columns.
        var doc = new RichTextBuilder()
                  .Paragraph(alignment: TextAlignment.Left, margin: Margins.Zero).Run("abc")
                  .Paragraph(alignment: TextAlignment.Right, margin: Margins.Zero).Run("wxyz")
                  .Build();

        var text = Format(doc, 10);
        var bounds = new Rect(0, 0, 10, 1);

        Assert.Equal(new Rect(0, 0, 3, 1), text.ComputeExtent(bounds));
        Assert.Equal(new Rect(0, 0, 3, 1), PaintedInk(text, bounds));
    }

    // ---- Degenerate documents ----

    [Fact]
    public void Extent_EmptyDocument_IsZeroSizedAtTheBoundsOrigin()
        => Assert.Equal(new Rect(3, 4, 0, 0), FormattedText.Empty.ComputeExtent(new Rect(3, 4, 10, 5)));

    [Fact]
    public void Extent_NegativeBlockWidth_IsFlooredRatherThanThrown()
    {
        // Not reachable through TextFormatter, but FormattedText and FormattedBlock are both publicly
        // constructible and Size does not enforce the non-negativity its own docs claim. Before the walk
        // existed the sampling rect's Math.Max(1, …) absorbed a negative width; the extent must not turn
        // that into an exception out of Paint.
        var block = new FormattedParagraph(ImmutableArray<FormattedLine>.Empty, new Size(-1, 1),
                                           TextAlignment.Left, false);
        var text = new FormattedText([block], new Size(0, 1), 10);
        var bounds = new Rect(0, 0, 10, 3);

        // Floored to zero columns the block is EMPTY, so Union drops it and the document has no extent —
        // which is the honest answer for a block that occupies no cells.
        Assert.Equal(new Rect(0, 0, 0, 0), text.ComputeExtent(bounds));
        text.Paint(new CellBuffer(10, 3), bounds, OutputCapabilities.None);
    }

    [Fact]
    public void Extent_BoundsWithNoRows_IsZeroSizedAtTheBoundsOrigin()
    {
        var text = Format(OneParagraph("abc", TextAlignment.Center), 11);

        Assert.Equal(new Rect(2, 7, 0, 0), text.ComputeExtent(new Rect(2, 7, 11, 0)));
    }

    // ---- The walk is the one the painter runs ----

    [Fact]
    public void Walk_ReproducesThePaintedRect()
    {
        // Paint's return value is built entirely from the walk's terminal state, so a walk that stacked
        // rows differently from the painter would show up here.
        var doc = new RichTextBuilder()
                  .Paragraph(alignment: TextAlignment.Left).Run("abc")
                  .Paragraph(alignment: TextAlignment.Right).Run("wxyz")
                  .Build();

        var text = Format(doc, 10);
        var bounds = new Rect(0, 0, 10, 6);

        var buffer = new CellBuffer(10, 6);
        var painted = text.Paint(buffer, bounds, OutputCapabilities.None);

        var walker = new FormattedBlockWalker(text, bounds);
        int blocks = 0;
        while (walker.MoveNext()) blocks++;

        Assert.Equal(2, blocks);
        Assert.Equal(new Rect(0, 0, Math.Min(walker.PaintedColumns, bounds.Columns), walker.Row - bounds.Row),
                     painted);
    }
}
