namespace Cursorial.Rendering.Fragments;

/// <summary>
/// The output of a quantization pass: a palette of distinct <see cref="SixelColor"/>s plus a
/// parallel index buffer mapping each source pixel to a palette entry.
/// </summary>
public readonly record struct QuantizedImage(byte[] IndexedPixels, SixelColor[] Palette);

/// <summary>
/// Median-cut color quantizer that reduces an RGBA pixel buffer to a palette of at most
/// <see cref="SixelEncoder.MaxPaletteSize"/> colors plus a parallel index buffer.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm: collect distinct RGB triplets (alpha is dropped — Sixel can't represent
/// translucency, so callers should pre-composite against a known background), recursively split
/// the smallest enclosing axis-aligned box along its widest channel at the median of that channel,
/// stop when the box queue holds the target color count or every remaining box is unsplittable
/// (single color). Each final box's representative color is the channel-wise average of the
/// pixels it contains, weighted by occurrence count.
/// </para>
/// <para>
/// Index assignment uses a flat nearest-neighbor scan over the final palette under squared
/// Euclidean distance. For 256-color palettes on typical UI imagery (icons, thumbnails) this is
/// fast enough and avoids the kd-tree machinery's complexity. For megapixel inputs a k-d tree
/// or an octree-backed search would pay back; not needed for v1.
/// </para>
/// <para>
/// The quantizer is allocation-heavy by design — palette / index buffers are caller-owned, but
/// the working sets (pixel boxes, channel histograms) live on the GC heap because they're
/// dimensioned by input size, not bounded.
/// </para>
/// </remarks>
public static class MedianCutQuantizer
{
    /// <summary>
    /// Quantize <paramref name="rgbaPixels"/> (row-major RGBA, 4 bytes per pixel) into a palette
    /// of at most <paramref name="maxColors"/> entries plus an index buffer. The alpha channel
    /// is ignored — callers should pre-composite translucent inputs against a known background.
    /// </summary>
    public static QuantizedImage Quantize(ReadOnlySpan<byte> rgbaPixels, int width, int height, int maxColors)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        if (maxColors is < 1 or > SixelEncoder.MaxPaletteSize)
            throw new ArgumentOutOfRangeException(nameof(maxColors), maxColors, $"maxColors must be in 1..{SixelEncoder.MaxPaletteSize}.");
        int expectedBytes = checked(width * height * 4);
        if (rgbaPixels.Length != expectedBytes)
            throw new ArgumentException($"rgbaPixels length {rgbaPixels.Length} doesn't match width × height × 4 = {expectedBytes}.", nameof(rgbaPixels));

        int pixelCount = width * height;

        // Strip alpha into a packed RGB-triplet buffer to keep the working sets compact.
        var rgb = new byte[pixelCount * 3];
        for (int i = 0; i < pixelCount; i++)
        {
            rgb[i * 3 + 0] = rgbaPixels[i * 4 + 0];
            rgb[i * 3 + 1] = rgbaPixels[i * 4 + 1];
            rgb[i * 3 + 2] = rgbaPixels[i * 4 + 2];
        }

        // Build the initial box covering every pixel.
        var initial = new Box(Enumerable.Range(0, pixelCount).ToArray(), rgb);

        var boxes = new List<Box> { initial };
        while (boxes.Count < maxColors)
        {
            int splitIndex = -1;
            int maxRange = -1;
            for (int i = 0; i < boxes.Count; i++)
            {
                int range = boxes[i].LongestRange;
                if (range > maxRange)
                {
                    maxRange = range;
                    splitIndex = i;
                }
            }

            if (splitIndex < 0 || maxRange <= 0)
                break; // Every remaining box is a single color — no further splits possible.

            var (left, right) = boxes[splitIndex].Split(rgb);
            boxes[splitIndex] = left;
            boxes.Add(right);
        }

        var palette = new SixelColor[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
            palette[i] = boxes[i].AverageColor(rgb);

        var indices = new byte[pixelCount];
        for (int p = 0; p < pixelCount; p++)
        {
            int r = rgb[p * 3 + 0];
            int g = rgb[p * 3 + 1];
            int b = rgb[p * 3 + 2];

            int bestIdx = 0;
            int bestDist = int.MaxValue;
            for (int i = 0; i < palette.Length; i++)
            {
                int dr = r - palette[i].R;
                int dg = g - palette[i].G;
                int db = b - palette[i].B;
                int dist = dr * dr + dg * dg + db * db;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                    if (dist == 0) break;
                }
            }

            indices[p] = (byte) bestIdx;
        }

        return new QuantizedImage(indices, palette);
    }

    /// <summary>
    /// A subset of pixel indices and the channel ranges they cover. Mutable in the sense that
    /// callers replace it with split children, but each instance is itself immutable.
    /// </summary>
    private readonly struct Box
    {
        public readonly int[] PixelIndices;
        public readonly byte MinR, MinG, MinB;
        public readonly byte MaxR, MaxG, MaxB;

        public Box(int[] pixelIndices, ReadOnlySpan<byte> rgb)
        {
            PixelIndices = pixelIndices;
            byte minR = 255, minG = 255, minB = 255;
            byte maxR = 0, maxG = 0, maxB = 0;
            foreach (int p in pixelIndices)
            {
                byte r = rgb[p * 3 + 0];
                byte g = rgb[p * 3 + 1];
                byte b = rgb[p * 3 + 2];
                if (r < minR) minR = r;
                if (g < minG) minG = g;
                if (b < minB) minB = b;
                if (r > maxR) maxR = r;
                if (g > maxG) maxG = g;
                if (b > maxB) maxB = b;
            }

            MinR = minR; MinG = minG; MinB = minB;
            MaxR = maxR; MaxG = maxG; MaxB = maxB;
        }

        public int LongestRange
        {
            get
            {
                int r = MaxR - MinR;
                int g = MaxG - MinG;
                int b = MaxB - MinB;
                return Math.Max(r, Math.Max(g, b));
            }
        }

        public (Box Left, Box Right) Split(ReadOnlySpan<byte> rgb)
        {
            int rangeR = MaxR - MinR;
            int rangeG = MaxG - MinG;
            int rangeB = MaxB - MinB;
            int channel = 0;
            if (rangeG >= rangeR && rangeG >= rangeB) channel = 1;
            else if (rangeB >= rangeR && rangeB >= rangeG) channel = 2;

            // Sort pixel indices by the selected channel, split at the median.
            byte[] keys = new byte[PixelIndices.Length];
            for (int i = 0; i < PixelIndices.Length; i++)
                keys[i] = rgb[PixelIndices[i] * 3 + channel];

            int[] sorted = (int[]) PixelIndices.Clone();
            Array.Sort(keys, sorted);

            int mid = sorted.Length / 2;
            var leftArr = sorted.AsSpan(0, mid).ToArray();
            var rightArr = sorted.AsSpan(mid).ToArray();
            return (new Box(leftArr, rgb), new Box(rightArr, rgb));
        }

        public SixelColor AverageColor(ReadOnlySpan<byte> rgb)
        {
            long sumR = 0, sumG = 0, sumB = 0;
            foreach (int p in PixelIndices)
            {
                sumR += rgb[p * 3 + 0];
                sumG += rgb[p * 3 + 1];
                sumB += rgb[p * 3 + 2];
            }

            int n = PixelIndices.Length;
            return new SixelColor((byte) (sumR / n), (byte) (sumG / n), (byte) (sumB / n));
        }
    }
}
