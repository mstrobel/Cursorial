using Cursorial.Output;
using Cursorial.Rendering;

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
        int width = buf.Set(0, 0, "a", Style.Default);

        Assert.Equal(1, width);
        Assert.Equal(CellKind.Single, buf[0, 0].Kind);
        Assert.Equal("a", buf[0, 0].Grapheme);
    }

    [Fact]
    public void Set_WideCharacter_StoresAsWideLeftAndContinuation()
    {
        var buf = new CellBuffer(5, 1);
        int width = buf.Set(0, 0, "中", Style.Default);

        Assert.Equal(2, width);
        Assert.Equal(CellKind.WideLeft, buf[0, 0].Kind);
        Assert.Equal("中", buf[0, 0].Grapheme);
        Assert.Equal(CellKind.WideContinuation, buf[1, 0].Kind);
    }

    [Fact]
    public void Set_WideCharacterAtRightEdge_DegradesToBlankSingleWidth()
    {
        var buf = new CellBuffer(3, 1);
        int width = buf.Set(2, 0, "中", Style.Default); // last column

        Assert.Equal(1, width);
        Assert.Equal(CellKind.Single, buf[2, 0].Kind);
        Assert.Null(buf[2, 0].Grapheme);
    }

    [Fact]
    public void Set_OverwriteWideLeft_ClearsOrphanContinuation()
    {
        var buf = new CellBuffer(5, 1);
        buf.Set(0, 0, "中", Style.Default);
        Assert.Equal(CellKind.WideContinuation, buf[1, 0].Kind);

        buf.Set(0, 0, "a", Style.Default);

        Assert.Equal(CellKind.Single, buf[0, 0].Kind);
        Assert.Equal(default, buf[1, 0]); // orphan continuation cleared
    }

    [Fact]
    public void Set_OverwriteWideContinuation_ClearsLeftWideHalf()
    {
        var buf = new CellBuffer(5, 1);
        buf.Set(0, 0, "中", Style.Default);
        Assert.Equal(CellKind.WideLeft, buf[0, 0].Kind);

        buf.Set(1, 0, "x", Style.Default);

        Assert.Equal(default, buf[0, 0]); // orphan wide-left cleared
        Assert.Equal(CellKind.Single, buf[1, 0].Kind);
        Assert.Equal("x", buf[1, 0].Grapheme);
    }

    [Fact]
    public void Set_OutOfBounds_Throws()
    {
        var buf = new CellBuffer(5, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.Set(0, 1, "a", Style.Default));
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.Set(5, 0, "a", Style.Default));
    }

    // ---- Indexer ----

    [Fact]
    public void Indexer_RoundTripsRawCell()
    {
        var buf = new CellBuffer(5, 1);
        var cell = new Cell("x", CellKind.Single, Style.Default.WithAttributes(TextAttributes.Bold));
        buf[0, 0] = cell;
        Assert.Equal(cell, buf[0, 0]);
    }

    // ---- Clear / Fill ----

    [Fact]
    public void Clear_ResetsAllCellsToDefault()
    {
        var buf = new CellBuffer(3, 3);
        buf.Set(0, 0, "a", Style.Default);
        buf.Set(1, 1, "中", Style.Default);

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
        var fill = new Cell(".", CellKind.Single, Style.Default.WithAttributes(TextAttributes.Italic));

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
        buf.Set(0, 0, "a", Style.Default);
        buf.Resize(5, 5);

        Assert.Equal(5, buf.Columns);
        Assert.Equal(5, buf.Rows);
        Assert.Equal(default, buf[0, 0]);
    }

    [Fact]
    public void Resize_SameDimensions_ClearsContents()
    {
        var buf = new CellBuffer(3, 3);
        buf.Set(0, 0, "a", Style.Default);
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

        int advanced = buf.Write(0, 0, "abc", Style.Default);

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
        int advanced = buf.Write(0, 0, "a中b", Style.Default);

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
        int advanced = buf.Write(0, 0, "👨‍👩‍👧!", Style.Default);

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
        int advanced = buf.Write(0, 0, "ab中", Style.Default);

        Assert.Equal(2, advanced);
        Assert.Equal("a", buf[0, 0].Grapheme);
        Assert.Equal("b", buf[1, 0].Grapheme);
        Assert.Equal(default(Cell), buf[2, 0]);          // wide glyph dropped, not degraded
    }

    [Fact]
    public void Write_EmptyOrNull_IsNoOp()
    {
        var buf = new CellBuffer(5, 1);

        Assert.Equal(0, buf.Write(0, 0, "", Style.Default));
        Assert.Equal(0, buf.Write(0, 0, (string?)null, Style.Default));
        Assert.Equal(default(Cell), buf[0, 0]);
    }

    [Fact]
    public void Write_InvalidStart_Throws()
    {
        var buf = new CellBuffer(5, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.Write(5, 0, "x", Style.Default));
    }
}