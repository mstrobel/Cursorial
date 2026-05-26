namespace Cursorial.Rendering.Imaging;

/// <summary>
/// Resampling filter family. Higher-quality filters use wider support windows and cost more.
/// </summary>
public enum ResampleFilter
{
    /// <summary>Nearest-neighbor — pick the single closest source pixel. Fastest; blocky.
    /// Preserves crisp pixel art when downscaling by integer factors.</summary>
    Nearest,

    /// <summary>Box filter — average over the source pixels covered by each destination pixel's
    /// footprint. Cheap and produces a clean result for integer-factor downscales but blurs
    /// detail and aliases at fractional factors.</summary>
    Box,

    /// <summary>Triangle (a.k.a. bilinear) — linear blend between adjacent source pixels.
    /// Reasonable cost / quality middle ground; loses high-frequency detail.</summary>
    Triangle,

    /// <summary>Mitchell–Netravali cubic with the standard <c>B = C = 1/3</c> parameters.
    /// Good general-purpose default — smoother than Lanczos with less ringing.</summary>
    Mitchell,

    /// <summary>Two-lobe Lanczos (windowed sinc). Sharper than Mitchell with mild ringing on
    /// hard edges; cheaper than <see cref="Lanczos3"/>.</summary>
    Lanczos2,

    /// <summary>Three-lobe Lanczos. The standard high-quality resampling kernel — best preserves
    /// detail and contrast when downsampling photographic / icon content. Mild ringing artifacts
    /// near step edges; the default.</summary>
    Lanczos3,
}

/// <summary>
/// High-quality RGBA image resampler. Supports arbitrary destination dimensions, including
/// upscaling and non-uniform scaling. Filtering is alpha-aware (premultiply → filter →
/// unpremultiply) to avoid color bleed across translucent edges.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm.</b> Separable two-pass convolution: horizontal pass produces an intermediate
/// <c>dstW × srcH</c> buffer; vertical pass produces the final <c>dstW × dstH</c> buffer. For
/// each axis, a per-destination-pixel weight table is computed once (<c>O(dst × support)</c>)
/// and reused across every row / column of the data.
/// </para>
/// <para>
/// <b>Alpha handling.</b> The source is premultiplied (RGB ← RGB·α) before filtering, so the
/// blend respects opacity at edges — a translucent halo around a sharp shape resamples as a
/// soft falloff in the cluster's actual color rather than the source's underlying RGB bleeding
/// out into the transparent region. After both passes the result is unpremultiplied back to
/// straight-alpha RGBA. Fully transparent input pixels (<c>α = 0</c>) round-trip cleanly.
/// </para>
/// <para>
/// <b>Color space.</b> Filtering happens in sRGB space, not linear. This is the wrong thing
/// for color-accurate scaling of photographic content but is the standard pragmatic choice
/// for UI / icon work at the small dimensions terminal output operates at — gamma-correct
/// filtering would double the per-pixel cost (decode + re-encode through the sRGB transfer
/// function) for a difference invisible at typical sizes.
/// </para>
/// <para>
/// <b>Edge handling.</b> Out-of-bounds samples clamp to the nearest-edge pixel. The alternative
/// (wrap / reflect) is appropriate for tileable textures but produces visible artifacts on
/// arbitrary images.
/// </para>
/// </remarks>
public static class ImageResampler
{
    /// <summary>Resample a <see cref="DecodedImage"/> to new dimensions.</summary>
    /// <param name="source">Source image (RGBA, row-major).</param>
    /// <param name="targetWidth">Destination width in pixels.</param>
    /// <param name="targetHeight">Destination height in pixels.</param>
    /// <param name="filter">Resampling filter — defaults to <see cref="ResampleFilter.Lanczos3"/>.</param>
    public static DecodedImage Resample(
        in DecodedImage source,
        int targetWidth,
        int targetHeight,
        ResampleFilter filter = ResampleFilter.Lanczos3)
    {
        ArgumentNullException.ThrowIfNull(source.Rgba);
        var bytes = Resample(source.Rgba, source.Width, source.Height, targetWidth, targetHeight, filter);
        return new DecodedImage(targetWidth, targetHeight, bytes);
    }

    /// <summary>Resample raw RGBA bytes to new dimensions.</summary>
    /// <param name="sourceRgba">Source pixels, 4 bytes per pixel, row-major.</param>
    /// <param name="sourceWidth">Source width in pixels.</param>
    /// <param name="sourceHeight">Source height in pixels.</param>
    /// <param name="targetWidth">Destination width in pixels.</param>
    /// <param name="targetHeight">Destination height in pixels.</param>
    /// <param name="filter">Resampling filter — defaults to <see cref="ResampleFilter.Lanczos3"/>.</param>
    public static byte[] Resample(
        ReadOnlySpan<byte> sourceRgba,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        ResampleFilter filter = ResampleFilter.Lanczos3)
    {
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth), sourceWidth, "Source width must be positive.");
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight), sourceHeight, "Source height must be positive.");
        if (targetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth), targetWidth, "Target width must be positive.");
        if (targetHeight <= 0) throw new ArgumentOutOfRangeException(nameof(targetHeight), targetHeight, "Target height must be positive.");

        int expected = checked(sourceWidth * sourceHeight * 4);
        if (sourceRgba.Length != expected)
            throw new ArgumentException($"sourceRgba length {sourceRgba.Length} doesn't match width × height × 4 = {expected}.", nameof(sourceRgba));

        // Identity case — caller asked for the same dimensions. Copy out (still a fresh array so
        // the caller can mutate freely).
        if (sourceWidth == targetWidth && sourceHeight == targetHeight)
            return sourceRgba.ToArray();

        if (filter is ResampleFilter.Nearest)
            return ResampleNearest(sourceRgba, sourceWidth, sourceHeight, targetWidth, targetHeight);

        return ResampleSeparable(sourceRgba, sourceWidth, sourceHeight, targetWidth, targetHeight, filter);
    }

    // ---- Nearest-neighbor (separate path because it skips the float pipeline entirely) ----

    private static byte[] ResampleNearest(
        ReadOnlySpan<byte> src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        float xScale = (float) srcW / dstW;
        float yScale = (float) srcH / dstH;

        for (int dy = 0; dy < dstH; dy++)
        {
            int sy = Math.Clamp((int) ((dy + 0.5f) * yScale), 0, srcH - 1);
            int srcRow = sy * srcW * 4;
            int dstRow = dy * dstW * 4;

            for (int dx = 0; dx < dstW; dx++)
            {
                int sx = Math.Clamp((int) ((dx + 0.5f) * xScale), 0, srcW - 1);
                int s = srcRow + sx * 4;
                int d = dstRow + dx * 4;
                dst[d + 0] = src[s + 0];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = src[s + 3];
            }
        }

        return dst;
    }

    // ---- Separable convolution pipeline ----

    private static byte[] ResampleSeparable(
        ReadOnlySpan<byte> src, int srcW, int srcH, int dstW, int dstH, ResampleFilter filter)
    {
        // Premultiply alpha and convert to float in one pass. Filtering happens in
        // premultiplied space, so translucent edges don't bleed source color into transparent
        // regions; we'll unpremultiply at the end.
        var premultiplied = new float[srcW * srcH * 4];
        for (int i = 0; i < srcW * srcH; i++)
        {
            float a = src[i * 4 + 3] / 255f;
            premultiplied[i * 4 + 0] = (src[i * 4 + 0] / 255f) * a;
            premultiplied[i * 4 + 1] = (src[i * 4 + 1] / 255f) * a;
            premultiplied[i * 4 + 2] = (src[i * 4 + 2] / 255f) * a;
            premultiplied[i * 4 + 3] = a;
        }

        // Horizontal pass: dstW × srcH intermediate.
        var hWeights = ComputeWeights(srcW, dstW, filter);
        var intermediate = new float[dstW * srcH * 4];
        ApplyHorizontal(premultiplied, srcW, srcH, intermediate, dstW, hWeights);

        // Vertical pass: dstW × dstH final.
        var vWeights = ComputeWeights(srcH, dstH, filter);
        var floatResult = new float[dstW * dstH * 4];
        ApplyVertical(intermediate, dstW, srcH, floatResult, dstH, vWeights);

        // Unpremultiply and quantize back to 8-bit. Clamp values that the filter may have
        // overshot (Lanczos in particular produces small negative values around step edges).
        var result = new byte[dstW * dstH * 4];
        for (int i = 0; i < dstW * dstH; i++)
        {
            float a = Math.Clamp(floatResult[i * 4 + 3], 0f, 1f);
            float r = floatResult[i * 4 + 0];
            float g = floatResult[i * 4 + 1];
            float b = floatResult[i * 4 + 2];

            if (a > 0f)
            {
                float inv = 1f / a;
                r *= inv;
                g *= inv;
                b *= inv;
            }

            result[i * 4 + 0] = ToByte(r);
            result[i * 4 + 1] = ToByte(g);
            result[i * 4 + 2] = ToByte(b);
            result[i * 4 + 3] = ToByte(a);
        }

        return result;
    }

    private static void ApplyHorizontal(
        float[] src, int srcW, int srcH,
        float[] dst, int dstW,
        WeightEntry[] weights)
    {
        for (int row = 0; row < srcH; row++)
        {
            int srcRow = row * srcW * 4;
            int dstRow = row * dstW * 4;

            for (int dx = 0; dx < dstW; dx++)
            {
                var entry = weights[dx];
                float r = 0, g = 0, b = 0, a = 0;
                int srcEnd = srcW - 1;

                for (int k = 0; k < entry.Weights.Length; k++)
                {
                    int sx = entry.Start + k;
                    if (sx < 0) sx = 0;
                    else if (sx > srcEnd) sx = srcEnd;

                    float w = entry.Weights[k];
                    int s = srcRow + sx * 4;
                    r += src[s + 0] * w;
                    g += src[s + 1] * w;
                    b += src[s + 2] * w;
                    a += src[s + 3] * w;
                }

                int d = dstRow + dx * 4;
                dst[d + 0] = r;
                dst[d + 1] = g;
                dst[d + 2] = b;
                dst[d + 3] = a;
            }
        }
    }

    private static void ApplyVertical(
        float[] src, int srcW, int srcH,
        float[] dst, int dstH,
        WeightEntry[] weights)
    {
        for (int dy = 0; dy < dstH; dy++)
        {
            var entry = weights[dy];
            int dstRow = dy * srcW * 4;
            int srcEnd = srcH - 1;

            for (int dx = 0; dx < srcW; dx++)
            {
                float r = 0, g = 0, b = 0, a = 0;

                for (int k = 0; k < entry.Weights.Length; k++)
                {
                    int sy = entry.Start + k;
                    if (sy < 0) sy = 0;
                    else if (sy > srcEnd) sy = srcEnd;

                    float w = entry.Weights[k];
                    int s = (sy * srcW + dx) * 4;
                    r += src[s + 0] * w;
                    g += src[s + 1] * w;
                    b += src[s + 2] * w;
                    a += src[s + 3] * w;
                }

                int d = dstRow + dx * 4;
                dst[d + 0] = r;
                dst[d + 1] = g;
                dst[d + 2] = b;
                dst[d + 3] = a;
            }
        }
    }

    // ---- Weight tables ----

    private readonly record struct WeightEntry(int Start, float[] Weights);

    private static WeightEntry[] ComputeWeights(int srcSize, int dstSize, ResampleFilter filter)
    {
        float filterSupport = FilterSupport(filter);
        float scale = (float) srcSize / dstSize;

        // When downsampling (scale > 1), the filter support has to stretch to cover the source
        // pixels that fall under each destination pixel — otherwise we'd alias. When upsampling
        // (scale < 1), the filter stays at its natural support, and we sample the source more
        // densely.
        float effectiveScale = Math.Max(1f, scale);
        float effectiveSupport = filterSupport * effectiveScale;

        var weights = new WeightEntry[dstSize];

        for (int dst = 0; dst < dstSize; dst++)
        {
            // Center of destination pixel mapped into source coordinates. The +0.5 / -0.5
            // shifts treat pixels as area samples at their centers (not at corners).
            float center = (dst + 0.5f) * scale - 0.5f;
            int start = (int) MathF.Floor(center - effectiveSupport + 0.5f);
            int end = (int) MathF.Floor(center + effectiveSupport - 0.5f);
            if (end < start) end = start;
            int count = end - start + 1;

            var w = new float[count];
            float sum = 0;

            for (int i = 0; i < count; i++)
            {
                float dist = (start + i - center) / effectiveScale;
                float k = EvaluateKernel(filter, dist);
                w[i] = k;
                sum += k;
            }

            // Normalize so the weights for this destination pixel sum to 1.0. Without this,
            // the output's overall brightness drifts away from the source's, especially at
            // the edges where the filter overlaps clamped-edge samples.
            if (sum > 0f)
            {
                float inv = 1f / sum;
                for (int i = 0; i < count; i++)
                    w[i] *= inv;
            }

            weights[dst] = new WeightEntry(start, w);
        }

        return weights;
    }

    // ---- Filter kernels ----

    private static float FilterSupport(ResampleFilter filter) => filter switch
    {
        ResampleFilter.Box      => 0.5f,
        ResampleFilter.Triangle => 1f,
        ResampleFilter.Mitchell => 2f,
        ResampleFilter.Lanczos2 => 2f,
        ResampleFilter.Lanczos3 => 3f,
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unrecognized filter.")
    };

    private static float EvaluateKernel(ResampleFilter filter, float x) => filter switch
    {
        ResampleFilter.Box      => BoxKernel(x),
        ResampleFilter.Triangle => TriangleKernel(x),
        ResampleFilter.Mitchell => MitchellKernel(x),
        ResampleFilter.Lanczos2 => LanczosKernel(x, 2),
        ResampleFilter.Lanczos3 => LanczosKernel(x, 3),
        _ => 0f,
    };

    private static float BoxKernel(float x) =>
        x is >= -0.5f and < 0.5f ? 1f : 0f;

    private static float TriangleKernel(float x)
    {
        x = MathF.Abs(x);
        return x < 1f ? 1f - x : 0f;
    }

    // ReSharper disable InconsistentNaming
    private static float MitchellKernel(float x)
    {
        // Mitchell-Netravali with the standard recommendation B = C = 1/3 (Mitchell's 1988 paper
        // describes the perceptual sweet spot at this point — minimal ringing, no over-blur).
        const float B = 1f / 3f;
        const float C = 1f / 3f;

        x = MathF.Abs(x);
        float x2 = x * x;
        float x3 = x2 * x;

        if (x < 1f)
            return ((12f - 9f * B - 6f * C) * x3 + (-18f + 12f * B + 6f * C) * x2 + (6f - 2f * B)) / 6f;
        if (x < 2f)
            return ((-B - 6f * C) * x3 + (6f * B + 30f * C) * x2 + (-12f * B - 48f * C) * x + (8f * B + 24f * C)) / 6f;
        return 0f;
    }
    // ReSharper restore InconsistentNaming

    private static float LanczosKernel(float x, int a)
    {
        if (x == 0f) return 1f;
        if (x <= -a || x >= a) return 0f;

        float px = MathF.PI * x;
        float sincX = MathF.Sin(px) / px;
        float pxOverA = px / a;
        float windowed = MathF.Sin(pxOverA) / pxOverA;
        return sincX * windowed;
    }

    // ---- Byte conversion ----

    private static byte ToByte(float v)
    {
        // Round-to-nearest with clamp. Negative values come from the windowed-sinc overshoot
        // around step edges; values above 1 from the equivalent over-shoot on the bright side.
        int n = (int) MathF.Round(v * 255f);
        if (n < 0) return 0;
        if (n > 255) return 255;
        return (byte) n;
    }
}
