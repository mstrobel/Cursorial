using Cursorial.Drawing.Media;
using Cursorial.Output;

namespace Cursorial.Tests.Drawing;

public class BrailleTests
{
    private static bool IsBraille(string? g) => !string.IsNullOrEmpty(g) && g[0] is >= '⠀' and <= '⣿';

    [Theory]
    [InlineData(0, 0, 0)] [InlineData(0, 1, 1)] [InlineData(0, 2, 2)] [InlineData(0, 3, 6)]   // col 0: dots 1,2,3,7
    [InlineData(1, 0, 3)] [InlineData(1, 1, 4)] [InlineData(1, 2, 5)] [InlineData(1, 3, 7)]   // col 1: dots 4,5,6,8
    public void BrailleGlyphs_Bit_MatchesStandardLayout(int dx, int dy, int bit) =>
        Assert.Equal(bit, BrailleGlyphs.Bit(dx, dy));

    [Fact]
    public void BrailleGlyphs_Glyph_MasksToCodepoint()
    {
        Assert.Equal("⠁", BrailleGlyphs.Glyph(0b0000_0001));   // dot 1 → ⠁
        Assert.Equal("⣿", BrailleGlyphs.Glyph(0xFF));          // all dots → ⣿
        Assert.Equal("⠀", BrailleGlyphs.Glyph(0));            // blank braille
    }

    [Fact]
    public void BrailleGlyphs_Ascii()
    {
        Assert.Equal("*", BrailleGlyphs.Glyph(0x01, GlyphSet.Ascii));
        Assert.Equal(" ", BrailleGlyphs.Glyph(0, GlyphSet.Ascii));
    }

    [Fact]
    public void BrailleRaster_OrsDotsThenLastWriterColor()
    {
        var raster = new BrailleRaster(1, 1);
        int red = raster.AddRecord(new BrailleRecord { Brush = Brushes.Red });
        int blue = raster.AddRecord(new BrailleRecord { Brush = Brushes.Blue });
        raster.Plot(0, 0, red);    // cell (0,0), dot 1 (bit 0)
        raster.Plot(1, 0, blue);   // same cell, dot 4 (bit 3)

        byte dots = 0;
        BrailleRecord record = default;
        raster.Flush((_, _, d, r) => { dots = d; record = r; });

        Assert.Equal(0b0000_1001, dots);   // bit0 | bit3
        Assert.Equal(Brushes.Blue.Color, Assert.IsType<SolidColorBrush>(record.Brush).Color);   // last writer wins color
    }

    [Fact]
    public void DiagonalLine_DrawsBraille_AndDoesNotThrow()
    {
        var b = DrawHarness.Render(4, 4, ctx => ctx.DrawLine(0, 0, 3, 3, Color.FromRgb(0, 200, 0)));
        Assert.True(IsBraille(b[0, 0].Grapheme));
        Assert.True(IsBraille(b[3, 3].Grapheme));
    }

    [Fact]
    public void AxisAlignedLine_StillUsesBoxGlyphs()
    {
        var b = DrawHarness.Render(3, 1, ctx => ctx.DrawLine(0, 0, 2, 0, Pens.Light));
        Assert.Equal("─", b[1, 0].Grapheme);   // box, not braille
    }

    [Fact]
    public void Text_BeatsBraille()
    {
        var b = DrawHarness.Render(4, 4, ctx =>
        {
            ctx.DrawText(0, 0, "X", Color.FromRgb(255, 255, 255));
            ctx.DrawLine(0, 0, 3, 3, Color.FromRgb(0, 200, 0));
        });
        Assert.Equal("X", b[0, 0].Grapheme);
    }

    [Fact]
    public void Braille_BeatsBox_AtASharedCell()
    {
        // Braille flushes before box, so it wins a shared cell; a box-only cell stays box.
        var b = DrawHarness.Render(4, 4, ctx =>
        {
            ctx.DrawLine(0, 0, 3, 0, Pens.Light);          // horizontal box line at row 0
            ctx.DrawLine(0, 0, 3, 3, Color.FromRgb(0, 200, 0));   // diagonal braille through (0,0)
        });
        Assert.True(IsBraille(b[0, 0].Grapheme));   // braille wins the shared cell
        Assert.Equal("─", b[3, 0].Grapheme);        // box-only (the diagonal is at row 3 by column 3)
    }
}
