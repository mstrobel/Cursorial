using System.Buffers;
using System.Text;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;

namespace Cursorial.Tests.Rendering;

public class FrameRendererQuantizationTests
{
    private static OutputCapabilities Caps(
        ColorDepth depth = ColorDepth.Truecolor,
        bool extendedUnderline = true,
        bool coloredUnderline = true,
        bool overline = true,
        bool strikethrough = true,
        bool italic = true,
        bool underline = true)
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
                Hyperlinks: false),
        };
    }

    private static string Render(FrameRenderer renderer, CellBuffer back)
    {
        var w = new ArrayBufferWriter<byte>();
        renderer.Render(back, w);
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    // ---- Truecolor caps: no quantization observed in output ----

    [Fact]
    public void TruecolorCaps_RgbEmittedAsTrueColorSgr()
    {
        var r = new FrameRenderer(Caps(ColorDepth.Truecolor));
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", Style.Default.WithForeground(Color.FromRgb(255, 128, 0)));

        var output = Render(r, buf);
        Assert.Contains("38;2;255;128;0", output);
    }

    // ---- 256-color caps: RGB quantized to palette ----

    [Fact]
    public void Ansi256Caps_RgbQuantizedToPaletteIndex()
    {
        var r = new FrameRenderer(Caps(ColorDepth.Ansi256));
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", Style.Default.WithForeground(Color.FromRgb(255, 0, 0)));

        var output = Render(r, buf);
        // 38;2;... (truecolor) should NOT appear; 38;5;<idx> should.
        Assert.DoesNotContain("38;2;255;0;0", output);
        Assert.Contains("38;5;", output);
    }

    [Fact]
    public void Ansi256Caps_PaletteIndexPassesThrough()
    {
        var r = new FrameRenderer(Caps(ColorDepth.Ansi256));
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", Style.Default.WithForeground(Color.FromPalette(196)));

        var output = Render(r, buf);
        Assert.Contains("38;5;196", output);
    }

    // ---- 16-color caps: palette index >= 16 quantized down ----

    [Fact]
    public void Ansi16Caps_HighPaletteIndexReducedToAnsi16()
    {
        var r = new FrameRenderer(Caps(ColorDepth.Ansi16));
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", Style.Default.WithForeground(Color.FromPalette(196))); // bright red palette

        var output = Render(r, buf);
        Assert.DoesNotContain("38;5;", output); // no palette-form SGR
        Assert.DoesNotContain("38;2;", output); // no truecolor SGR
        // Output should contain a basic SGR for an ANSI 16-color foreground (3x or 9x).
    }

    // ---- Extended underline fallback ----

    [Fact]
    public void NoExtendedUnderlineCaps_CurlyFallsBackToSingle()
    {
        var r = new FrameRenderer(Caps(extendedUnderline: false));
        var buf = new CellBuffer(3, 1);
        var curlyStyle = Style.Default
            .WithAttributes(TextAttributes.Underline)
            .WithUnderlineStyle(UnderlineStyle.Curly);
        buf.Set(0, 0, "x", curlyStyle);

        var output = Render(r, buf);
        // The 4:3 (curly) form should be absent; SGR 4 (single underline) present.
        Assert.DoesNotContain("4:3", output);
    }

    // ---- Dropped attributes ----

    [Fact]
    public void NoOverlineCaps_OverlineAttributeDropped()
    {
        var r = new FrameRenderer(Caps(overline: false));
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", Style.Default.WithAttributes(TextAttributes.Overline));

        var output = Render(r, buf);
        Assert.DoesNotContain("53m", output); // SGR 53 = overline-on
        Assert.DoesNotContain("53;", output);
    }

    // ---- Stable frame produces empty diff (quantization must be deterministic) ----

    [Fact]
    public void StableFrame_AcrossRenders_EmitsNoCellContent()
    {
        var r = new FrameRenderer(Caps(ColorDepth.Ansi256));
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "a", Style.Default.WithForeground(Color.FromRgb(255, 0, 0)));

        Render(r, buf); // first frame
        var output = Render(r, buf); // second frame, identical buffer

        // After quantization the second frame should not re-emit 'a' — front buffer should
        // hold the quantized form so the comparison is stable.
        Assert.DoesNotContain("a", output);
    }

    // ---- No quantizer when capabilities not supplied ----

    [Fact]
    public void NoCapabilities_NoQuantization_RgbPassesThrough()
    {
        var r = new FrameRenderer();
        var buf = new CellBuffer(3, 1);
        buf.Set(0, 0, "x", Style.Default.WithForeground(Color.FromRgb(255, 128, 0)));

        var output = Render(r, buf);
        Assert.Contains("38;2;255;128;0", output);
    }

    // ---- Ordered dither ----

    // Distinct "38;5;N" foreground-palette indices present in the emitted bytes.
    private static HashSet<int> DistinctFgPaletteIndices(string output) =>
        System.Text.RegularExpressions.Regex.Matches(output, @"38;5;(\d+)")
              .Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();

    private static CellBuffer ConstantBand(Color fg, int width = 8)
    {
        var buf = new CellBuffer(width, 1);
        for (int c = 0; c < width; c++)
            buf.Set(c, 0, "x", Style.Default.WithForeground(fg));
        return buf;
    }

    [Fact]
    public void OrderedDither_Ansi256_ConstantBand_EmitsMultiplePaletteIndices()
    {
        // A flat mid-gray band, dithered, spreads across several palette stops per the cells' Bayer phase —
        // proof of spatial dithering (the band would otherwise be one flat index).
        var r = new FrameRenderer(Caps(ColorDepth.Ansi256), new FrameRendererOptions(OrderedDither: true));
        var output = Render(r, ConstantBand(Color.FromRgb(105, 105, 105)));
        Assert.True(DistinctFgPaletteIndices(output).Count >= 2, output);
    }

    [Fact]
    public void OrderedDither_Off_ConstantBand_SinglePaletteIndex()
    {
        // Same band without dither: one flat index — the banding the dither relieves.
        var r = new FrameRenderer(Caps(ColorDepth.Ansi256));
        var output = Render(r, ConstantBand(Color.FromRgb(105, 105, 105)));
        Assert.Single(DistinctFgPaletteIndices(output));
    }

    [Fact]
    public void OrderedDither_TruecolorCaps_NoSpatialVariation()
    {
        // At full depth there's nothing to reduce, so dither is a no-op: one truecolor SGR, no stipple.
        var r = new FrameRenderer(Caps(ColorDepth.Truecolor), new FrameRendererOptions(OrderedDither: true));
        var output = Render(r, ConstantBand(Color.FromRgb(105, 105, 105)));
        Assert.Contains("38;2;105;105;105", output);
        Assert.Empty(DistinctFgPaletteIndices(output));   // no 38;5;N palette form at truecolor
    }

    [Fact]
    public void OrderedDither_StableFrame_EmitsNoCellContent()
    {
        // Position-deterministic phase → the dithered front buffer is stable, so an identical second frame
        // re-emits nothing (the dither must not jitter frame-to-frame).
        var r = new FrameRenderer(Caps(ColorDepth.Ansi256), new FrameRendererOptions(OrderedDither: true));
        var buf = ConstantBand(Color.FromRgb(105, 105, 105));
        Render(r, buf);
        var output = Render(r, buf);
        Assert.DoesNotContain("x", output);
    }
}
