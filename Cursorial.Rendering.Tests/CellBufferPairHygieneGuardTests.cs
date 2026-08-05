using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Rendering;

/// <summary>
/// Both write paths — <see cref="CellBuffer.Set(int, int, string?, in Style)"/> and the raw indexer —
/// blank the surviving half of a wide pair they break apart. Neither may do that on the word of the
/// <em>overwritten</em> cell alone: it must first confirm the neighbor really is the pairing half.
/// The buffer can legitimately be inconsistent when they run — <see cref="CellBuffer.Fill(in Rect, in Cell)"/>
/// and <see cref="CellBuffer.ClearCells(in Rect)"/> write raw cells with no pair maintenance, so a
/// region edge landing mid-pair leaves a bare continuation or a lone WideLeft behind. Blanking on
/// kind alone then destroys whatever innocent cell happens to sit next door.
/// </summary>
public class CellBufferPairHygieneGuardTests
{
    private static readonly Color Red = Color.FromRgb(200, 0, 0);
    private static readonly Color Green = Color.FromRgb(0, 160, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 200);

    private static readonly Style Innocent = Style.Default.WithBackground(Green);

    /// <summary>
    /// A pair at columns 1–2, then one of its halves stomped by a raw <see cref="CellBuffer.Fill(in Rect, in Cell)"/>
    /// (which does no pair maintenance) and replaced with an unrelated bystander glyph. What is left
    /// is exactly the inconsistency the kind guards exist for.
    /// </summary>
    private static CellBuffer BufferWithBrokenPair(int stompedColumn, string bystander)
    {
        var buf = new CellBuffer(6, 1);
        buf.Set(1, 0, "中", Style.Default.WithBackground(Red));
        buf.Fill(new Rect(stompedColumn, 0, 1, 1), new Cell(bystander, CellKind.Single, Innocent));
        return buf;
    }

    [Fact]
    public void Set_OverContinuation_WhoseLeftNeighborIsNotAWideLeft_LeavesTheNeighborAlone()
    {
        // Column 1 is now a plain "A"; column 2 is a bare continuation pointing at nothing.
        var buf = BufferWithBrokenPair(stompedColumn: 1, bystander: "A");
        Assert.Equal(CellKind.WideContinuation, buf[2, 0].Kind);   // the inconsistency really is there

        buf.Set(2, 0, "x", Style.Default.WithBackground(Blue));

        Assert.Equal("A", buf[1, 0].Grapheme);
        Assert.Equal(Green, buf[1, 0].Style.Background);
        Assert.Equal("x", buf[2, 0].Grapheme);
    }

    [Fact]
    public void Set_OverWideLeft_WhoseRightNeighborIsNotAContinuation_LeavesTheNeighborAlone()
    {
        // Column 2 is now a plain "B"; column 1 is a WideLeft with no continuation.
        var buf = BufferWithBrokenPair(stompedColumn: 2, bystander: "B");
        Assert.Equal(CellKind.WideLeft, buf[1, 0].Kind);

        buf.Set(1, 0, "x", Style.Default.WithBackground(Blue));

        Assert.Equal("B", buf[2, 0].Grapheme);
        Assert.Equal(Green, buf[2, 0].Style.Background);
        Assert.Equal("x", buf[1, 0].Grapheme);
    }

    [Fact]
    public void Set_OverWideLeft_DoesNotDismemberAnAdjacentIntactPair()
    {
        // A lone WideLeft at 1 with the NEXT pair butted right against it at 2–3. Blanking column 2
        // on kind alone would decapitate that pair and strand its continuation at 3.
        var buf = new CellBuffer(6, 1);
        buf.Set(2, 0, "全", Style.Default.WithBackground(Green));
        buf.Fill(new Rect(1, 0, 1, 1), new Cell("中", CellKind.WideLeft, Style.Default.WithBackground(Red)));

        buf.Set(1, 0, "x", Style.Default.WithBackground(Blue));

        Assert.Equal("全", buf[2, 0].Grapheme);
        Assert.Equal(CellKind.WideLeft, buf[2, 0].Kind);
        Assert.Equal(CellKind.WideContinuation, buf[3, 0].Kind);
    }

    [Fact]
    public void Indexer_OverContinuation_WhoseLeftNeighborIsNotAWideLeft_LeavesTheNeighborAlone()
    {
        var buf = BufferWithBrokenPair(stompedColumn: 1, bystander: "A");

        buf[2, 0] = new Cell("x", CellKind.Single, Style.Default.WithBackground(Blue));

        Assert.Equal("A", buf[1, 0].Grapheme);
        Assert.Equal(Green, buf[1, 0].Style.Background);
    }

    [Fact]
    public void Indexer_OverWideLeft_WhoseRightNeighborIsNotAContinuation_LeavesTheNeighborAlone()
    {
        var buf = BufferWithBrokenPair(stompedColumn: 2, bystander: "B");

        buf[1, 0] = new Cell("x", CellKind.Single, Style.Default.WithBackground(Blue));

        Assert.Equal("B", buf[2, 0].Grapheme);
        Assert.Equal(Green, buf[2, 0].Style.Background);
    }

    [Fact]
    public void Set_WidePairOverBrokenPair_StillLeavesNoStrandedHalf()
    {
        // The guards must not buy neighbor safety at the cost of the invariant they protect: writing
        // a fresh pair across a broken one has to leave the row fully paired either way.
        var buf = BufferWithBrokenPair(stompedColumn: 2, bystander: "B");

        buf.Set(1, 0, "全", Style.Default.WithBackground(Blue));

        AssertNoStrandedHalves(buf);
        Assert.Equal(CellKind.WideLeft, buf[1, 0].Kind);
        Assert.Equal(CellKind.WideContinuation, buf[2, 0].Kind);
    }

    private static void AssertNoStrandedHalves(CellBuffer buffer)
    {
        for (int column = 0; column < buffer.Columns; column++)
        {
            var cell = buffer[column, 0];
            if (cell.Kind == CellKind.WideLeft)
            {
                Assert.True(column + 1 < buffer.Columns);
                Assert.Equal(CellKind.WideContinuation, buffer[column + 1, 0].Kind);
            }
            else if (cell.Kind == CellKind.WideContinuation)
            {
                Assert.True(column > 0);
                Assert.Equal(CellKind.WideLeft, buffer[column - 1, 0].Kind);
            }
        }
    }
}
