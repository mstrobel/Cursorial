using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

// DrawTitledBox / DrawPanel: a box outline with a label on the top edge, the rule split around it. All four
// edges deposit under one stroke record so corners are JunctionMode-independent (like DrawBox).
public class PanelTests
{
    private static readonly Color White = Color.FromRgb(255, 255, 255);
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);

    [Fact]
    public void TitledBox_DrawsCornersAndSplitTopAroundTitle()
    {
        // Left title "Hi": ┌─ Hi ─…─┐ — gapStart=2, text at col 3.
        var b = DrawHarness.Render(20, 4, ctx => ctx.DrawTitledBox(new Rect(0, 0, 12, 4), "Hi", White));
        Assert.Equal("┌", b[0, 0].Grapheme);
        Assert.Equal("┐", b[11, 0].Grapheme);
        Assert.Equal("└", b[0, 3].Grapheme);
        Assert.Equal("┘", b[11, 3].Grapheme);
        Assert.Equal("─", b[1, 0].Grapheme);    // left line run reaches the corner
        Assert.Equal("H", b[3, 0].Grapheme);
        Assert.Equal("i", b[4, 0].Grapheme);
        Assert.Equal("─", b[6, 0].Grapheme);    // line resumes after the title gap
    }

    [Theory]
    [InlineData(JunctionMode.Merge)]
    [InlineData(JunctionMode.Break)]
    [InlineData(JunctionMode.Overlay)]
    public void TitledBox_CornersClose_RegardlessOfJunctionMode(JunctionMode mode)
    {
        // The defect this design fixes: with separate DrawLine edges, Break/Overlay would break the corners.
        var pen = new Pen(White) { Junction = mode };
        var b = DrawHarness.Render(20, 4, ctx => ctx.DrawTitledBox(new Rect(0, 0, 12, 4), "Hi", pen));
        Assert.Equal("┌", b[0, 0].Grapheme);
        Assert.Equal("┐", b[11, 0].Grapheme);
        Assert.Equal("└", b[0, 3].Grapheme);
        Assert.Equal("┘", b[11, 3].Grapheme);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void TitledBox_TooNarrowForTitle_DegradesToPlainBox(int width)
    {
        // Below width 7 there's no room for a title with corner runs + pad → plain box, corners still close.
        var b = DrawHarness.Render(12, 4, ctx =>
            ctx.DrawTitledBox(new Rect(0, 0, width, 4), new PanelTitle("Title").WithPosition(TitlePosition.Center), White));
        Assert.Equal("┌", b[0, 0].Grapheme);
        Assert.Equal("┐", b[width - 1, 0].Grapheme);
        Assert.Equal("─", b[1, 0].Grapheme);             // full top rule (no gap)
        Assert.DoesNotContain("T", Enumerable.Range(0, width).Select(c => b[c, 0].Grapheme));
    }

    [Fact]
    public void TitledBox_GradientPen_SamplesAgainstFullRect()
    {
        // One record with bounds == rect, so the rule's gradient spans the whole box, not the partial runs.
        var pen = new Pen(new LinearGradientBrush(Red, Blue, startPoint: RelativePoint.Left, endPoint: RelativePoint.Right));
        var b = DrawHarness.Render(20, 4, ctx => ctx.DrawTitledBox(new Rect(0, 0, 12, 4), "Hi", pen));
        Assert.True(b[0, 0].Style.Foreground.Red > b[0, 0].Style.Foreground.Blue, "left corner red-dominant");
        Assert.True(b[11, 0].Style.Foreground.Blue > b[11, 0].Style.Foreground.Red, "right corner blue-dominant");
    }

    [Fact]
    public void TitledBox_WideTitleGlyph_NoOrphanContinuation()
    {
        var b = DrawHarness.Render(20, 4, ctx => ctx.DrawTitledBox(new Rect(0, 0, 14, 4), "中", White));
        Assert.Equal("中", b[3, 0].Grapheme);
        Assert.Equal(CellKind.WideContinuation, b[4, 0].Kind);
        Assert.Equal("─", b[6, 0].Grapheme);   // line resumes cleanly after the wide glyph + pad
    }

    [Fact]
    public void TitledBox_LongTitle_ClippedToInterior_NotSurface()
    {
        // Box (10) narrower than the surface (30): the title must clip to the box interior, not overrun the corner.
        var b = DrawHarness.Render(30, 4, ctx => ctx.DrawTitledBox(new Rect(0, 0, 10, 4), "VeryLongTitle", White));
        Assert.Equal("┐", b[9, 0].Grapheme);   // right corner intact
        Assert.Equal("V", b[3, 0].Grapheme);
        Assert.Equal("y", b[6, 0].Grapheme);   // clipped to "Very" (maxText = 10 − 6 = 4)
        Assert.Equal("─", b[8, 0].Grapheme);
    }

    [Fact]
    public void TitledBox_RightPosition_SeatsTitleNearRightCorner()
    {
        var b = DrawHarness.Render(20, 4, ctx =>
            ctx.DrawTitledBox(new Rect(0, 0, 12, 4), new PanelTitle("Hi").WithPosition(TitlePosition.Right), White));
        Assert.Equal("H", b[7, 0].Grapheme);
        Assert.Equal("i", b[8, 0].Grapheme);
        Assert.Equal("┐", b[11, 0].Grapheme);
    }

    [Fact]
    public void TitledBox_OverwritePen_DoesNotStompTitle()
    {
        var b = DrawHarness.Render(20, 4, ctx => ctx.DrawTitledBox(new Rect(0, 0, 12, 4), "Hi", White, overwrite: true));
        Assert.Equal("H", b[3, 0].Grapheme);   // border runs skip the gap, title survives
        Assert.Equal("i", b[4, 0].Grapheme);
    }

    [Fact]
    public void DrawPanel_FillsAndBordersAndTitles()
    {
        var b = DrawHarness.Render(20, 5, ctx => ctx.DrawPanel(new Rect(0, 0, 12, 5), White, Blue, new PanelTitle("P")));
        Assert.Equal("┌", b[0, 0].Grapheme);
        Assert.Equal(Blue, b[5, 2].Style.Background);   // interior filled
        Assert.Equal("P", b[3, 0].Grapheme);
    }

    [Fact]
    public void TitledBox_ZeroRect_NoOp()
    {
        var b = DrawHarness.Render(10, 4, ctx => ctx.DrawTitledBox(new Rect(2, 2, 0, 0), "X", White));
        Assert.True(string.IsNullOrEmpty(b[2, 2].Grapheme));
    }
}
