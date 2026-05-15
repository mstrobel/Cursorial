using Cursorial.Output;
using Cursorial.Output.Capabilities;

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

        Style style = Style.Default
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
        var result = q.Quantize(Style.Default.WithForeground(Color.FromRgb(255, 0, 0)));
        Assert.Equal(ColorKind.Palette, result.Foreground.Kind);
    }

    [Fact]
    public void Ansi256_PassesHigherPaletteIndicesThrough()
    {
        var q = new StyleQuantizer(Caps(ColorDepth.Ansi256));
        var result = q.Quantize(Style.Default.WithForeground(Color.FromPalette(196)));
        Assert.Equal(Color.FromPalette(196), result.Foreground);
    }

    [Fact]
    public void Ansi16_QuantizesRgbToAnsi16()
    {
        var q = new StyleQuantizer(Caps(ColorDepth.Ansi16));
        var result = q.Quantize(Style.Default.WithForeground(Color.FromRgb(255, 0, 0)));
        Assert.Equal(ColorKind.Palette, result.Foreground.Kind);
        Assert.InRange(result.Foreground.PaletteIndex, (byte) 0, (byte) 15);
    }

    [Fact]
    public void Ansi16_ReducesHigherPaletteIndices()
    {
        var q = new StyleQuantizer(Caps(ColorDepth.Ansi16));
        var result = q.Quantize(Style.Default.WithForeground(Color.FromPalette(196)));
        Assert.InRange(result.Foreground.PaletteIndex, (byte) 0, (byte) 15);
    }

    [Fact]
    public void NoColor_ReducesAllColorsToDefault()
    {
        var q = new StyleQuantizer(Caps(ColorDepth.NoColor));

        Style result = q.Quantize(Style.Default
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
        var result = q.Quantize(Style.Default.WithAttributes(TextAttributes.Italic | TextAttributes.Bold));
        Assert.Equal(TextAttributes.Bold, result.Attributes);
    }

    [Fact]
    public void DropsOverlineWhenNotSupported()
    {
        var q = new StyleQuantizer(Caps(overline: false));
        var result = q.Quantize(Style.Default.WithAttributes(TextAttributes.Overline));
        Assert.Equal(TextAttributes.None, result.Attributes);
    }

    [Fact]
    public void DropsUnderlineWhenNotSupported()
    {
        var q = new StyleQuantizer(Caps(underline: false));
        var result = q.Quantize(Style.Default.WithAttributes(TextAttributes.Underline));
        Assert.Equal(TextAttributes.None, result.Attributes);
    }

    // ---- Underline shape and color ----

    [Fact]
    public void FallsBackExtendedUnderlineShapeToSingle()
    {
        var q = new StyleQuantizer(Caps(extendedUnderline: false));

        Style result = q.Quantize(Style.Default
                                       .WithAttributes(TextAttributes.Underline)
                                       .WithUnderlineStyle(UnderlineStyle.Curly));

        Assert.Equal(UnderlineStyle.Single, result.UnderlineStyle);
    }

    [Fact]
    public void DropsUnderlineColorWhenNotSupported()
    {
        var q = new StyleQuantizer(Caps(coloredUnderline: false));

        Style result = q.Quantize(Style.Default
                                       .WithAttributes(TextAttributes.Underline)
                                       .WithUnderlineColor(Color.FromRgb(255, 0, 0)));

        Assert.True(result.UnderlineColor.IsDefault);
    }

    [Fact]
    public void QuantizesUnderlineColorAcrossDepths()
    {
        var q = new StyleQuantizer(Caps(ColorDepth.Ansi256));

        Style result = q.Quantize(Style.Default
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
}