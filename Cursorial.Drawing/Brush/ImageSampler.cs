using Cursorial.Output;
using Cursorial.Rendering.Imaging;

namespace Cursorial.Drawing;

/// <summary>
/// Samples a decoded RGBA image at fractional pixel coordinates, shared by <see cref="ImageBrush"/> and
/// <see cref="TileBrush"/>. Bilinear filtering blends through <see cref="Color.Lerp"/> — the same
/// premultiplied-sRGB interpolation gradients use — so the four-texel average is correct without
/// re-deriving premultiplied math here.
/// </summary>
internal static class ImageSampler
{
    /// <summary>
    /// Sample <paramref name="image"/> at pixel coordinate (<paramref name="px"/>, <paramref name="py"/>),
    /// where pixel centers sit at integer + 0.5. Out-of-range coordinates clamp to the edge texel. Returns a
    /// straight (non-premultiplied) <see cref="Color"/>; transparent (alpha 0) when the image is empty.
    /// </summary>
    public static Color Sample(in DecodedImage image, double px, double py, BrushInterpolation interpolation)
    {
        int w = image.Width, h = image.Height;
        if (w <= 0 || h <= 0) return Colors.Transparent;

        if (interpolation == BrushInterpolation.NearestNeighbor)
        {
            int x = Math.Clamp((int) Math.Floor(px), 0, w - 1);
            int y = Math.Clamp((int) Math.Floor(py), 0, h - 1);
            return Texel(image, x, y);
        }

        // Bilinear: the four texels around (px, py). Texel centers are at i+0.5, so the lower-left neighbor
        // index is floor(p - 0.5) and the sub-texel weight is the fractional remainder.
        double fx = px - 0.5, fy = py - 0.5;
        int x0 = (int) Math.Floor(fx), y0 = (int) Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0;

        int x0c = Math.Clamp(x0, 0, w - 1), x1c = Math.Clamp(x0 + 1, 0, w - 1);
        int y0c = Math.Clamp(y0, 0, h - 1), y1c = Math.Clamp(y0 + 1, 0, h - 1);

        var top = Color.Lerp(Texel(image, x0c, y0c), Texel(image, x1c, y0c), tx);
        var bottom = Color.Lerp(Texel(image, x0c, y1c), Texel(image, x1c, y1c), tx);
        return Color.Lerp(top, bottom, ty);
    }

    private static Color Texel(in DecodedImage image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        byte[] rgba = image.Rgba;
        return Color.FromRgba(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }
}
