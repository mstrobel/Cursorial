using Cursorial.Rendering.Fragments;

namespace Cursorial.Tests.Rendering;

public class MedianCutQuantizerTests
{
    private static byte[] Rgba(params (byte R, byte G, byte B)[] pixels)
    {
        var buffer = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            buffer[i * 4 + 0] = pixels[i].R;
            buffer[i * 4 + 1] = pixels[i].G;
            buffer[i * 4 + 2] = pixels[i].B;
            buffer[i * 4 + 3] = 255;
        }

        return buffer;
    }

    [Fact]
    public void Quantize_ZeroWidth_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MedianCutQuantizer.Quantize([], 0, 1, 16));
    }

    [Fact]
    public void Quantize_MaxColorsOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MedianCutQuantizer.Quantize(Rgba((0, 0, 0)), 1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MedianCutQuantizer.Quantize(Rgba((0, 0, 0)), 1, 1, SixelEncoder.MaxPaletteSize + 1));
    }

    [Fact]
    public void Quantize_PixelCountMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MedianCutQuantizer.Quantize(new byte[3], 1, 1, 16));
    }

    [Fact]
    public void Quantize_SingleColor_ReturnsOneEntryPalette()
    {
        var rgba = Rgba((128, 64, 200), (128, 64, 200), (128, 64, 200), (128, 64, 200));
        var result = MedianCutQuantizer.Quantize(rgba, 2, 2, 16);

        Assert.Single(result.Palette);
        Assert.Equal(new SixelColor(128, 64, 200), result.Palette[0]);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, result.IndexedPixels);
    }

    [Fact]
    public void Quantize_TwoDistinctColors_BothPreservedExactly()
    {
        var rgba = Rgba((255, 0, 0), (0, 255, 0));
        var result = MedianCutQuantizer.Quantize(rgba, 2, 1, 16);

        // Palette holds both colors (order isn't strictly defined; both must appear).
        Assert.Equal(2, result.Palette.Length);
        Assert.Contains(new SixelColor(255, 0, 0), result.Palette);
        Assert.Contains(new SixelColor(0, 255, 0), result.Palette);

        // Indices point at the right palette entries.
        int redIdx = Array.IndexOf(result.Palette, new SixelColor(255, 0, 0));
        int greenIdx = Array.IndexOf(result.Palette, new SixelColor(0, 255, 0));
        Assert.Equal(redIdx, result.IndexedPixels[0]);
        Assert.Equal(greenIdx, result.IndexedPixels[1]);
    }

    [Fact]
    public void Quantize_StopsSplittingWhenAllBoxesAreSingleColor()
    {
        // 3 distinct colors, maxColors=16 → palette has exactly 3 entries (further splits impossible).
        var rgba = Rgba((255, 0, 0), (0, 255, 0), (0, 0, 255));
        var result = MedianCutQuantizer.Quantize(rgba, 3, 1, 16);

        Assert.Equal(3, result.Palette.Length);
    }

    [Fact]
    public void Quantize_FewerColorsThanTarget_ReturnsAllColors()
    {
        var rgba = Rgba((255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255));
        var result = MedianCutQuantizer.Quantize(rgba, 4, 1, 256);

        Assert.Equal(4, result.Palette.Length);
        Assert.Contains(new SixelColor(255, 0, 0), result.Palette);
        Assert.Contains(new SixelColor(0, 255, 0), result.Palette);
        Assert.Contains(new SixelColor(0, 0, 255), result.Palette);
        Assert.Contains(new SixelColor(255, 255, 255), result.Palette);
    }

    [Fact]
    public void Quantize_ManyColorsCapped_HonorsMaxColors()
    {
        // 8 distinct colors compressed into a 4-entry palette.
        var rgba = Rgba(
            (0, 0, 0), (32, 32, 32),
            (64, 64, 64), (96, 96, 96),
            (128, 128, 128), (160, 160, 160),
            (192, 192, 192), (255, 255, 255));
        var result = MedianCutQuantizer.Quantize(rgba, 8, 1, 4);

        Assert.Equal(4, result.Palette.Length);
        // Every source pixel must map to *some* palette entry.
        foreach (byte idx in result.IndexedPixels)
            Assert.InRange(idx, 0, 3);
    }

    [Fact]
    public void Quantize_NearestNeighborAssignment_PicksClosestPaletteEntry()
    {
        // Two well-separated clusters, pixel near red should map to the red-ish entry.
        var rgba = Rgba(
            (250, 5, 5), (245, 10, 10), (240, 0, 0),    // red cluster
            (5, 5, 250), (10, 10, 245), (0, 0, 240));   // blue cluster
        var result = MedianCutQuantizer.Quantize(rgba, 6, 1, 2);

        Assert.Equal(2, result.Palette.Length);

        // Identify which palette entry is the "red" one by checking which has more red than blue.
        int redIdx = result.Palette[0].R > result.Palette[0].B ? 0 : 1;
        int blueIdx = 1 - redIdx;

        // First 3 pixels (red cluster) → red palette entry. Last 3 (blue cluster) → blue.
        Assert.Equal(redIdx, result.IndexedPixels[0]);
        Assert.Equal(redIdx, result.IndexedPixels[1]);
        Assert.Equal(redIdx, result.IndexedPixels[2]);
        Assert.Equal(blueIdx, result.IndexedPixels[3]);
        Assert.Equal(blueIdx, result.IndexedPixels[4]);
        Assert.Equal(blueIdx, result.IndexedPixels[5]);
    }

    [Fact]
    public void Quantize_OutputFeedsBackIntoEncoder()
    {
        // End-to-end smoke: quantize → encode without throwing, output starts and ends correctly.
        var rgba = Rgba(
            (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0),
            (255, 0, 255), (0, 255, 255), (128, 128, 128), (255, 255, 255));
        var quantized = MedianCutQuantizer.Quantize(rgba, 4, 2, 8);
        var encoded = SixelEncoder.Encode(quantized.IndexedPixels, 4, 2, quantized.Palette);

        Assert.Equal(0x1B, encoded[0]);
        Assert.Equal((byte) 'P', encoded[1]);
        Assert.Equal((byte) '\\', encoded[^1]);
        Assert.Equal(0x1B, encoded[^2]);
    }
}
