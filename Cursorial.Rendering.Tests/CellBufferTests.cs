using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;

namespace Cursorial.Tests.Rendering;

public class CellBufferTests
{
    [Fact]
    public void Constructor_ZeroOrNegativeDimensions_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellBuffer(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellBuffer(10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellBuffer(-1, 10));
    }

    [Fact]
    public void Dimensions_ReturnConstructorValues()
    {
        var buf = new CellBuffer(80, 24);
        Assert.Equal(80, buf.Columns);
        Assert.Equal(24, buf.Rows);
        Assert.Equal(80 * 24, buf.CellCount);
    }

    [Fact]
    public void NewBuffer_AllCellsAreDefault()
    {
        var buf = new CellBuffer(5, 5);
        for (int r = 0; r < 5; r++)
        {
            for (int c = 0; c < 5; c++)
                Assert.Equal(default(Cell), buf[c, r]);
        }
    }

    // ---- Set ----

    [Fact]
    public void Set_SingleCharacter_StoresAsSingleWidth()
    {
        var buf = new CellBuffer(5, 1);
        int width = buf.Set(0, 0, "a", CellStyle.Default);

        Assert.Equal(1, width);
        Assert.Equal(CellKind.Single, buf[0, 0].Kind);
        Assert.Equal("a", buf[0, 0].Grapheme);
    }

    [Fact]
    public void Set_WideCharacter_StoresAsWideLeftAndContinuation()
    {
        var buf = new CellBuffer(5, 1);
        int width = buf.Set(0, 0, "中", CellStyle.Default);

        Assert.Equal(2, width);
        Assert.Equal(CellKind.WideLeft, buf[0, 0].Kind);
        Assert.Equal("中", buf[0, 0].Grapheme);
        Assert.Equal(CellKind.WideContinuation, buf[1, 0].Kind);
    }

    [Fact]
    public void Set_WideCharacterAtRightEdge_DegradesToBlankSingleWidth()
    {
        var buf = new CellBuffer(3, 1);
        int width = buf.Set(2, 0, "中", CellStyle.Default); // last column

        Assert.Equal(1, width);
        Assert.Equal(CellKind.Single, buf[2, 0].Kind);
        Assert.Null(buf[2, 0].Grapheme);
    }

    [Fact]
    public void Set_OverwriteWideLeft_ClearsOrphanContinuation()
    {
        var buf = new CellBuffer(5, 1);
        buf.Set(0, 0, "中", CellStyle.Default);
        Assert.Equal(CellKind.WideContinuation, buf[1, 0].Kind);

        buf.Set(0, 0, "a", CellStyle.Default);

        Assert.Equal(CellKind.Single, buf[0, 0].Kind);
        Assert.Equal(default, buf[1, 0]); // orphan continuation cleared
    }

    [Fact]
    public void Set_OverwriteWideContinuation_ClearsLeftWideHalf()
    {
        var buf = new CellBuffer(5, 1);
        buf.Set(0, 0, "中", CellStyle.Default);
        Assert.Equal(CellKind.WideLeft, buf[0, 0].Kind);

        buf.Set(1, 0, "x", CellStyle.Default);

        Assert.Equal(default, buf[0, 0]); // orphan wide-left cleared
        Assert.Equal(CellKind.Single, buf[1, 0].Kind);
        Assert.Equal("x", buf[1, 0].Grapheme);
    }

    [Fact]
    public void Set_OutOfBounds_Throws()
    {
        var buf = new CellBuffer(5, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.Set(0, 1, "a", CellStyle.Default));
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.Set(5, 0, "a", CellStyle.Default));
    }

    // ---- Indexer ----

    [Fact]
    public void Indexer_RoundTripsRawCell()
    {
        var buf = new CellBuffer(5, 1);
        var cell = new Cell("x", CellKind.Single, CellStyle.Default.WithAttributes(TextAttributes.Bold));
        buf[0, 0] = cell;
        Assert.Equal(cell, buf[0, 0]);
    }

    // ---- Indexer wide-pair invariant maintenance ----
    // The raw indexer bypasses blending but NOT pair consistency: the buffer must never hold half
    // a wide glyph, or the frame renderer's wide-cell emission contract corrupts the terminal
    // (the SceneCompositor writes through this indexer at every region seam).

    [Fact]
    public void Indexer_SingleOverContinuation_ClearsOrphanWideLeft()
    {
        var buf = new CellBuffer(5, 1);
        buf.Set(0, 0, "中", CellStyle.Default);

        buf[1, 0] = new Cell("x", CellKind.Single, CellStyle.Default); // raw write over the right half

        Assert.Equal(default, buf[0, 0]); // the orphaned WideLeft was blanked
        Assert.Equal("x", buf[1, 0].Grapheme);
    }

    [Fact]
    public void Indexer_SingleOverWideLeft_ClearsOrphanContinuation()
    {
        var buf = new CellBuffer(5, 1);
        buf.Set(0, 0, "中", CellStyle.Default);

        buf[0, 0] = new Cell("x", CellKind.Single, CellStyle.Default); // raw write over the left half

        Assert.Equal("x", buf[0, 0].Grapheme);
        Assert.Equal(default, buf[1, 0]); // the orphaned continuation was blanked
    }

    [Fact]
    public void Indexer_WideLeft_WritesContinuation()
    {
        var buf = new CellBuffer(5, 1);
        var style = CellStyle.Default.WithAttributes(TextAttributes.Bold);

        buf[0, 0] = new Cell("中", CellKind.WideLeft, style);

        Assert.Equal(CellKind.WideLeft, buf[0, 0].Kind);
        Assert.Equal(style, buf[0, 0].Style);

        // Kind alone on the continuation: the wide-left's SGR is what paints both columns, so a
        // style here would be a duplicate nothing reads and every write would have to keep in sync.
        Assert.Equal(CellKind.WideContinuation, buf[1, 0].Kind);
        Assert.Equal(default, buf[1, 0].Style);
    }

    [Fact]
    public void Indexer_WideLeftAtRightEdge_DegradesToBlankSingle()
    {
        var buf = new CellBuffer(3, 1);

        buf[2, 0] = new Cell("中", CellKind.WideLeft, CellStyle.Default); // last column — no room for the right half

        Assert.Equal(CellKind.Single, buf[2, 0].Kind);
        Assert.Null(buf[2, 0].Grapheme);
    }

    [Fact]
    public void Indexer_BareContinuation_DegradesToBlankSingle()
    {
        // A region copy whose left edge split a pair stores a continuation with no WideLeft to
        // pair with — it must not survive as half a glyph.
        var buf = new CellBuffer(5, 1);

        buf[2, 0] = Cell.WideContinuation with { Style = CellStyle.Default };

        Assert.Equal(CellKind.Single, buf[2, 0].Kind);
        Assert.Null(buf[2, 0].Grapheme);
    }

    [Fact]
    public void Indexer_PairCopyCellByCell_PreservesPair()
    {
        // The blit pattern: copy a consistent pair left-to-right through the raw indexer. The
        // WideLeft write auto-writes a continuation; the explicit continuation write replaces it
        // in kind and must NOT trigger the orphan cleanup (which would blank the just-written left).
        var source = new CellBuffer(5, 1);
        source.Set(0, 0, "中", CellStyle.Default);

        var buf = new CellBuffer(5, 1);
        buf[0, 0] = source[0, 0];
        buf[1, 0] = source[1, 0];

        Assert.Equal(CellKind.WideLeft, buf[0, 0].Kind);
        Assert.Equal("中", buf[0, 0].Grapheme);
        Assert.Equal(CellKind.WideContinuation, buf[1, 0].Kind);
    }

    [Fact]
    public void Indexer_WideLeftOverNextPairsWideLeft_ClearsCascadedOrphan()
    {
        // Writing a pair whose continuation lands on the NEXT pair's WideLeft must blank that
        // pair's own continuation two columns over — otherwise it survives as half a glyph.
        var buf = new CellBuffer(6, 1);
        buf.Set(1, 0, "中", CellStyle.Default); // pair at (1,2)

        buf[0, 0] = new Cell("全", CellKind.WideLeft, CellStyle.Default); // pair at (0,1) — overwrites 中's left

        Assert.Equal(CellKind.WideLeft, buf[0, 0].Kind);
        Assert.Equal(CellKind.WideContinuation, buf[1, 0].Kind);
        Assert.Equal(default, buf[2, 0]); // 中's orphaned continuation was blanked
    }

    [Fact]
    public void Set_WidePairOverNextPairsWideLeft_ClearsCascadedOrphan()
    {
        // The same cascade through Set (the pre-existing gap the indexer work surfaced).
        var buf = new CellBuffer(6, 1);
        buf.Set(1, 0, "中", CellStyle.Default); // pair at (1,2)

        buf.Set(0, 0, "全", CellStyle.Default); // pair at (0,1) — its continuation overwrites 中's left

        Assert.Equal(CellKind.WideLeft, buf[0, 0].Kind);
        Assert.Equal(CellKind.WideContinuation, buf[1, 0].Kind);
        Assert.Equal(default, buf[2, 0]); // 中's orphaned continuation was blanked
    }

    // ---- Clear / Fill ----

    [Fact]
    public void Clear_ResetsAllCellsToDefault()
    {
        var buf = new CellBuffer(3, 3);
        buf.Set(0, 0, "a", CellStyle.Default);
        buf.Set(1, 1, "中", CellStyle.Default);

        buf.Clear();

        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
                Assert.Equal(default(Cell), buf[c, r]);
        }
    }

    [Fact]
    public void Fill_AssignsCellEverywhere()
    {
        var buf = new CellBuffer(2, 2);
        var fill = new Cell(".", CellKind.Single, CellStyle.Default.WithAttributes(TextAttributes.Italic));

        buf.Fill(fill);

        for (int r = 0; r < 2; r++)
        {
            for (int c = 0; c < 2; c++)
                Assert.Equal(fill, buf[c, r]);
        }
    }

    // ---- Resize ----

    [Fact]
    public void Resize_DiscardsContents()
    {
        var buf = new CellBuffer(3, 3);
        buf.Set(0, 0, "a", CellStyle.Default);
        buf.Resize(5, 5);

        Assert.Equal(5, buf.Columns);
        Assert.Equal(5, buf.Rows);
        Assert.Equal(default, buf[0, 0]);
    }

    [Fact]
    public void Resize_SameDimensions_ClearsContents()
    {
        var buf = new CellBuffer(3, 3);
        buf.Set(0, 0, "a", CellStyle.Default);
        buf.Resize(3, 3);

        Assert.Equal(default, buf[0, 0]);
    }

    // ---- Cursor ----

    [Fact]
    public void Cursor_StatePropertiesRoundTrip()
    {
        var buf = new CellBuffer(10, 5);
        buf.CursorRow = 2;
        buf.CursorColumn = 4;
        buf.CursorVisible = false;
        buf.CursorShape = CursorShape.BlinkingBar;

        Assert.Equal(2, buf.CursorRow);
        Assert.Equal(4, buf.CursorColumn);
        Assert.False(buf.CursorVisible);
        Assert.Equal(CursorShape.BlinkingBar, buf.CursorShape);
    }

    [Fact]
    public void Cursor_DefaultsAreSensible()
    {
        var buf = new CellBuffer(10, 5);
        Assert.Equal(0, buf.CursorRow);
        Assert.Equal(0, buf.CursorColumn);
        Assert.True(buf.CursorVisible);
        Assert.Equal(CursorShape.Default, buf.CursorShape);
    }

    [Fact]
    public void Write_PlacesEachClusterAndAdvances()
    {
        var buf = new CellBuffer(10, 1);

        int advanced = buf.Write(0, 0, "abc", CellStyle.Default);

        Assert.Equal(3, advanced);
        Assert.Equal("a", buf[0, 0].Grapheme);
        Assert.Equal("b", buf[1, 0].Grapheme);
        Assert.Equal("c", buf[2, 0].Grapheme);
    }

    [Fact]
    public void Write_AdvancesByTwoForWideClusters()
    {
        var buf = new CellBuffer(10, 1);

        // "a中b": single + wide (2 cells) + single = 4 columns.
        int advanced = buf.Write(0, 0, "a中b", CellStyle.Default);

        Assert.Equal(4, advanced);
        Assert.Equal(CellKind.Single, buf[0, 0].Kind);
        Assert.Equal(CellKind.WideLeft, buf[1, 0].Kind);
        Assert.Equal("中", buf[1, 0].Grapheme);
        Assert.Equal(CellKind.WideContinuation, buf[2, 0].Kind);
        Assert.Equal("b", buf[3, 0].Grapheme);
    }

    [Fact]
    public void Write_TreatsMultiCodepointEmojiAsOneCluster()
    {
        var buf = new CellBuffer(10, 1);

        // A ZWJ family sequence is a single (wide) grapheme cluster, not its component runes.
        int advanced = buf.Write(0, 0, "👨‍👩‍👧!", CellStyle.Default);

        Assert.Equal(3, advanced);                       // wide cluster (2) + "!" (1)
        Assert.Equal(CellKind.WideLeft, buf[0, 0].Kind);
        Assert.Equal("👨‍👩‍👧", buf[0, 0].Grapheme);
        Assert.Equal("!", buf[2, 0].Grapheme);
    }

    [Fact]
    public void Write_StopsAtRightEdge_WithoutClippingWideGlyph()
    {
        var buf = new CellBuffer(3, 1);

        // "ab中": a, b fit at columns 0,1; the wide 中 can't fit in the single remaining column.
        int advanced = buf.Write(0, 0, "ab中", CellStyle.Default);

        Assert.Equal(2, advanced);
        Assert.Equal("a", buf[0, 0].Grapheme);
        Assert.Equal("b", buf[1, 0].Grapheme);
        Assert.Equal(default(Cell), buf[2, 0]);          // wide glyph dropped, not degraded
    }

    [Fact]
    public void Write_EmptyOrNull_IsNoOp()
    {
        var buf = new CellBuffer(5, 1);

        Assert.Equal(0, buf.Write(0, 0, "", CellStyle.Default));
        Assert.Equal(0, buf.Write(0, 0, (string?)null, CellStyle.Default));
        Assert.Equal(default(Cell), buf[0, 0]);
    }

    [Fact]
    public void Write_StopsAtFirstControlCharacter()
    {
        // Single-row contract: a newline is not interpreted, it terminates the write (no junk cell).
        var buf = new CellBuffer(10, 1);

        int advanced = buf.Write(0, 0, "ab\ncd", CellStyle.Default);

        Assert.Equal(2, advanced);
        Assert.Equal("a", buf[0, 0].Grapheme);
        Assert.Equal("b", buf[1, 0].Grapheme);
        Assert.Equal(default(Cell), buf[2, 0]);          // nothing past the control — "cd" dropped
    }

    [Theory]
    [InlineData("\tab")]      // tab (C0)
    [InlineData("\rab")]      // CR (C0)
    [InlineData("\u007Fab")]  // DEL
    public void Write_LeadingControlCharacter_WritesNothing(string text)
    {
        var buf = new CellBuffer(10, 1);

        Assert.Equal(0, buf.Write(0, 0, text, CellStyle.Default));
        Assert.Equal(default(Cell), buf[0, 0]);
    }

    [Fact]
    public void Write_StopsAtC1Control()
    {
        var buf = new CellBuffer(10, 1);

        int advanced = buf.Write(0, 0, "ab" + (char) 0x9C + "cd", CellStyle.Default);   // U+009C (ST)

        Assert.Equal(2, advanced);
        Assert.Equal("b", buf[1, 0].Grapheme);
        Assert.Equal(default(Cell), buf[2, 0]);
    }

    [Fact]
    public void Write_InvalidStart_Throws()
    {
        var buf = new CellBuffer(5, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.Write(5, 0, "x", CellStyle.Default));
    }

    [Fact]
    public void FillRegion_TransparentCell_ClearsUnderDefaultMode()
    {
        // Default mode is a verbatim replace (consistent with the whole-buffer Fill), so a
        // transparent fill actually clears the region rather than blending to a no-op.
        var buf = new CellBuffer(4, 2);
        buf.Fill(new Rect(0, 0, 4, 2), new Cell("X", CellKind.Single, CellStyle.Default.WithBackground(Color.FromRgb(10, 20, 30))));
        Assert.Equal(Color.FromRgb(10, 20, 30), buf[0, 0].Style.Background);

        buf.Fill(new Rect(0, 0, 4, 2), new Cell(null, CellKind.Single, CellStyle.Transparent));
        Assert.True(buf[0, 0].Style.Background.IsTransparent);
        Assert.Null(buf[0, 0].Grapheme);
    }
}