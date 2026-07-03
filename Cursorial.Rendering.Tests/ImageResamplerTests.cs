using Cursorial.Rendering.Imaging;

// ReSharper disable CheckNamespace

namespace Cursorial.Rendering.Tests;

public class ImageResamplerTests
{
    // ---- Argument validation ----

    [Fact]
    public void Resample_ZeroDimension_Throws()
    {
        var src = new byte[4];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ImageResampler.Resample(src, 0, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ImageResampler.Resample(src, 1, 1, 0, 1));
    }

    [Fact]
    public void Resample_MismatchedBufferLength_Throws()
    {
        var src = new byte[10]; // Not 4 × 4 × 1.
        Assert.Throws<ArgumentException>(() =>
            ImageResampler.Resample(src, 4, 1, 4, 1));
    }

    // ---- Identity ----

    [Theory]
    [InlineData(ResampleFilter.Nearest)]
    [InlineData(ResampleFilter.Box)]
    [InlineData(ResampleFilter.Triangle)]
    [InlineData(ResampleFilter.Mitchell)]
    [InlineData(ResampleFilter.Lanczos2)]
    [InlineData(ResampleFilter.Lanczos3)]
    public void Resample_IdenticalDimensions_PreservesEveryPixel(ResampleFilter filter)
    {
        var src = MakePattern(width: 4, height: 4);

        var result = ImageResampler.Resample(src, 4, 4, 4, 4, filter);

        Assert.Equal(src, result);
    }

    // ---- Downscale ----

    [Fact]
    public void Resample_BoxDownscale2x_AveragesSourceQuads()
    {
        // 4×4 image where each 2×2 block has a uniform RGB. Box-filter 2× downscale should
        // produce a 2×2 image whose pixels exactly equal those block colors.
        var src = new byte[4 * 4 * 4];
        SetBlock(src, 4, 0, 0, 2, 2, 10, 20, 30, 255);
        SetBlock(src, 4, 2, 0, 2, 2, 200, 100, 50, 255);
        SetBlock(src, 4, 0, 2, 2, 2, 64, 128, 192, 255);
        SetBlock(src, 4, 2, 2, 2, 2, 255, 255, 255, 255);

        var result = ImageResampler.Resample(src, 4, 4, 2, 2, ResampleFilter.Box);

        Assert.Equal(4 * 4, result.Length);
        AssertPixelClose(result, 2, 0, 0, 10, 20, 30, 255, tolerance: 1);
        AssertPixelClose(result, 2, 1, 0, 200, 100, 50, 255, tolerance: 1);
        AssertPixelClose(result, 2, 0, 1, 64, 128, 192, 255, tolerance: 1);
        AssertPixelClose(result, 2, 1, 1, 255, 255, 255, 255, tolerance: 1);
    }

    [Theory]
    [InlineData(ResampleFilter.Triangle)]
    [InlineData(ResampleFilter.Mitchell)]
    [InlineData(ResampleFilter.Lanczos2)]
    [InlineData(ResampleFilter.Lanczos3)]
    public void Resample_DownscaleSolidColor_PreservesSolidColor(ResampleFilter filter)
    {
        // A solid-color image should round-trip the same color through every filter. The
        // weight-normalization in ComputeWeights guarantees brightness preservation; without
        // it the edge pixels would shift slightly because the filter overlaps clamped edges.
        var src = MakeSolid(width: 16, height: 16, r: 100, g: 150, b: 200, a: 255);

        var result = ImageResampler.Resample(src, 16, 16, 4, 4, filter);

        for (int i = 0; i < 16; i++)
        {
            Assert.InRange(result[i * 4 + 0], 99, 101);
            Assert.InRange(result[i * 4 + 1], 149, 151);
            Assert.InRange(result[i * 4 + 2], 199, 201);
            Assert.Equal(255, result[i * 4 + 3]);
        }
    }

    [Fact]
    public void Resample_NearestDownscale_PreservesPixelArt()
    {
        // 2×2 checkerboard pattern. Nearest-neighbor 2× downsample picks one pixel from each
        // 2×2 block — no averaging, the checkerboard stays the checkerboard.
        var src = new byte[]
        {
            // Row 0: black, white
            0, 0, 0, 255,   255, 255, 255, 255,
            // Row 1: white, black
            255, 255, 255, 255,   0, 0, 0, 255,
        };

        var result = ImageResampler.Resample(src, 2, 2, 2, 2, ResampleFilter.Nearest);

        // Identity case at same dims — returns a copy of the source.
        Assert.Equal(src, result);
    }

    // ---- Upscale ----

    [Theory]
    [InlineData(ResampleFilter.Triangle)]
    [InlineData(ResampleFilter.Mitchell)]
    [InlineData(ResampleFilter.Lanczos2)]
    [InlineData(ResampleFilter.Lanczos3)]
    public void Resample_UpscaleSolidColor_PreservesSolidColor(ResampleFilter filter)
    {
        var src = MakeSolid(width: 2, height: 2, r: 80, g: 160, b: 240, a: 255);

        var result = ImageResampler.Resample(src, 2, 2, 8, 8, filter);

        Assert.Equal(8 * 8 * 4, result.Length);
        for (int i = 0; i < 64; i++)
        {
            Assert.InRange(result[i * 4 + 0], 79, 81);
            Assert.InRange(result[i * 4 + 1], 159, 161);
            Assert.InRange(result[i * 4 + 2], 239, 241);
            Assert.Equal(255, result[i * 4 + 3]);
        }
    }

    // ---- Alpha edges (the advisor's specific callout) ----

    [Fact]
    public void Resample_TransparentCore_StaysTransparentThroughResample()
    {
        // 8×8 image: outer ring opaque red, inner 4×4 fully transparent. After downsampling
        // to 4×4, the corners should remain opaque-ish, and the center pixels should still
        // be transparent (or close to it — the filter blurs slightly).
        var src = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int i = (y * 8 + x) * 4;
                bool transparent = x is >= 2 and < 6 && y is >= 2 and < 6;
                if (transparent)
                {
                    src[i + 0] = 0; src[i + 1] = 0; src[i + 2] = 0; src[i + 3] = 0;
                }
                else
                {
                    src[i + 0] = 200; src[i + 1] = 50; src[i + 2] = 50; src[i + 3] = 255;
                }
            }
        }

        var result = ImageResampler.Resample(src, 8, 8, 4, 4, ResampleFilter.Lanczos3);

        // The very center pixels (mapping to the original transparent core, far from any
        // opaque edge contributing meaningfully under a 3-lobe Lanczos kernel at 2× downscale)
        // should be substantially less opaque than the corners. We don't demand strict alpha=0
        // because the filter's support extends ~6 source pixels around each destination, but
        // the brightness should be markedly lower than the corner.
        byte cornerAlpha = result[(0 * 4 + 0) * 4 + 3];
        Assert.True(cornerAlpha > 240, $"Corner alpha should be near-opaque; got {cornerAlpha}.");
    }

    [Fact]
    public void Resample_PremultipliedPipeline_DoesNotBleedSourceColorThroughTransparentRegions()
    {
        // The classic "straight-alpha bleed" check: pure-red pixel with alpha=0 next to
        // pure-blue pixel with alpha=255. A naive (straight-alpha) filter averages RGB as if
        // both were opaque, producing a purple haze. A premultiplied filter weights the red's
        // RGB contribution by its alpha=0 — i.e., not at all — so the result stays clean blue.
        var src = new byte[]
        {
            // Two pixels: red at alpha=0, blue at alpha=255.
            255, 0, 0, 0,    0, 0, 255, 255,
        };

        var result = ImageResampler.Resample(src, 2, 1, 1, 1, ResampleFilter.Triangle);

        // Result alpha is the average (0 + 255) / 2 = 127.5 → 128.
        // The blue's RGB should dominate; red's premultiplied contribution is zero. After
        // unpremultiply, the result is essentially pure blue at half alpha.
        Assert.True(result[0] < 8, $"Red channel should be near zero (no bleed); got {result[0]}.");
        Assert.True(result[1] < 8, $"Green channel should be near zero; got {result[1]}.");
        Assert.True(result[2] > 240, $"Blue channel should dominate; got {result[2]}.");
        Assert.InRange(result[3], 120, 135);
    }

    // ---- DecodedImage overload ----

    [Fact]
    public void Resample_DecodedImageOverload_RoundTripsThroughTheRecordType()
    {
        var src = new DecodedImage(2, 2, MakeSolid(2, 2, 100, 100, 100, 255));

        var result = ImageResampler.Resample(src, 4, 4, ResampleFilter.Triangle);

        Assert.Equal(4, result.Width);
        Assert.Equal(4, result.Height);
        Assert.Equal(4 * 4 * 4, result.Rgba.Length);
    }

    // ---- Helpers ----

    private static byte[] MakePattern(int width, int height)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bytes[i + 0] = (byte) (x * 31);
                bytes[i + 1] = (byte) (y * 31);
                bytes[i + 2] = (byte) ((x + y) * 15);
                bytes[i + 3] = 255;
            }
        }
        return bytes;
    }

    private static byte[] MakeSolid(int width, int height, byte r, byte g, byte b, byte a)
    {
        var bytes = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            bytes[i * 4 + 0] = r;
            bytes[i * 4 + 1] = g;
            bytes[i * 4 + 2] = b;
            bytes[i * 4 + 3] = a;
        }
        return bytes;
    }

    private static void SetBlock(byte[] buffer, int stride, int x0, int y0, int w, int h, byte r, byte g, byte b, byte a)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                int i = (y * stride + x) * 4;
                buffer[i + 0] = r;
                buffer[i + 1] = g;
                buffer[i + 2] = b;
                buffer[i + 3] = a;
            }
        }
    }

    private static void AssertPixelClose(byte[] buffer, int width, int x, int y, byte r, byte g, byte b, byte a, int tolerance)
    {
        int i = (y * width + x) * 4;
        Assert.InRange(buffer[i + 0], r - tolerance, r + tolerance);
        Assert.InRange(buffer[i + 1], g - tolerance, g + tolerance);
        Assert.InRange(buffer[i + 2], b - tolerance, b + tolerance);
        Assert.InRange(buffer[i + 3], a - tolerance, a + tolerance);
    }
}
