using Cursorial.Drawing;
using Cursorial.Drawing.Charts;
using Cursorial.Drawing.Media;
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

    // The sparse far-edge ramps (negative-bar direction): Unicode offers only a 1/8 sliver and a 1/2 block,
    // so the quantization is {0, ⅛, ½, full}. Pin it so the hand-written table can't silently drift.
    [Theory]
    [InlineData(0, " ")]
    [InlineData(1, "▔")]
    [InlineData(2, "▔")]
    [InlineData(3, "▀")]   // sliver → half boundary
    [InlineData(6, "▀")]
    [InlineData(7, "█")]   // half → full boundary
    [InlineData(8, "█")]
    [InlineData(-1, " ")]  // clamped
    [InlineData(9, "█")]   // clamped
    public void BlockGlyphs_FarVerticalRamp_MatchesOracle(int level, string expected) =>
        Assert.Equal(expected, BlockGlyphs.FarGlyph(level, BlockAxis.Vertical));

    [Theory]
    [InlineData(0, " ")]
    [InlineData(1, "▕")]
    [InlineData(3, "▐")]
    [InlineData(7, "█")]
    public void BlockGlyphs_FarHorizontalRamp_MatchesOracle(int level, string expected) =>
        Assert.Equal(expected, BlockGlyphs.FarGlyph(level, BlockAxis.Horizontal));

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

    // ---- deferred chart features: NaN-gap, signed bars, FillArea, ToLayers, category axes ----

    private static readonly Color Green = Color.FromRgb(0, 200, 0);

    [Fact]
    public void LineChart_NaNPoint_BreaksTheCurveIntoAGap()
    {
        // A NaN in the middle breaks the line into two runs — fewer painted cells than a connected line.
        PointD[] connected = [new(0, 0), new(1, 1), new(2, 0), new(3, 1), new(4, 0)];
        PointD[] gapped    = [new(0, 0), new(1, 1), new(2, double.NaN), new(3, 1), new(4, 0)];
        int connectedCells = CountPainted(DrawHarness.Render(20, 6, ctx => new LineChart(connected, Green).Render(ctx, new Rect(0, 0, 20, 6))));
        int gappedCells    = CountPainted(DrawHarness.Render(20, 6, ctx => new LineChart(gapped, Green).Render(ctx, new Rect(0, 0, 20, 6))));
        Assert.True(gappedCells < connectedCells, $"a NaN gap should paint fewer cells: gapped={gappedCells}, connected={connectedCells}");
    }

    [Fact]
    public void BarChart_SignedValues_DrawAboveAndBelowTheBaseline()
    {
        // [1, -1]: bar 0 (positive) fills the top half; bar 1 (negative) the bottom half (was an empty bar before).
        var b = DrawHarness.Render(2, 4, ctx => new BarChart([1, -1], Green).Render(ctx, new Rect(0, 0, 2, 4)));
        Assert.False(string.IsNullOrEmpty(b[0, 0].Grapheme) && string.IsNullOrEmpty(b[0, 1].Grapheme), "positive bar should paint above the baseline");
        Assert.False(string.IsNullOrEmpty(b[1, 2].Grapheme) && string.IsNullOrEmpty(b[1, 3].Grapheme), "negative bar should paint below the baseline");
        // Exclusivity pins the baseline split — neither bar crosses to the other half.
        Assert.True(string.IsNullOrEmpty(b[0, 2].Grapheme) && string.IsNullOrEmpty(b[0, 3].Grapheme), "positive bar must not paint below the baseline");
        Assert.True(string.IsNullOrEmpty(b[1, 0].Grapheme) && string.IsNullOrEmpty(b[1, 1].Grapheme), "negative bar must not paint above the baseline");
    }

    [Fact]
    public void LineChart_FillArea_AddsBackgroundUnderTheCurve()
    {
        PointD[] pts = [new(0, 0), new(1, 2), new(2, 0)];   // a peak above the zero baseline
        int noFill = CountBackground(DrawHarness.Render(8, 5, ctx => new LineChart(pts, Green).Render(ctx, new Rect(0, 0, 8, 5))), Green);
        int filled = CountBackground(DrawHarness.Render(8, 5, ctx => new LineChart(pts, Green) { FillArea = true }.Render(ctx, new Rect(0, 0, 8, 5))), Green);
        Assert.Equal(0, noFill);                  // no fill → no green backgrounds (the curve only sets foreground)
        Assert.True(filled > 0, "FillArea should add green background cells under the curve");
    }

    [Fact]
    public void LineChart_FillArea_SkipsACellTheCurveBarelyClips()
    {
        // A flat curve at y=3.1 in a [0,4] range over 4 rows sits at fractional row 0.9 — it covers only 0.1
        // of the top cell (row 0), below the 0.2 threshold, so row 0 isn't shaded even though the braille dot
        // is in it. At y=3.5 (coverage 0.5) the top cell IS shaded.
        var yr = new AxisRange(0, 4);
        PointD[] barely = [new(0, 3.1), new(3, 3.1)];
        PointD[] well    = [new(0, 3.5), new(3, 3.5)];
        var b1 = DrawHarness.Render(4, 4, ctx => new LineChart(barely, Green) { FillArea = true, YRange = yr }.Render(ctx, new Rect(0, 0, 4, 4)));
        var b2 = DrawHarness.Render(4, 4, ctx => new LineChart(well, Green) { FillArea = true, YRange = yr }.Render(ctx, new Rect(0, 0, 4, 4)));
        Assert.NotEqual(Green, b1[0, 0].Style.Background);   // barely-clipped top cell → not shaded
        Assert.Equal(Green, b2[0, 0].Style.Background);      // well-covered top cell → shaded
    }

    [Fact]
    public void MultiLineChart_ToLayers_TranslucentFillsBlendOnOverlap()
    {
        // The per-layer payoff (§6): two translucent area fills compose where they overlap — red + blue → purple —
        // which a single-surface Render can't do (a cell has one background, last-writer-wins).
        var chart = new MultiLineChart(
        [
            new ChartSeries([new(0, 2), new(3, 2)], Color.FromRgb(255, 0, 0)) { FillArea = true, AreaBrush = new SolidColorBrush(Color.FromRgba(255, 0, 0, 128)) },
            new ChartSeries([new(0, 2), new(3, 2)], Color.FromRgb(0, 0, 255)) { FillArea = true, AreaBrush = new SolidColorBrush(Color.FromRgba(0, 0, 255, 128)) },
        ]) { YRange = new AxisRange(0, 2) };

        var layers = chart.ToLayers(new Rect(0, 0, 6, 4));
        Assert.Equal(2, layers.Count);

        var target = new CellBuffer(6, 4);
        new SceneCompositor(Style.Default)
            .Composite([new SceneLayer(layers[0]), new SceneLayer(layers[1])], target.AsView());

        var bg = target[0, 3].Style.Background;   // a cell under both flat fills
        Assert.True(bg.Red > 0 && bg.Blue > 0, $"overlapping translucent fills should blend red+blue, was {bg}");

        foreach (var s in layers) s.Dispose();
    }

    [Fact]
    public void BarChart_Categories_LabelTheBottomRow()
    {
        var b = DrawHarness.Render(6, 4, ctx =>
            new BarChart([3, 6], Green) { Categories = ["A", "B"], LabelColor = Color.FromRgb(200, 200, 200) }
                .Render(ctx, new Rect(0, 0, 6, 4)));
        string bottom = string.Concat(Enumerable.Range(0, 6).Select(c => b[c, 3].Grapheme ?? ""));
        Assert.Contains("A", bottom);
        Assert.Contains("B", bottom);
    }

    [Fact]
    public void BarChart_AllNegative_GrowsFromTopWithFarRampPartial()
    {
        // All-negative data puts the baseline at the top edge; bars grow downward and a non-cell-aligned tip
        // uses the sparse far (upper) ramp. [-1,-3]: bar 0's tip is "▀" (UpperRamp[3]), not a near-ramp partial.
        var b = DrawHarness.Render(2, 4, ctx => new BarChart([-1, -3], Green).Render(ctx, new Rect(0, 0, 2, 4)));
        Assert.Equal("█", b[0, 0].Grapheme);   // full cell at the top
        Assert.Equal("▀", b[0, 1].Grapheme);   // far-ramp partial below it
        Assert.True(string.IsNullOrEmpty(b[0, 3].Grapheme), "negative bar grows from the top — the bottom stays empty");
    }

    [Fact]
    public void BarChart_Horizontal_SignedValues_GrowLeftAndRightOfTheBaseline()
    {
        // Horizontal signed bars: positive grows right of the baseline, negative grows left.
        var b = DrawHarness.Render(8, 2, ctx =>
            new BarChart([2, -2], Green) { Orientation = BarOrientation.Horizontal, Range = new AxisRange(-2, 2) }
                .Render(ctx, new Rect(0, 0, 8, 2)));
        Assert.False(string.IsNullOrEmpty(b[7, 0].Grapheme), "positive bar should reach the right edge");
        Assert.True(string.IsNullOrEmpty(b[0, 0].Grapheme), "positive bar should not paint left of the baseline");
        Assert.False(string.IsNullOrEmpty(b[0, 1].Grapheme), "negative bar should reach the left edge");
        Assert.True(string.IsNullOrEmpty(b[7, 1].Grapheme), "negative bar should not paint right of the baseline");
    }

    [Fact]
    public void LineChart_FillArea_FollowsTheCurveBelowTheBaseline()
    {
        // A curve dipping below zero fills DOWNWARD from the baseline; with a range straddling zero the baseline
        // is interior, so green backgrounds appear in the lower half.
        PointD[] pts = [new(0, 0), new(1, -2), new(2, 0)];
        var b = DrawHarness.Render(8, 6, ctx =>
            new LineChart(pts, Green) { FillArea = true, YRange = new AxisRange(-2, 2) }.Render(ctx, new Rect(0, 0, 8, 6)));
        int lowerGreen = 0;
        for (int r = 3; r < 6; r++)
        for (int c = 0; c < 8; c++)
            if (b[c, r].Style.Background == Green) lowerGreen++;
        Assert.True(lowerGreen > 0, "a negative curve should fill below the baseline");
    }

    [Fact]
    public void LineChart_NaNAtEnds_RendersLikeTheTrimmedInterior()
    {
        // Leading and trailing non-finite points are just gap boundaries — the result matches the bare interior.
        var xr = new AxisRange(0, 4);
        var yr = new AxisRange(0, 2);
        PointD[] withEnds = [new(0, double.NaN), new(1, 1), new(2, 2), new(3, 1), new(4, double.NaN)];
        PointD[] interior = [new(1, 1), new(2, 2), new(3, 1)];
        int a = CountPainted(DrawHarness.Render(16, 6, ctx => new LineChart(withEnds, Green) { XRange = xr, YRange = yr }.Render(ctx, new Rect(0, 0, 16, 6))));
        int c = CountPainted(DrawHarness.Render(16, 6, ctx => new LineChart(interior, Green) { XRange = xr, YRange = yr }.Render(ctx, new Rect(0, 0, 16, 6))));
        Assert.Equal(c, a);
    }

    [Fact]
    public void MultiLineChart_ToLayers_ScenesAreSizedToTheArea_AndClampDegenerate()
    {
        var chart = new MultiLineChart([new ChartSeries([new(0, 0), new(1, 1)], Color.FromRgb(255, 0, 0))]);
        var layers = chart.ToLayers(new Rect(0, 0, 7, 5));
        var layer = Assert.Single(layers);
        Assert.Equal(7, layer.Columns);
        Assert.Equal(5, layer.Rows);
        layer.Dispose();
        Assert.Empty(chart.ToLayers(new Rect(0, 0, 0, 0)));   // degenerate area → empty list, nothing to dispose
    }

    [Fact]
    public void BarChart_Categories_CenterAndTruncateWithinTheLane()
    {
        // barThickness=3 (6 cols, 2 bars): "A" centers at laneStart+1; "LONG" truncates to the 3-col lane.
        var b = DrawHarness.Render(6, 4, ctx =>
            new BarChart([3, 6], Green) { Categories = ["A", "LONG"], LabelColor = Color.FromRgb(200, 200, 200) }
                .Render(ctx, new Rect(0, 0, 6, 4)));
        Assert.Equal("A", b[1, 3].Grapheme);                    // centered in lane 0 (cols 0-2) → col 1
        Assert.True(string.IsNullOrEmpty(b[0, 3].Grapheme));    // not at the lane start
        string lane1 = (b[3, 3].Grapheme ?? "") + (b[4, 3].Grapheme ?? "") + (b[5, 3].Grapheme ?? "");
        Assert.Equal("LON", lane1);                             // truncated to the lane width, no bleed past col 5
    }

    private static int CountPainted(CellBuffer b)
    {
        int count = 0;
        for (int r = 0; r < b.Rows; r++)
        for (int c = 0; c < b.Columns; c++)
            if (!string.IsNullOrEmpty(b[c, r].Grapheme)) count++;
        return count;
    }

    private static int CountBackground(CellBuffer b, Color background)
    {
        int count = 0;
        for (int r = 0; r < b.Rows; r++)
        for (int c = 0; c < b.Columns; c++)
            if (b[c, r].Style.Background == background) count++;
        return count;
    }
}
