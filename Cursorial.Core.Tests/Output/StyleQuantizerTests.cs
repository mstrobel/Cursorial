using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Text;

namespace Cursorial.Tests.Output;

public class StyleQuantizerTests
{
    private static OutputCapabilities Caps(
        ColorDepth depth = ColorDepth.Truecolor,
        bool italic = true,
        bool underline = true,
        bool extendedUnderline = true,
        bool coloredUnderline = true,
        bool strikethrough = true,
        bool overline = true)
    {
        return OutputCapabilities.None with
               {
                   Color = OutputCapabilities.None.Color with { Depth = depth },
                   Styling = new TextStylingCapabilities(
                       Italic: italic,
                       Underline: underline,
                       ExtendedUnderline: extendedUnderline,
                       ColoredUnderline: coloredUnderline,
                       Strikethrough: strikethrough,
                       Overline: overline,
                       Hyperlinks: false)
               };
    }

    // ---- Color depth ----

    [Fact]
    public void Truecolor_PassesRgbAndPaletteThrough()
    {
        var q = new StyleQuantizer(Caps());

        CellStyle style = CellStyle.Default
                           .WithForeground(Color.FromRgb(123, 45, 200))
                           .WithBackground(Color.FromPalette(196));

        var result = q.Quantize(style);

        Assert.Equal(Color.FromRgb(123, 45, 200), result.Foreground);
        Assert.Equal(Color.FromPalette(196), result.Background);
    }

    [Fact]
    public void Ansi256_QuantizesRgbToPalette()
    {
        var q = new StyleQuantizer(Caps(ColorDepth.Ansi256));
        var result = q.Quantize(CellStyle.Default.WithForeground(Color.FromRgb(255, 0, 0)));
        Assert.Equal(ColorKind.Palette, result.Foreground.Kind);
    }

    [Fact]
    public void Ansi256_PassesHigherPaletteIndicesThrough()
    {
        var q = new StyleQuantizer(Caps(ColorDepth.Ansi256));
        var result = q.Quantize(CellStyle.Default.WithForeground(Color.FromPalette(196)));
        Assert.Equal(Color.FromPalette(196), result.Foreground);
    }

    [Fact]
    public void Ansi16_QuantizesRgbToAnsi16()
    {
        var q = new StyleQuantizer(Caps(ColorDepth.Ansi16));
        var result = q.Quantize(CellStyle.Default.WithForeground(Color.FromRgb(255, 0, 0)));
        Assert.Equal(ColorKind.Palette, result.Foreground.Kind);
        Assert.InRange(result.Foreground.PaletteIndex, (byte) 0, (byte) 15);
    }

    [Fact]
    public void Ansi16_ReducesHigherPaletteIndices()
    {
        var q = new StyleQuantizer(Caps(ColorDepth.Ansi16));
        var result = q.Quantize(CellStyle.Default.WithForeground(Color.FromPalette(196)));
        Assert.InRange(result.Foreground.PaletteIndex, (byte) 0, (byte) 15);
    }

    [Fact]
    public void NoColor_ReducesAllColorsToDefault()
    {
        var q = new StyleQuantizer(Caps(ColorDepth.NoColor));

        CellStyle result = q.Quantize(CellStyle.Default
                                       .WithForeground(Color.FromRgb(255, 0, 0))
                                       .WithBackground(Color.FromPalette(2)));

        Assert.True(result.Foreground.IsDefault);
        Assert.True(result.Background.IsDefault);
    }

    // ---- Attribute filtering ----

    [Fact]
    public void DropsItalicWhenNotSupported()
    {
        var q = new StyleQuantizer(Caps(italic: false));
        var result = q.Quantize(CellStyle.Default.WithAttributes(TextAttributes.Italic | TextAttributes.Bold));
        Assert.Equal(TextAttributes.Bold, result.Attributes);
    }

    [Fact]
    public void DropsOverlineWhenNotSupported()
    {
        var q = new StyleQuantizer(Caps(overline: false));
        var result = q.Quantize(CellStyle.Default.WithAttributes(TextAttributes.Overline));
        Assert.Equal(TextAttributes.None, result.Attributes);
    }

    [Fact]
    public void DropsUnderlineWhenNotSupported()
    {
        var q = new StyleQuantizer(Caps(underline: false));
        var result = q.Quantize(CellStyle.Default.WithAttributes(TextAttributes.Underline));
        Assert.Equal(TextAttributes.None, result.Attributes);
    }

    // ---- Underline shape and color ----

    [Fact]
    public void FallsBackExtendedUnderlineShapeToSingle()
    {
        var q = new StyleQuantizer(Caps(extendedUnderline: false));

        CellStyle result = q.Quantize(CellStyle.Default
                                       .WithAttributes(TextAttributes.Underline)
                                       .WithUnderlineStyle(UnderlineStyle.Curly));

        Assert.Equal(UnderlineStyle.Single, result.UnderlineStyle);
    }

    [Fact]
    public void DropsUnderlineColorWhenNotSupported()
    {
        var q = new StyleQuantizer(Caps(coloredUnderline: false));

        CellStyle result = q.Quantize(CellStyle.Default
                                       .WithAttributes(TextAttributes.Underline)
                                       .WithUnderlineColor(Color.FromRgb(255, 0, 0)));

        Assert.True(result.UnderlineColor.IsDefault);
    }

    [Fact]
    public void QuantizesUnderlineColorAcrossDepths()
    {
        var q = new StyleQuantizer(Caps(ColorDepth.Ansi256));

        CellStyle result = q.Quantize(CellStyle.Default
                                       .WithAttributes(TextAttributes.Underline)
                                       .WithUnderlineColor(Color.FromRgb(0, 0, 255)));

        Assert.Equal(ColorKind.Palette, result.UnderlineColor.Kind);
    }

    // ---- NearestPaletteIndex ----

    [Theory]
    [InlineData(0, 0, 0, 16)]        // cube corner = black
    [InlineData(255, 255, 255, 231)] // cube corner = white
    [InlineData(255, 0, 0, 196)]     // pure red
    [InlineData(0, 255, 0, 46)]      // pure green
    [InlineData(0, 0, 255, 21)]      // pure blue
    public void NearestPaletteIndex_CornersOfRgbCube(int r, int g, int b, int expected)
    {
        Assert.Equal((byte) expected, StyleQuantizer.NearestPaletteIndex((byte) r, (byte) g, (byte) b));
    }

    [Fact]
    public void NearestPaletteIndex_MidGray_HitsGrayscaleRamp()
    {
        // Mid-gray (128,128,128) should pick the grayscale ramp over the cube — the ramp has
        // finer steps in the gray region.
        var idx = StyleQuantizer.NearestPaletteIndex(128, 128, 128);
        Assert.InRange(idx, (byte) 232, (byte) 255);
    }

    // ---- Ordered dither ----

    [Fact]
    public void QuantizeDithered_Ansi256_AdjacentColumns_ProduceDistinctIndices()
    {
        // A constant mid-gray, dithered, lands on different palette stops at adjacent Bayer phases —
        // that spatial split is what breaks the band. Oracle-pinned to the exact bytes.
        var q = new StyleQuantizer(Caps(ColorDepth.Ansi256));
        CellStyle style = CellStyle.Default.WithForeground(Color.FromRgb(105, 105, 105));

        var a = q.QuantizeDithered(style, 0, 0).Foreground;   // Bayer threshold 0  → −22.5 → rgb 82
        var b = q.QuantizeDithered(style, 1, 0).Foreground;   // Bayer threshold 8  → +1.5  → rgb 106

        Assert.Equal(ColorKind.Palette, a.Kind);
        Assert.Equal(ColorKind.Palette, b.Kind);
        Assert.NotEqual(a.PaletteIndex, b.PaletteIndex);
        Assert.Equal((byte) 239, a.PaletteIndex);
        Assert.Equal((byte) 242, b.PaletteIndex);
    }

    [Fact]
    public void QuantizeDithered_IsDeterministic()
    {
        var q = new StyleQuantizer(Caps(ColorDepth.Ansi256));
        CellStyle style = CellStyle.Default.WithForeground(Color.FromRgb(105, 105, 105)).WithBackground(Color.FromRgb(40, 90, 200));
        Assert.Equal(q.QuantizeDithered(style, 3, 2), q.QuantizeDithered(style, 3, 2));
    }

    [Fact]
    public void QuantizeDithered_Truecolor_EqualsPlainQuantize()
    {
        // No reduction happens at full depth, so dithering must be a no-op — the exact RGB survives.
        var q = new StyleQuantizer(Caps(ColorDepth.Truecolor));
        CellStyle style = CellStyle.Default.WithForeground(Color.FromRgb(105, 105, 105)).WithBackground(Color.FromRgb(7, 99, 200));
        Assert.Equal(q.Quantize(style), q.QuantizeDithered(style, 1, 1));
        Assert.Equal(Color.FromRgb(105, 105, 105), q.QuantizeDithered(style, 1, 1).Foreground);
    }

    [Fact]
    public void QuantizeDithered_PaletteDefaultAndTransparent_MatchPlainQuantize()
    {
        // No continuous RGB to perturb → identical to plain quantize at any position.
        var q = new StyleQuantizer(Caps(ColorDepth.Ansi256));
        foreach (var c in new[] { Color.FromPalette(196), Color.Default, Color.Transparent })
        {
            CellStyle style = CellStyle.Default.WithForeground(c);
            Assert.Equal(q.Quantize(style).Foreground, q.QuantizeDithered(style, 2, 3).Foreground);
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    public void QuantizeDithered_ChannelExtremes_NeverOverflow(int r, int g, int b)
    {
        // Black and white at every Bayer phase must clamp cleanly to a valid palette index (no wrap).
        var q = new StyleQuantizer(Caps(ColorDepth.Ansi256));
        CellStyle style = CellStyle.Default.WithForeground(Color.FromRgb((byte) r, (byte) g, (byte) b));
        for (int row = 0; row < 4; row++)
        for (int col = 0; col < 4; col++)
        {
            var fg = q.QuantizeDithered(style, col, row).Foreground;
            Assert.Equal(ColorKind.Palette, fg.Kind);
        }
    }

    [Fact]
    public void QuantizeDithered_Ansi16_StaysInRange()
    {
        var q = new StyleQuantizer(Caps(ColorDepth.Ansi16));
        CellStyle style = CellStyle.Default.WithForeground(Color.FromRgb(105, 160, 70));
        for (int row = 0; row < 4; row++)
        for (int col = 0; col < 4; col++)
            Assert.InRange(q.QuantizeDithered(style, col, row).Foreground.PaletteIndex, (byte) 0, (byte) 15);
    }
}