using System.Buffers;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Rendering;

public class CellBufferViewTests
{
    // ---- Construction & basic geometry ----

    [Fact]
    public void Constructor_NullBuffer_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CellBufferView(null!));
    }

    [Fact]
    public void FullView_HasBufferDimensions()
    {
        var buf = new CellBuffer(80, 24);
        var view = buf.AsView();

        Assert.Equal(80, view.Columns);
        Assert.Equal(24, view.Rows);
        Assert.Equal(0, view.OffsetRow);
        Assert.Equal(0, view.OffsetColumn);
        Assert.Same(buf, view.Buffer);
        Assert.False(view.IsEmpty);
    }

    [Fact]
    public void View_ClipsToBufferBounds()
    {
        var buf = new CellBuffer(10, 10);

        // Request a view extending past the buffer's right and bottom edges.
        var view = buf.View(8, 8, 10, 10);

        // Offset honored, dimensions clipped to what fits.
        Assert.Equal(8, view.OffsetRow);
        Assert.Equal(8, view.OffsetColumn);
        Assert.Equal(2, view.Columns);
        Assert.Equal(2, view.Rows);
    }

    [Fact]
    public void View_OffsetEntirelyOutsideBuffer_ProducesEmptyView()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(20, 20, 5, 5);

        Assert.True(view.IsEmpty);
        Assert.Equal(0, view.Columns);
        Assert.Equal(0, view.Rows);
    }

    [Fact]
    public void View_NegativeOffsetsClampedToZero()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(-5, -5, 8, 8);

        Assert.Equal(0, view.OffsetRow);
        Assert.Equal(0, view.OffsetColumn);
        // The 8x8 request was anchored at -5,-5 then clamped — its remaining extent reaching
        // back into the buffer is 3x3 (from origin to -5+8 = 3).
        Assert.Equal(3, view.Columns);
        Assert.Equal(3, view.Rows);
    }

    [Fact]
    public void Contains_ReturnsTrueForInBoundsCoords()
    {
        var view = new CellBuffer(10, 10).View(2, 2, 5, 5);
        Assert.True(view.Contains(0, 0));
        Assert.True(view.Contains(4, 4));
        Assert.False(view.Contains(0, 5));
        Assert.False(view.Contains(5, 0));
        Assert.False(view.Contains(0, -1));
    }

    // ---- Coordinate translation via Set ----

    [Fact]
    public void Set_AtViewOrigin_WritesAtBufferOffset()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(4, 3, 5, 5);

        view.Set(0, 0, "X", Style.Default with { Foreground = Color.FromRgb(255, 0, 0) });

        Assert.Equal("X", buf[4, 3].Grapheme);
        // Verify nothing else got written.
        Assert.Null(buf[0, 0].Grapheme);
        Assert.Null(buf[4, 2].Grapheme);
    }

    [Fact]
    public void Set_TranslatesByOffsetForEachCell()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(4, 3, 5, 5);

        view.Set(3, 2, "A", default);

        Assert.Equal("A", buf[7, 5].Grapheme);
    }

    // ---- Clipping ----

    [Fact]
    public void Set_OutsideViewBounds_SilentlyDropped()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(4, 3, 5, 5);

        // Past the right edge of the view.
        int written = view.Set(5, 0, "X", default);
        Assert.Equal(0, written);
        Assert.Null(buf[9, 3].Grapheme);

        // Past the bottom edge.
        written = view.Set(0, 5, "Y", default);
        Assert.Equal(0, written);

        // Negative.
        written = view.Set(0, -1, "Z", default);
        Assert.Equal(0, written);
    }

    [Fact]
    public void Indexer_OutsideViewBounds_Throws()
    {
        var view = new CellBuffer(10, 10).View(4, 3, 5, 5);

        Assert.Throws<ArgumentOutOfRangeException>(() => view[0, 5]);
        Assert.Throws<ArgumentOutOfRangeException>(() => view[5, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => view[0, -1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => view[-1, 0] = default);
    }

    [Fact]
    public void Indexer_InBounds_ReadsAndWritesAtTranslatedCoords()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(4, 3, 5, 5);

        view[2, 1] = new Cell("Q", CellKind.Single, Style.Default);
        Assert.Equal("Q", buf[6, 4].Grapheme);
        Assert.Equal("Q", view[2, 1].Grapheme);
    }

    // ---- Wide cells crossing the view edge ----

    [Fact]
    public void Set_WideGlyphAtViewRightEdge_DegradesToBlank()
    {
        var buf = new CellBuffer(20, 1);
        var view = buf.View(5, 0, 10, 1); // columns 5..14

        // Place a wide glyph at the last cell of the view (column 9 in view, 14 in buffer).
        // No room for the right half within the view — should degrade.
        int written = view.Set(9, 0, "あ", default); // East-Asian wide
        Assert.Equal(1, written);

        // The wide glyph wasn't placed; the buffer cell at column 14 is a blank single, not a
        // WideLeft.
        Assert.NotEqual(CellKind.WideLeft, buf[14, 0].Kind);
        // The next column (column 15 in buffer, past the view) must NOT be a continuation.
        Assert.NotEqual(CellKind.WideContinuation, buf[15, 0].Kind);
    }

    [Fact]
    public void Set_WideGlyphInsideViewRightEdge_FitsAndOccupiesTwoCells()
    {
        var buf = new CellBuffer(20, 1);
        var view = buf.View(5, 0, 10, 1);

        // Place at view column 8 — there's room for both halves (columns 8 and 9 = buffer 13, 14).
        int written = view.Set(8, 0, "あ", default);
        Assert.Equal(2, written);

        Assert.Equal(CellKind.WideLeft, buf[13, 0].Kind);
        Assert.Equal(CellKind.WideContinuation, buf[14, 0].Kind);
    }

    // ---- Sub-views ----

    [Fact]
    public void SubView_OffsetsAreCumulative()
    {
        var buf = new CellBuffer(20, 20);
        var outer = buf.View(3, 2, 10, 10);
        var inner = outer.View(1, 1, 5, 5);

        Assert.Equal(3, inner.OffsetRow);    // 2 + 1
        Assert.Equal(4, inner.OffsetColumn); // 3 + 1
        Assert.Equal(5, inner.Columns);
        Assert.Equal(5, inner.Rows);

        inner.Set(0, 0, "Z", default);
        Assert.Equal("Z", buf[4, 3].Grapheme);
    }

    [Fact]
    public void SubView_ClipsToParentBounds()
    {
        var buf = new CellBuffer(20, 20);
        var outer = buf.View(3, 2, 10, 10);   // covers buffer rows 2..11, cols 3..12.
        var inner = outer.View(8, 8, 10, 10); // requested 10x10 from (8,8) — extends past parent.

        // Inner is clipped against parent's bounds, not the buffer's.
        Assert.Equal(10, inner.OffsetRow);    // 2 + 8
        Assert.Equal(11, inner.OffsetColumn); // 3 + 8
        Assert.Equal(2, inner.Columns);       // parent had 10 columns, 8 consumed -> 2 left
        Assert.Equal(2, inner.Rows);
    }

    [Fact]
    public void SubView_OutsideParentBounds_IsEmpty()
    {
        var outer = new CellBuffer(20, 20).View(3, 2, 10, 10);
        var inner = outer.View(20, 20, 5, 5);

        Assert.True(inner.IsEmpty);

        // No-op for writes — doesn't reach the buffer.
        Assert.Equal(0, inner.Set(0, 0, "X", default));
    }

    // ---- Fill / Clear scoped to view ----

    [Fact]
    public void Fill_ReplacesOnlyCellsInsideView()
    {
        var buf = new CellBuffer(10, 10);
        // Pre-seed cells outside the view's region so we can verify they're untouched.
        buf[0, 0] = new Cell("X", CellKind.Single, Style.Default);
        buf[9, 9] = new Cell("Y", CellKind.Single, Style.Default);

        var view = buf.View(3, 3, 4, 4);
        view.Fill(new Cell(".", CellKind.Single, Style.Default));

        // Inside the view: replaced.
        Assert.Equal(".", buf[3, 3].Grapheme);
        Assert.Equal(".", buf[6, 6].Grapheme);

        // Outside: untouched.
        Assert.Equal("X", buf[0, 0].Grapheme);
        Assert.Equal("Y", buf[9, 9].Grapheme);
        Assert.Null(buf[3, 2].Grapheme); // immediately above the view
    }

    [Fact]
    public void Clear_ResetsOnlyCellsInsideView()
    {
        var buf = new CellBuffer(10, 10);
        buf.Fill(new Cell("X", CellKind.Single, Style.Default));

        var view = buf.View(3, 3, 4, 4);
        view.Clear();

        // Inside the view: blank.
        Assert.Null(buf[3, 3].Grapheme);
        Assert.Null(buf[6, 6].Grapheme);

        // Outside: still "X".
        Assert.Equal("X", buf[0, 0].Grapheme);
        Assert.Equal("X", buf[3, 2].Grapheme);
        Assert.Equal("X", buf[3, 7].Grapheme); // immediately below the view
    }

    // ---- Cursor accessors ----

    [Fact]
    public void CursorRow_GetTranslatesFromBufferCoords()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(4, 3, 5, 5);
        buf.CursorRow = 5;
        buf.CursorColumn = 6;

        Assert.Equal(2, view.CursorRow);    // 5 - 3
        Assert.Equal(2, view.CursorColumn); // 6 - 4
    }

    [Fact]
    public void CursorRow_GetReportsOutOfViewMath_WithoutThrowing()
    {
        // The buffer cursor may be set outside the view by another widget. The getter returns
        // the literal math — negative or past-end — so the caller can inspect.
        var buf = new CellBuffer(10, 10);
        var view = buf.View(4, 3, 5, 5);
        buf.CursorRow = 0;
        Assert.Equal(-3, view.CursorRow);
    }

    [Fact]
    public void CursorRow_SetInsideView_TranslatesAndWrites()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(4, 3, 5, 5);
        view.CursorRow = 2;
        view.CursorColumn = 3;

        Assert.Equal(5, buf.CursorRow);
        Assert.Equal(7, buf.CursorColumn);
    }

    [Fact]
    public void CursorRow_SetOutsideView_Throws()
    {
        var view = new CellBuffer(10, 10).View(4, 3, 5, 5);

        Assert.Throws<ArgumentOutOfRangeException>(() => view.CursorRow = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => view.CursorRow = 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => view.CursorColumn = 5);
    }

    [Fact]
    public void CursorVisibleAndShape_ForwardToBuffer()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(2, 2, 5, 5);
        view.CursorVisible = false;
        view.CursorShape = CursorShape.SteadyBar;

        Assert.False(buf.CursorVisible);
        Assert.Equal(CursorShape.SteadyBar, buf.CursorShape);
    }

    // ---- Blending stack pass-through ----

    [Fact]
    public void PushPopBlendingMode_ForwardsToBuffer()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(2, 2, 5, 5);

        view.PushBlendingMode(BlendingModes.Multiply);
        Assert.Same(BlendingModes.Multiply, buf.CurrentBlendingMode);
        Assert.Same(BlendingModes.Multiply, view.CurrentBlendingMode);

        view.PopBlendingMode();
        Assert.Same(BlendingModes.Default, buf.CurrentBlendingMode);
    }

    // ---- Fragments translated + view-clipped ----

    [Fact]
    public void AddFragment_TranslatesAnchorToBufferCoords()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(4, 3, 5, 5);
        var fragment = new TestFragment();

        bool added = view.AddFragment(1, 1, fragment);

        Assert.True(added);
        Assert.True(buf.Fragments.ContainsKey((5, 4))); // (col 4+1, row 3+1)
    }

    [Fact]
    public void AddFragment_OutsideView_NotRegistered()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(4, 3, 5, 5);

        bool added = view.AddFragment(0, 5, new TestFragment()); // row=5 is past view's 5 rows

        Assert.False(added);
        Assert.Empty(buf.Fragments);
    }

    [Fact]
    public void RemoveFragment_TranslatesAnchor()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(4, 3, 5, 5);
        view.AddFragment(2, 2, new TestFragment());

        Assert.True(view.RemoveFragment(2, 2));
        Assert.Empty(buf.Fragments);
    }

    // ---- Dirty regions translated + clipped to view ----

    [Fact]
    public void MarkDirty_TranslatesAndClipsToView()
    {
        var buf = new CellBuffer(20, 20);
        var view = buf.View(3, 2, 10, 10);

        // View-local rect (1, 1) with 5x5 -> buffer rect (3, 4) with 5x5.
        view.MarkDirty(1, 1, 5, 5);

        Assert.Single(buf.DirtyRegions);
        var dirty = buf.DirtyRegions[0];
        Assert.Equal(3, dirty.Row);
        Assert.Equal(4, dirty.Column);
        Assert.Equal(5, dirty.Columns);
        Assert.Equal(5, dirty.Rows);
    }

    [Fact]
    public void MarkDirty_RegionExtendingPastView_ClippedToViewRect()
    {
        var buf = new CellBuffer(20, 20);
        var view = buf.View(3, 2, 10, 10);

        // View-local rect (8, 8) with 5x5 -> would reach (13, 13) in view space, clipped to (10, 10).
        // Translates to buffer rect (10, 11) of size 2x2.
        view.MarkDirty(8, 8, 5, 5);

        Assert.Single(buf.DirtyRegions);
        var dirty = buf.DirtyRegions[0];
        Assert.Equal(10, dirty.Row);
        Assert.Equal(11, dirty.Column);
        Assert.Equal(2, dirty.Columns);
        Assert.Equal(2, dirty.Rows);
    }

    [Fact]
    public void MarkDirty_RegionEntirelyOutsideView_DroppedSilently()
    {
        var buf = new CellBuffer(20, 20);
        var view = buf.View(3, 2, 10, 10);

        view.MarkDirty(20, 20, 5, 5);
        Assert.Empty(buf.DirtyRegions);
    }

    // ---- Rect-scoped Fill / Clear on CellBuffer directly (not via view) ----

    [Fact]
    public void CellBuffer_FillRect_FillsOnlyTheGivenRect()
    {
        var buf = new CellBuffer(10, 10);
        buf.Fill(new Rect(3, 3, 4, 4), new Cell(".", CellKind.Single, Style.Default));

        Assert.Equal(".", buf[3, 3].Grapheme);
        Assert.Equal(".", buf[6, 6].Grapheme);
        Assert.Null(buf[3, 2].Grapheme); // just above
        Assert.Null(buf[2, 3].Grapheme); // just left
        Assert.Null(buf[3, 7].Grapheme); // just below
    }

    [Fact]
    public void CellBuffer_ClearCells_ResetsOnlyTheGivenRect()
    {
        var buf = new CellBuffer(10, 10);
        buf.Fill(new Cell("X", CellKind.Single, Style.Default));

        buf.ClearCells(new Rect(3, 3, 4, 4));

        Assert.Null(buf[3, 3].Grapheme);
        Assert.Null(buf[6, 6].Grapheme);
        Assert.Equal("X", buf[3, 2].Grapheme);
        Assert.Equal("X", buf[3, 7].Grapheme);
    }

    [Fact]
    public void CellBuffer_FillRect_OutOfBufferClipped()
    {
        var buf = new CellBuffer(10, 10);
        // Rect extends past buffer; only the in-buffer slice fills.
        buf.Fill(new Rect(8, 8, 5, 5), new Cell(".", CellKind.Single, Style.Default));

        Assert.Equal(".", buf[8, 8].Grapheme);
        Assert.Equal(".", buf[9, 9].Grapheme);
    }

    // ---- Write (parity with CellBuffer.Write) ----

    [Fact]
    public void Write_TranslatesByOffsetAndAdvances()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(4, 3, 5, 5);

        int advanced = view.Write(1, 2, "ab", Style.Default);

        Assert.Equal(2, advanced);
        Assert.Equal("a", buf[5, 5].Grapheme);   // (4+1, 3+2)
        Assert.Equal("b", buf[6, 5].Grapheme);
    }

    [Fact]
    public void Write_AdvancesByTwoForWideClusters()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(4, 3, 6, 5);

        int advanced = view.Write(0, 0, "中b", Style.Default);

        Assert.Equal(3, advanced);
        Assert.Equal(CellKind.WideLeft, buf[4, 3].Kind);
        Assert.Equal(CellKind.WideContinuation, buf[5, 3].Kind);
        Assert.Equal("b", buf[6, 3].Grapheme);
    }

    [Fact]
    public void Write_StopsAtViewRightEdge()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(0, 0, 3, 1);   // 3 columns wide

        int advanced = view.Write(0, 0, "ab中", Style.Default);

        Assert.Equal(2, advanced);          // wide 中 can't fit the single remaining column
        Assert.Equal("a", buf[0, 0].Grapheme);
        Assert.Equal("b", buf[1, 0].Grapheme);
        Assert.Equal(default(Cell), buf[2, 0]);
    }

    [Fact]
    public void Write_StartOutsideView_SilentlyDropped()
    {
        var buf = new CellBuffer(10, 10);
        var view = buf.View(4, 3, 5, 5);

        Assert.Equal(0, view.Write(5, 0, "X", default));   // past the right edge of the view
        Assert.Equal(0, view.Write(0, 5, "X", default));   // past the bottom edge
    }

    // ---- Fill(in Rect, in Cell) ----

    [Fact]
    public void FillRegion_TranslatesAndClipsToView()
    {
        var buf = new CellBuffer(10, 5);
        var view = buf.View(2, 1, 5, 3);   // backing rect cols [2,7) rows [1,4)
        var cell = new Cell(".", CellKind.Single, Style.Default);

        // View-local rect cols [1,4) rows [0,2) → backing cols [3,6) rows [1,3).
        view.Fill(new Rect(1, 0, 3, 2), cell);

        Assert.Equal(".", buf[3, 1].Grapheme);
        Assert.Equal(".", buf[5, 2].Grapheme);
        // Just outside the filled rect (still inside the view) — untouched.
        Assert.Null(buf[2, 1].Grapheme);
        Assert.Null(buf[6, 1].Grapheme);
    }

    [Fact]
    public void FillRegion_ClipsRectExtendingPastView()
    {
        var buf = new CellBuffer(10, 5);
        var view = buf.View(2, 1, 3, 2);   // backing rect cols [2,5) rows [1,3)
        var cell = new Cell("#", CellKind.Single, Style.Default);

        // Oversized view-local rect — only the in-view slice fills.
        view.Fill(new Rect(0, 0, 100, 100), cell);

        Assert.Equal("#", buf[2, 1].Grapheme);
        Assert.Equal("#", buf[4, 2].Grapheme);
        Assert.Null(buf[5, 1].Grapheme);   // past the view's right edge
        Assert.Null(buf[2, 3].Grapheme);   // past the view's bottom edge
    }

    // ---- DirtyRegions (transformed projection) ----

    [Fact]
    public void DirtyRegions_ProjectAndClipToViewLocalCoordinates()
    {
        var buf = new CellBuffer(10, 4);
        var view = buf.View(2, 1, 5, 2);   // backing rect cols [2,7) rows [1,3)

        // Backing region cols [3,7) rows [1,3) — fully inside the view's backing rect.
        buf.MarkDirty(new Rect(3, 1, 4, 2));

        var regions = view.DirtyRegions;

        Assert.Single(regions);
        // Translated to view-local: subtract the (2, 1) offset → cols [1,5) rows [0,2).
        Assert.Equal(new Rect(1, 0, 4, 2), regions[0]);
    }

    [Fact]
    public void DirtyRegions_ClipRegionExtendingPastView()
    {
        var buf = new CellBuffer(10, 4);
        var view = buf.View(2, 1, 3, 2);   // backing rect cols [2,5) rows [1,3)

        // Region spans the whole buffer width; only the [2,5) slice overlaps the view.
        buf.MarkDirty(new Rect(0, 1, 10, 2));

        var regions = view.DirtyRegions;

        Assert.Single(regions);
        Assert.Equal(new Rect(0, 0, 3, 2), regions[0]);   // clipped to the 3-wide view, view-local
    }

    [Fact]
    public void DirtyRegions_DropRegionOutsideView()
    {
        var buf = new CellBuffer(10, 4);
        var view = buf.View(0, 0, 3, 2);

        buf.MarkDirty(new Rect(5, 2, 2, 1));   // entirely outside the view

        Assert.Empty(view.DirtyRegions);
    }

    [Fact]
    public void DirtyRegions_EmptyWhenNoneMarked()
    {
        var buf = new CellBuffer(10, 4);
        var view = buf.View(2, 1, 5, 2);

        Assert.Empty(view.DirtyRegions);
    }

    [Fact]
    public void MarkDirty_OnView_RecordsTranslatedRegionOnBuffer()
    {
        var buf = new CellBuffer(10, 4);
        var view = buf.View(2, 1, 5, 2);

        view.MarkDirty(new Rect(1, 0, 2, 1));   // view-local

        Assert.Single(buf.DirtyRegions);
        Assert.Equal(new Rect(3, 1, 2, 1), buf.DirtyRegions[0]);   // translated to backing coords
    }

    // ---- Test helpers ----

    private sealed class TestFragment : IBufferFragment
    {
        public Size GetSize() => new(0, 0);
        public bool IsSupported(OutputCapabilities capabilities) => true;
        public void Emit(int column, int row, IBufferWriter<byte> output,
                         OutputCapabilities capabilities)
        { }
    }
}
