using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Tests.Drawing;

public class ChartTests
{
    // ---- BlockGlyphs (oracle-pinned ramps) ----

    [Theory]
    [InlineData(0, " ")]
    [InlineData(1, "▁")]
    [InlineData(4, "▄")]
    [InlineData(7, "▇")]
    [InlineData(8, "█")]
    public void BlockGlyphs_VerticalRamp_MatchesOracle(int level, string expected) =>
        Assert.Equal(expected, BlockGlyphs.Glyph(level, BlockAxis.Vertical));

    [Theory]
    [InlineData(0, " ")]
    [InlineData(1, "▏")]
    [InlineData(4, "▌")]
    [InlineData(7, "▉")]
    [InlineData(8, "█")]
    public void BlockGlyphs_HorizontalRamp_MatchesOracle(int level, string expected) =>
        Assert.Equal(expected, BlockGlyphs.Glyph(level, BlockAxis.Horizontal));

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 4)]
    [InlineData(1.0, 8)]
    [InlineData(-1.0, 0)]   // clamped
    [InlineData(2.0, 8)]    // clamped
    public void BlockGlyphs_Level_QuantizesAndClamps(double fraction, int expected) =>
        Assert.Equal(expected, BlockGlyphs.Level(fraction));

    [Fact]
    public void BlockGlyphs_Ascii_IsBlankFilledOrPartial()
    {
        Assert.Equal(" ", BlockGlyphs.Glyph(0, BlockAxis.Vertical, GlyphSet.Ascii));
        Assert.Equal("#", BlockGlyphs.Glyph(8, BlockAxis.Vertical, GlyphSet.Ascii));
        Assert.Equal("+", BlockGlyphs.Glyph(4, BlockAxis.Vertical, GlyphSet.Ascii));
    }

    // ---- AxisRange ----

    [Fact]
    public void AxisRange_FromValues_NormalEmptyAndAllEqual()
    {
        Assert.Equal(new AxisRange(2, 9), AxisRange.FromValues([5, 2, 9, 7]));
        Assert.Equal(new AxisRange(0, 1), AxisRange.FromValues(ReadOnlySpan<double>.Empty));
        Assert.Equal(new AxisRange(2, 4), AxisRange.FromValues([3, 3, 3]));   // padded ±1
    }

    [Fact]
    public void AxisRange_FromValues_IgnoresNonFinite()
    {
        var r = AxisRange.FromValues([1, double.NaN, 5, double.PositiveInfinity]);
        Assert.Equal(new AxisRange(1, 5), r);
    }

    [Fact]
    public void AxisRange_UnionIncludingZeroAndNormalize()
    {
        Assert.Equal(new AxisRange(2, 12), new AxisRange(2, 8).Union(new AxisRange(5, 12)));
        Assert.Equal(new AxisRange(0, 8), new AxisRange(3, 8).IncludingZero());
        Assert.Equal(0.5, new AxisRange(0, 10).Normalize(5));
        Assert.Equal(0.5, new AxisRange(4, 4).Normalize(4));   // degenerate → mid, no divide-by-zero
    }

    // ---- BarChart / Sparkline render (via composite read-back) ----

    [Fact]
    public void BarChart_Vertical_FillsBottomAnchoredByEighths()
    {
        // 1-row area: 8 eighths max. values 0 / half / full → " " / ▄ / █.
        var b = DrawHarness.Render(3, 1, ctx => ctx.BarChart(new Rect(0, 0, 3, 1), [0, 4, 8], Color.FromRgb(0, 200, 0)));
        Assert.True(string.IsNullOrEmpty(b[0, 0].Grapheme));   // 0 → empty
        Assert.Equal("▄", b[1, 0].Grapheme);                   // 4/8
        Assert.Equal("█", b[2, 0].Grapheme);                   // full
    }

    [Fact]
    public void BarChart_Vertical_TallBarStacksFullCellsThenPartialTop()
    {
        // 2-row area (16 eighths), explicit range. value 12/16 → full bottom cell (8) + half top cell (4).
        var b = DrawHarness.Render(1, 2, ctx =>
            new BarChart([12], Color.FromRgb(0, 200, 0)) { Range = new AxisRange(0, 16) }
                .Render(ctx, new Rect(0, 0, 1, 2)));
        Assert.Equal("█", b[0, 1].Grapheme);   // bottom row full
        Assert.Equal("▄", b[0, 0].Grapheme);   // top row half
    }

    [Fact]
    public void BarChart_Vertical_BarColorIsTheBrush()
    {
        var green = Color.FromRgb(0, 200, 0);
        var b = DrawHarness.Render(1, 1, ctx =>
            new BarChart([8], green) { Range = new AxisRange(0, 8) }.Render(ctx, new Rect(0, 0, 1, 1)));
        Assert.Equal("█", b[0, 0].Grapheme);
        Assert.Equal(green, b[0, 0].Style.Foreground);
    }

    [Fact]
    public void BarChart_Horizontal_FillsLeftAnchored()
    {
        // Row of 3 cells (24 eighths), explicit range. value 6/12 = half → █ ▌ empty.
        var b = DrawHarness.Render(3, 1, ctx =>
            new BarChart([6], Color.FromRgb(0, 200, 0)) { Orientation = BarOrientation.Horizontal, Range = new AxisRange(0, 12) }
                .Render(ctx, new Rect(0, 0, 3, 1)));
        Assert.Equal("█", b[0, 0].Grapheme);
        Assert.Equal("▌", b[1, 0].Grapheme);
        Assert.True(string.IsNullOrEmpty(b[2, 0].Grapheme));
    }

    [Fact]
    public void BarChart_AutoRange_IsZeroToDataMax_NotPadded()
    {
        // Default (no explicit Range): a lone bar reaches full (auto-range [0, dataMax]), and an
        // all-equal series all reach full — not the ±1-padded sparkline range.
        var lone = DrawHarness.Render(1, 1, ctx => ctx.BarChart(new Rect(0, 0, 1, 1), [12], Color.FromRgb(0, 200, 0)));
        Assert.Equal("█", lone[0, 0].Grapheme);

        var equal = DrawHarness.Render(2, 1, ctx => ctx.BarChart(new Rect(0, 0, 2, 1), [5, 5], Color.FromRgb(0, 200, 0)));
        Assert.Equal("█", equal[0, 0].Grapheme);
        Assert.Equal("█", equal[1, 0].Grapheme);
    }

    [Fact]
    public void Sparkline_MapsMinToOneEighthAndMaxToFull()
    {
        var b = DrawHarness.Render(3, 1, ctx => ctx.Sparkline(0, 0, 3, [0, 5, 10], Color.FromRgb(200, 200, 0)));
        Assert.Equal("▁", b[0, 0].Grapheme);   // min → level 1
        Assert.Equal("█", b[2, 0].Grapheme);   // max → level 8
        Assert.False(string.IsNullOrEmpty(b[1, 0].Grapheme));   // mid present
    }

    [Fact]
    public void Chart_Extension_DispatchesToRender()
    {
        IChart chart = new BarChart([8], Color.FromRgb(0, 200, 0)) { Range = new AxisRange(0, 8) };
        var b = DrawHarness.Render(1, 1, ctx => ctx.Chart(new Rect(0, 0, 1, 1), chart));
        Assert.Equal("█", b[0, 0].Grapheme);
    }

    [Fact]
    public void Charts_EmptyOrZeroArea_AreNoOps()
    {
        DrawHarness.Render(2, 2, ctx =>
        {
            ctx.BarChart(new Rect(0, 0, 2, 2), ReadOnlySpan<double>.Empty, Color.FromRgb(0, 200, 0));
            ctx.BarChart(new Rect(0, 0, 0, 0), [1, 2, 3], Color.FromRgb(0, 200, 0));
            ctx.Sparkline(0, 0, 0, [1, 2], Color.FromRgb(0, 200, 0));
        });   // must not throw
    }
}
