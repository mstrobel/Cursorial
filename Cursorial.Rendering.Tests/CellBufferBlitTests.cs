using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Rendering;

// Blit: copy a view's contents into a surface at a destination rect. The four properties that make
// it usable anywhere other than "everything at the origin" — destination anchor, source window,
// clipping, and pair hygiene — each had a defect when the API was introduced.
public class CellBufferBlitTests
{
    private static CellBuffer Filled(int columns, int rows, string glyph)
    {
        var buffer = new CellBuffer(columns, rows);
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
            buffer.Set(column, row, glyph, CellStyle.Default);
        return buffer;
    }

    private static string Map(CellBuffer buffer)
    {
        var map = new System.Text.StringBuilder();
        for (int row = 0; row < buffer.Rows; row++)
        {
            for (int column = 0; column < buffer.Columns; column++)
                map.Append(buffer[column, row].Grapheme is { Length: > 0 } g ? g[0] : '.');
            if (row + 1 < buffer.Rows) map.Append('/');
        }
        return map.ToString();
    }

    [Fact]
    public void Blit_LandsAtTheDestinationAnchor()
    {
        // The anchor was dropped whenever the destination contained the target rect — every blit
        // landed at (0, 0) regardless of where it was asked to go.
        var source = Filled(2, 2, "X");
        var destination = new CellBuffer(6, 4);

        destination.Blit(source.View(new Rect(0, 0, 2, 2)), new Rect(3, 1, 2, 2));

        Assert.Equal("....../...XX./...XX./......", Map(destination));
    }

    [Fact]
    public void Blit_ReadsTheViewsWindow_NotTheBackingBuffersOrigin()
    {
        // The source was indexed with view-local coordinates against the BACKING buffer, so a view
        // over a sub-rectangle silently copied the buffer's top-left corner instead.
        var source = new CellBuffer(4, 2);
        for (int row = 0; row < 2; row++)
        for (int column = 0; column < 4; column++)
            source.Set(column, row, column < 2 ? "A" : "B", CellStyle.Default);

        var destination = new CellBuffer(2, 2);
        destination.Blit(source.View(new Rect(2, 0, 2, 2)), new Rect(0, 0, 2, 2));

        Assert.Equal("BB/BB", Map(destination));
    }

    [Fact]
    public void Blit_OversizedSource_ClipsInsteadOfThrowing()
    {
        // The loop walked the unclipped source rect and indexed the destination with the source's
        // stride — an IndexOutOfRangeException, or silent row-wrapping corruption.
        var source = Filled(4, 4, "X");
        var destination = new CellBuffer(2, 2);

        var exception = Record.Exception(() => destination.Blit(source.View(new Rect(0, 0, 4, 4)), new Rect(0, 0, 4, 4)));

        Assert.Null(exception);
        Assert.Equal("XX/XX", Map(destination));
    }

    [Fact]
    public void Blit_ClipsAtTheDestinationEdges()
    {
        var source = Filled(3, 3, "X");
        var destination = new CellBuffer(4, 3);

        destination.Blit(source.View(new Rect(0, 0, 3, 3)), new Rect(2, 1, 3, 3));

        Assert.Equal("..../..XX/..XX", Map(destination));
    }

    [Fact]
    public void Blit_WhollyOutsideTheDestination_WritesNothing()
    {
        // It used to stomp the top-left corner instead of no-oping.
        var source = Filled(2, 2, "X");
        var destination = Filled(3, 2, "o");

        destination.Blit(source.View(new Rect(0, 0, 2, 2)), new Rect(9, 9, 2, 2));

        Assert.Equal("ooo/ooo", Map(destination));
    }

    [Fact]
    public void Blit_SplitWidePair_DegradesRatherThanStrandingAHalf()
    {
        // Raw _cells writes bypassed the pair hygiene the indexer enforces, so a copy that cut a
        // wide glyph in half left a WideLeft with no continuation (or vice versa) in the target.
        var source = new CellBuffer(4, 1);
        source.Set(0, 0, "漢", CellStyle.Default);   // occupies columns 0-1
        source.Set(2, 0, "字", CellStyle.Default);   // occupies columns 2-3

        var destination = new CellBuffer(3, 1);
        destination.Blit(source.View(new Rect(0, 0, 4, 1)), new Rect(0, 0, 4, 1));

        // The second glyph's continuation falls outside the destination: it must not be stranded.
        Assert.Equal(CellKind.WideLeft, destination[0, 0].Kind);
        Assert.Equal(CellKind.WideContinuation, destination[1, 0].Kind);
        Assert.NotEqual(CellKind.WideLeft, destination[2, 0].Kind);
    }

    [Fact]
    public void Blit_ContinuationWhoseLeadingHalfWasCut_KeepsThePairsBackground()
    {
        // The mirror of the case above: the copy's LEFT edge lands inside a wide pair, so the
        // destination gets a continuation with no leading half and the indexer blanks it. A
        // continuation carries no style of its own, so that blank would be a terminal-default hole
        // in the middle of the pair's background unless the blit sources the leading half's style —
        // which the cut has left one column outside the copied view.
        var background = Color.FromRgb(90, 30, 30);
        var source = new CellBuffer(6, 1);
        source.Set(1, 0, "中", CellStyle.Default.WithBackground(background));   // pair at columns 1-2

        var destination = new CellBuffer(4, 1);
        destination.Blit(source.View(new Rect(2, 0, 2, 1)), new Rect(0, 0, 2, 1)); // starts on the continuation

        Assert.Equal(CellKind.Single, destination[0, 0].Kind);
        Assert.True(string.IsNullOrEmpty(destination[0, 0].Grapheme));
        Assert.Equal(background, destination[0, 0].Style.Background);
    }

    [Fact]
    public void Blit_WidePairAtTheRectEdge_DoesNotSpillPastTheRect()
    {
        // The indexer's own degrade fires only at the BUFFER edge. When the copied rectangle ends
        // before that, writing a leading half would make the indexer pair-write one column PAST the
        // rectangle — a blit scribbling outside the region it was given.
        var source = new CellBuffer(4, 1);
        source.Set(2, 0, "字", CellStyle.Default);   // a wide glyph at the rect's last column

        var destination = Filled(6, 1, "o");     // roomy: the indexer would happily pair-write
        destination.Blit(source.View(new Rect(0, 0, 4, 1)), new Rect(0, 0, 3, 1));

        Assert.NotEqual(CellKind.WideLeft, destination[2, 0].Kind);   // degraded, not paired
        Assert.Equal("o", destination[3, 0].Grapheme);                // the cell past the rect is untouched
    }

    [Fact]
    public void ViewIndexer_WideLeftAtTheWindowEdge_DoesNotWriteOutsideTheView()
    {
        // A view is a clip: nothing it is handed may land outside its window. The indexer forwards
        // to the buffer's, whose pair-write only degrades at the BUFFER's edge — so a leading half
        // at the view's right edge paired one column past the window, escaping the view entirely.
        // CellBufferView.Set already anchors that degrade on the view's own edge; the indexer must too.
        var backing = Filled(6, 1, "o");
        var window = backing.View(new Rect(1, 0, 2, 1));   // window covers backing columns 1-2

        window[1, 0] = new Cell("字", CellKind.WideLeft, CellStyle.Default);

        Assert.Equal("o", backing[3, 0].Grapheme);                     // outside the window: untouched
        Assert.NotEqual(CellKind.WideContinuation, backing[3, 0].Kind);
        Assert.NotEqual(CellKind.WideLeft, backing[2, 0].Kind);        // degraded instead of paired
    }

    [Fact]
    public void Blit_ThroughAView_HonorsTheViewsOriginAndWindow()
    {
        // CellBufferView.Blit forwards to the buffer; the destination rect is view-local and must be
        // mapped through the view's origin, and clipped to its window.
        var source = Filled(2, 2, "X");
        var backing = new CellBuffer(8, 4);
        var window = backing.View(new Rect(4, 1, 4, 3));

        window.Blit(source.View(new Rect(0, 0, 2, 2)), new Rect(1, 0, 2, 2));

        // View-local (1,0) is backing (5,1).
        Assert.Equal("......../.....XX./.....XX./........", Map(backing));
    }

    [Fact]
    public void Blit_ThroughAView_ClipsToTheWindow()
    {
        var source = Filled(4, 4, "X");
        var backing = Filled(6, 3, "o");
        var window = backing.View(new Rect(1, 0, 2, 2));   // a 2x2 window

        window.Blit(source.View(new Rect(0, 0, 4, 4)), new Rect(0, 0, 4, 4));

        // Only the window's own 2x2 may be written; everything outside stays untouched.
        Assert.Equal("oXXooo/oXXooo/oooooo", Map(backing));
    }
}
