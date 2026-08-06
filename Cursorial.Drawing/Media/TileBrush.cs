using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Imaging;
using Cursorial.Rendering.Media;

namespace Cursorial.Drawing.Media;

/// <summary>
/// A brush that tiles a source image across the paint bounds. The image fills a tile of
/// <see cref="TileSize"/> cells, anchored at the bounds origin; <see cref="TileMode"/> governs cells beyond
/// the first tile (repeat, mirror, or transparent). Sampling, opacity, and alpha semantics match
/// <see cref="ImageBrush"/>.
/// </summary>
public sealed class TileBrush : IBrush
{
    private readonly DecodedImage _image;
    private readonly double _opacity;

    /// <summary>Tile <paramref name="image"/> in <paramref name="tileSize"/>-cell tiles.</summary>
    /// <exception cref="ArgumentException">The image's pixel buffer is invalid (see <see cref="ImageBrush"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tileSize"/> has a dimension &lt; 1, or
    /// <paramref name="opacity"/> is not finite.</exception>
    public TileBrush(DecodedImage image,
                     Size tileSize,
                     TileMode tileMode = TileMode.Tile,
                     BrushInterpolation interpolation = BrushInterpolation.Bilinear,
                     double opacity = 1.0)
    {
        ImageBrush.ValidateImage(image);
        if (tileSize.Columns < 1 || tileSize.Rows < 1)
            throw new ArgumentOutOfRangeException(nameof(tileSize), tileSize, "Tile size must be at least 1×1 cells.");
        if (!double.IsFinite(opacity))
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be a finite value.");

        _image = image;
        TileSize = tileSize;
        TileMode = tileMode;
        Interpolation = interpolation;
        _opacity = Math.Clamp(opacity, 0.0, 1.0);
    }

    /// <summary>Decode <paramref name="pngBytes"/> (PNG) and tile it. See <see cref="ImageBrush.FromPng"/>.</summary>
    public static TileBrush FromPng(ReadOnlySpan<byte> pngBytes, Size tileSize,
                                    TileMode tileMode = TileMode.Tile,
                                    BrushInterpolation interpolation = BrushInterpolation.Bilinear,
                                    double opacity = 1.0) =>
        new(PngDecoder.Decode(pngBytes), tileSize, tileMode, interpolation, opacity);

    /// <summary>The tile footprint in cells (each dimension ≥ 1).</summary>
    public Size TileSize { get; }

    /// <summary>How cells beyond the first tile are filled.</summary>
    public TileMode TileMode { get; }

    /// <summary>Texel filtering used when a cell falls between source pixels.</summary>
    public BrushInterpolation Interpolation { get; }

    /// <summary>The opacity (0–1) folded into every sampled alpha.</summary>
    public double Opacity => _opacity;

    /// <inheritdoc/>
    public Color ColorAt(int column, int row, Rect bounds)
    {
        if (bounds.Columns <= 0 || bounds.Rows <= 0) return Colors.Transparent;

        int localCol = column - bounds.Column, localRow = row - bounds.Row;
        int tileW = TileSize.Columns, tileH = TileSize.Rows;

        int tileX = localCol / tileW, tileY = localRow / tileH;
        if (TileMode == TileMode.None && (tileX > 0 || tileY > 0)) return Colors.Transparent;

        // Cell center within its tile, normalized to [0,1).
        double u = (localCol - tileX * tileW + 0.5) / tileW;
        double v = (localRow - tileY * tileH + 0.5) / tileH;

        // Mirror alternate tiles so a flipped pattern is seamless across the tile seam.
        if ((TileMode == TileMode.FlipX || TileMode == TileMode.FlipXY) && (tileX & 1) == 1) u = 1.0 - u;
        if ((TileMode == TileMode.FlipY || TileMode == TileMode.FlipXY) && (tileY & 1) == 1) v = 1.0 - v;

        var color = ImageSampler.Sample(_image, u * _image.Width, v * _image.Height, Interpolation);
        return ImageBrush.ApplyOpacity(color, _opacity);
    }
}
