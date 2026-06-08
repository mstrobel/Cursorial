using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

public class PenStrokeTests
{
    private static readonly Rect Box3 = new(0, 0, 3, 3);

    [Fact]
    public void LightBox_DrawsCornersAndEdges()
    {
        var b = DrawHarness.Render(3, 3, ctx => ctx.DrawBox(Box3, Pens.Light));

        Assert.Equal("┌", b[0, 0].Grapheme);
        Assert.Equal("┐", b[2, 0].Grapheme);
        Assert.Equal("└", b[0, 2].Grapheme);
        Assert.Equal("┘", b[2, 2].Grapheme);
        Assert.Equal("─", b[1, 0].Grapheme);
        Assert.Equal("│", b[0, 1].Grapheme);
        Assert.True(string.IsNullOrEmpty(b[1, 1].Grapheme));   // interior not stroked
    }

    [Fact]
    public void HeavyAndDoubleBoxes_UseTheRightGlyphFamily()
    {
        var heavy = DrawHarness.Render(3, 3, ctx => ctx.DrawBox(Box3, Pens.Heavy));
        Assert.Equal("┏", heavy[0, 0].Grapheme);
        Assert.Equal("┛", heavy[2, 2].Grapheme);
        Assert.Equal("━", heavy[1, 0].Grapheme);
        Assert.Equal("┃", heavy[0, 1].Grapheme);

        var dbl = DrawHarness.Render(3, 3, ctx => ctx.DrawBox(Box3, Pens.Double));
        Assert.Equal("╔", dbl[0, 0].Grapheme);
        Assert.Equal("╝", dbl[2, 2].Grapheme);
        Assert.Equal("═", dbl[1, 0].Grapheme);
        Assert.Equal("║", dbl[0, 1].Grapheme);
    }

    [Fact]
    public void LoneArmWithoutCap_RendersFullLine_NotStub()
    {
        // A 3-cell line: every cell is the full horizontal glyph, ends included.
        var b = DrawHarness.Render(3, 1, ctx => ctx.DrawLine(0, 0, 2, 0, Pens.Light));
        Assert.Equal("─", b[0, 0].Grapheme);
        Assert.Equal("─", b[1, 0].Grapheme);
        Assert.Equal("─", b[2, 0].Grapheme);
    }

    [Fact]
    public void TwoLinesSharingACell_FormAnElbow()
    {
        // (0,0)->(2,0) and (2,0)->(2,2) share cell (2,0) → ┐.
        var b = DrawHarness.Render(3, 3, ctx =>
        {
            ctx.DrawLine(0, 0, 2, 0, Pens.Light);
            ctx.DrawLine(2, 0, 2, 2, Pens.Light);
        });
        Assert.Equal("┐", b[2, 0].Grapheme);
        Assert.Equal("─", b[0, 0].Grapheme);   // horizontal lone left end
        Assert.Equal("│", b[2, 2].Grapheme);   // vertical lone bottom end
    }

    [Fact]
    public void TwoLinesNotSharingACell_DoNotJoin()
    {
        // The vertical now starts at (2,1), so (2,0) holds only the horizontal end.
        var b = DrawHarness.Render(3, 3, ctx =>
        {
            ctx.DrawLine(0, 0, 2, 0, Pens.Light);
            ctx.DrawLine(2, 1, 2, 2, Pens.Light);
        });
        Assert.Equal("─", b[2, 0].Grapheme);   // horizontal end — no down arm
        Assert.Equal("│", b[2, 1].Grapheme);   // vertical start — no join
    }

    [Fact]
    public void Merge_IsPerDirectionMax_NotBitwiseOr()
    {
        // A light run and a heavy run over the same cells: MAX → heavy ━, NOT light|heavy = double ═.
        var b = DrawHarness.Render(3, 1, ctx =>
        {
            ctx.DrawLine(0, 0, 2, 0, Pens.Light);
            ctx.DrawLine(0, 0, 2, 0, Pens.Heavy);
        });
        Assert.Equal("━", b[1, 0].Grapheme);
    }

    [Fact]
    public void HeavyPlusDouble_HasNoExactGlyph_FallsBackToHeavy()
    {
        // Heavy horizontal + double vertical at (1,0): no Unicode glyph → double→heavy → ┳.
        var b = DrawHarness.Render(3, 3, ctx =>
        {
            ctx.DrawLine(0, 0, 2, 0, Pens.Heavy);
            ctx.DrawLine(1, 0, 1, 2, Pens.Double);
        });
        Assert.Equal("┳", b[1, 0].Grapheme);
    }

    [Fact]
    public void RoundedBox_UsesArcCorners()
    {
        var b = DrawHarness.Render(3, 3, ctx => ctx.DrawBox(Box3, Pens.Rounded));
        Assert.Equal("╭", b[0, 0].Grapheme);
        Assert.Equal("╮", b[2, 0].Grapheme);
        Assert.Equal("╯", b[2, 2].Grapheme);
        Assert.Equal("╰", b[0, 2].Grapheme);
    }

    [Fact]
    public void DashedRun_UsesDashGlyphOnTheInterior()
    {
        var b = DrawHarness.Render(5, 1, ctx => ctx.DrawLine(0, 0, 4, 0, Pens.Light.WithDash(LineDash.Triple)));
        Assert.Equal("┄", b[2, 0].Grapheme);
    }

    [Fact]
    public void Caps_RenderStubsAtLoneEnds()
    {
        var b = DrawHarness.Render(3, 1, ctx => ctx.DrawLine(0, 0, 2, 0, Pens.Light.WithEndCap(EndCap.Stub)));
        Assert.Equal("╶", b[0, 0].Grapheme);   // right stub (left terminus)
        Assert.Equal("─", b[1, 0].Grapheme);
        Assert.Equal("╴", b[2, 0].Grapheme);   // left stub (right terminus)
    }

    [Fact]
    public void AsciiGlyphSet_UsesPlusDashPipe()
    {
        var b = DrawHarness.Render(3, 3, ctx => ctx.DrawBox(Box3, Pens.Ascii));
        Assert.Equal("+", b[0, 0].Grapheme);
        Assert.Equal("-", b[1, 0].Grapheme);
        Assert.Equal("|", b[0, 1].Grapheme);
    }

    [Fact]
    public void Text_BeatsDecoration_RegardlessOfDrawOrder()
    {
        var white = Color.FromRgb(255, 255, 255);

        var boxThenText = DrawHarness.Render(3, 3, ctx =>
        {
            ctx.DrawBox(Box3, Pens.Light);
            ctx.DrawText(0, 0, "X", white);
        });
        Assert.Equal("X", boxThenText[0, 0].Grapheme);

        var textThenBox = DrawHarness.Render(3, 3, ctx =>
        {
            ctx.DrawText(0, 0, "X", white);
            ctx.DrawBox(Box3, Pens.Light);
        });
        Assert.Equal("X", textThenBox[0, 0].Grapheme);
    }

    [Fact]
    public void Overwrite_BypassesEviction()
    {
        var b = DrawHarness.Render(3, 3, ctx =>
        {
            ctx.DrawText(0, 0, "X", Color.FromRgb(255, 255, 255));
            ctx.DrawBox(Box3, Pens.Light, overwrite: true);
        });
        Assert.Equal("┌", b[0, 0].Grapheme);
    }

    [Fact]
    public void DrawRectangle_OutlinesAFilledInterior()
    {
        var fill = Color.FromRgb(40, 40, 40);
        var b = DrawHarness.Render(3, 3, ctx => ctx.DrawRectangle(Box3, Color.FromRgb(200, 200, 200), fill));

        Assert.Equal("┌", b[0, 0].Grapheme);
        Assert.Equal(fill, b[0, 0].Style.Background);          // fill shows under the box glyph
        Assert.True(string.IsNullOrEmpty(b[1, 1].Grapheme));   // interior: no glyph
        Assert.Equal(fill, b[1, 1].Style.Background);          // interior: filled
    }

    [Fact]
    public void JunctionMode_Break_IncomingYields_PriorStaysContinuous()
    {
        // Horizontal drawn first; the crossing vertical uses Break → it yields at (1,1), so the
        // horizontal stays continuous (─) and the vertical shows the gap there.
        var b = DrawHarness.Render(3, 3, ctx =>
        {
            ctx.DrawLine(0, 1, 2, 1, Pens.Light);
            ctx.DrawLine(1, 0, 1, 2, Pens.Light.WithJunction(JunctionMode.Break));
        });
        Assert.Equal("─", b[1, 1].Grapheme);
    }

    [Fact]
    public void JunctionMode_Overlay_IncomingReplaces_PriorShowsGap()
    {
        // Same crossing, but Overlay → the vertical replaces the cell (│); the horizontal gaps there.
        var b = DrawHarness.Render(3, 3, ctx =>
        {
            ctx.DrawLine(0, 1, 2, 1, Pens.Light);
            ctx.DrawLine(1, 0, 1, 2, Pens.Light.WithJunction(JunctionMode.Overlay));
        });
        Assert.Equal("│", b[1, 1].Grapheme);
    }

    [Fact]
    public void WideGlyph_SurvivesABoxDrawnOverIt()
    {
        // A wide CJK glyph occupies (1,0)+(2,0); a horizontal line across the row must not overwrite
        // either half (text beats decoration), but draws normally on the clear cells.
        var b = DrawHarness.Render(5, 1, ctx =>
        {
            ctx.DrawText(1, 0, "中", Color.FromRgb(255, 255, 255));
            ctx.DrawLine(0, 0, 4, 0, Pens.Light);
        });

        Assert.Equal("中", b[1, 0].Grapheme);    // wide glyph's left half survives
        Assert.NotEqual("─", b[2, 0].Grapheme);  // its continuation half is not stomped by a box edge
        Assert.Equal("─", b[0, 0].Grapheme);     // box drew on the clear cells
        Assert.Equal("─", b[3, 0].Grapheme);
    }

    [Fact]
    public void DiagonalLine_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            DrawHarness.Render(3, 3, ctx => ctx.DrawLine(0, 0, 2, 2, Pens.Light)));
    }
}
