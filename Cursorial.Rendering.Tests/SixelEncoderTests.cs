using System.Buffers;
using System.Text;

using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Rendering;

public class SixelEncoderTests
{
    private static string Encode(byte[] pixels, int width, int height, params SixelColor[] palette)
    {
        var writer = new ArrayBufferWriter<byte>();
        SixelEncoder.Encode(writer, pixels, width, height, palette);
        return Encoding.ASCII.GetString(writer.WrittenSpan);
    }

    // ---- Validation ----

    [Fact]
    public void Encode_ZeroWidth_Throws()
    {
        var pal = new[] { new SixelColor(255, 0, 0) };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SixelEncoder.Encode([], 0, 1, pal));
    }

    [Fact]
    public void Encode_EmptyPalette_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            SixelEncoder.Encode([0], 1, 1, ReadOnlySpan<SixelColor>.Empty));
    }

    [Fact]
    public void Encode_OversizedPalette_Throws()
    {
        var palette = new SixelColor[SixelEncoder.MaxPaletteSize + 1];
        Assert.Throws<ArgumentException>(() =>
            SixelEncoder.Encode([0], 1, 1, palette));
    }

    [Fact]
    public void Encode_PixelCountMismatch_Throws()
    {
        var pal = new[] { new SixelColor(255, 0, 0) };
        Assert.Throws<ArgumentException>(() =>
            SixelEncoder.Encode([0, 0, 0], 2, 2, pal)); // 3 pixels for 2×2 = 4 expected
    }

    // ---- Envelope structure ----

    [Fact]
    public void Encode_EmitsDcsIntroducerAndStringTerminator()
    {
        var pal = new[] { new SixelColor(255, 0, 0) };
        var output = Encode(new byte[] { 0 }, 1, 1, pal);

        Assert.StartsWith("\x1bP0;1;0q", output); // DCS Pa;Pb;Pc q
        Assert.EndsWith("\x1b\\", output);        // ST
    }

    [Fact]
    public void Encode_EmitsRasterAttributesWithImageDimensions()
    {
        var pal = new[] { new SixelColor(255, 0, 0) };
        var output = Encode(new byte[12], 4, 3, pal); // 4×3 image

        Assert.Contains("\"1;1;4;3", output);
    }

    [Fact]
    public void Encode_EmitsColorRegisterDefinitionsScaledTo0To100()
    {
        // sRGB 255 should scale to 100 (full). 0 stays 0. 127 ≈ 50.
        var pal = new[]
        {
            new SixelColor(255, 0, 0),
            new SixelColor(127, 127, 127),
        };
        var output = Encode(new byte[1], 1, 1, pal);

        Assert.Contains("#0;2;100;0;0", output);
        Assert.Contains("#1;2;50;50;50", output);
    }

    // ---- Pixel data ----

    [Fact]
    public void Encode_SingleColor_AllRowsSet_EmitsTildeForFullBand()
    {
        // 1×6 all the same color → one band, pattern 0b111111 = 0x3F + 63 = 0x7E = '~'.
        var pal = new[] { new SixelColor(255, 0, 0) };
        var output = Encode(new byte[6], 1, 6, pal);

        // After the color selector "#0", a single '~' (no RLE for length-1).
        Assert.Contains("#0~", output);
    }

    [Fact]
    public void Encode_SingleColor_RunOfFourOrMore_UsesRleNotation()
    {
        // 10×6 single color → 10 identical columns, RLE collapses to "!10~".
        var pal = new[] { new SixelColor(255, 0, 0) };
        var output = Encode(new byte[60], 10, 6, pal);

        Assert.Contains("#0!10~", output);
    }

    [Fact]
    public void Encode_RunOfThree_LeavesLiteralBytesNoRle()
    {
        // 3-column run of identical patterns shouldn't be RLE'd (break-even at 4).
        var pal = new[] { new SixelColor(255, 0, 0) };
        var output = Encode(new byte[18], 3, 6, pal); // 3×6 all color 0

        // Expect "~~~" (three literal bytes) rather than "!3~".
        Assert.Contains("#0~~~", output);
        Assert.DoesNotContain("!3~", output);
    }

    [Fact]
    public void Encode_TwoColorsInSameBand_EmitsCarriageReturnBetween()
    {
        // 2×6 image, column 0 = color 0 (all 6 rows), column 1 = color 1 (all 6 rows).
        var pal = new[]
        {
            new SixelColor(255, 0, 0),
            new SixelColor(0, 255, 0),
        };
        var pixels = new byte[12];
        for (int r = 0; r < 6; r++)
        {
            pixels[r * 2 + 0] = 0;
            pixels[r * 2 + 1] = 1;
        }

        var output = Encode(pixels, 2, 6, pal);

        // Color 0 covers column 0 only → pattern '~' at col 0, '?' at col 1.
        // Color 1 covers column 1 only → pattern '?' at col 0, '~' at col 1.
        // Separated by '$' (carriage return) so color 1 paints at the same band start.
        Assert.Contains("#0~?", output);
        Assert.Contains("$#1?~", output);
    }

    [Fact]
    public void Encode_TwoBands_SeparatedByDash()
    {
        // 4×12 image → 2 bands of 6 rows each. Bands separated by '-'.
        var pal = new[] { new SixelColor(255, 0, 0) };
        var output = Encode(new byte[48], 4, 12, pal);

        // Each band emits "#0" then patterns. Two bands → "#0...-...#0" — but actually the
        // encoder emits "#0" once per band, not just once total.
        // Pattern: #0!4~ - #0!4~ ESC \
        Assert.Contains("-", output);
        // Count of '#0' followed by RLE in payload — both bands.
        int bandCount = 0;
        int idx = 0;
        while ((idx = output.IndexOf("#0!4~", idx, StringComparison.Ordinal)) >= 0)
        {
            bandCount++;
            idx += "#0!4~".Length;
        }
        Assert.Equal(2, bandCount);
    }

    [Fact]
    public void Encode_HeightNotMultipleOfSix_LastBandUsesPartialPattern()
    {
        // 1×8 image, all color 0. Two bands: band 0 has 6 rows, band 1 has 2 rows.
        // Band 0 pattern: 0b111111 = 0x3F + 63 = '~'.
        // Band 1 pattern: 0b000011 = 0x3F + 3 = 'B' (0x42).
        var pal = new[] { new SixelColor(255, 0, 0) };
        var output = Encode(new byte[8], 1, 8, pal);

        Assert.Contains("#0~", output);  // full first band
        Assert.Contains("#0B", output);  // partial last band — 2 rows set (bits 0..1)
    }
}
