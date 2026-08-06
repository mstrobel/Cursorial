using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Imaging;
using Cursorial.Rendering.Media;

namespace Cursorial.Drawing.Media;

/// <summary>
/// A brush that fills a shape or text run by sampling a decoded RGBA image. The image is mapped into the
/// paint <see cref="Rect"/> bounds per <see cref="Stretch"/>, sampled at each cell center, and returned as a
/// straight <see cref="Color"/> whose alpha the compositor consumes — so partially transparent source
/// pixels let the backdrop show through (on the fill path, which stores alpha verbatim; glyph writes
/// consume it at draw time like every other brush). Pure and allocation-free per cell.
/// </summary>
public sealed class ImageBrush : IBrush
{
    private readonly DecodedImage _image;
    private readonly double _opacity;

    /// <summary>Wrap an already-decoded RGBA image (row-major, 4 bytes/pixel).</summary>
    /// <exception cref="ArgumentException">The image's pixel buffer is null or not <c>Width·Height·4</c> bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="opacity"/> is not finite.</exception>
    public ImageBrush(DecodedImage image,
                      Stretch stretch = Stretch.Fill,
                      BrushInterpolation interpolation = BrushInterpolation.Bilinear,
                      double opacity = 1.0)
    {
        ValidateImage(image);
        if (!double.IsFinite(opacity))
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be a finite value.");

        _image = image;
        Stretch = stretch;
        Interpolation = interpolation;
        _opacity = Math.Clamp(opacity, 0.0, 1.0);
    }

    /// <summary>Decode <paramref name="pngBytes"/> (PNG, via Rendering's <see cref="PngDecoder"/>) and wrap it.</summary>
    /// <remarks>Decoding is eager; malformed or unsupported PNGs (paletted / grayscale / non-8-bit / interlaced)
    /// surface the decoder's <see cref="System.NotSupportedException"/> / <see cref="System.IO.InvalidDataException"/>.</remarks>
    public static ImageBrush FromPng(ReadOnlySpan<byte> pngBytes,
                                     Stretch stretch = Stretch.Fill,
                                     BrushInterpolation interpolation = BrushInterpolation.Bilinear,
                                     double opacity = 1.0,
                                     double cellAspectRatio = 1.0) =>
        new(PngDecoder.Decode(pngBytes), stretch, interpolation, opacity) { CellAspectRatio = cellAspectRatio };

    /// <summary>Source image width in pixels.</summary>
    public int PixelWidth => _image.Width;

    /// <summary>Source image height in pixels.</summary>
    public int PixelHeight => _image.Height;

    /// <summary>How the image fits the paint bounds.</summary>
    public Stretch Stretch { get; }

    /// <summary>Texel filtering used when a cell falls between source pixels.</summary>
    public BrushInterpolation Interpolation { get; }

    /// <summary>The opacity (0–1) folded into every sampled alpha.</summary>
    public double Opacity => _opacity;

    /// <summary>
    /// Width-to-height pixel ratio of a terminal cell (≈ 0.5 — a cell is about half as wide as it is tall).
    /// Aspect-corrects <see cref="Stretch.Uniform"/> / <see cref="Stretch.UniformToFill"/> so the image keeps
    /// its true on-screen aspect rather than being squashed by the cell geometry. <c>1.0</c> (default) treats
    /// cells as square (no correction); ignored by <see cref="Stretch.Fill"/> / <see cref="Stretch.None"/>.
    /// </summary>
    public double CellAspectRatio { get; init; } = 1.0;

    /// <inheritdoc/>
    public Color ColorAt(int column, int row, Rect bounds)
    {
        if (bounds.Columns <= 0 || bounds.Rows <= 0) return Colors.Transparent;

        if (!TryMapToSource(column - bounds.Column, row - bounds.Row, bounds.Columns, bounds.Rows,
                            out double px, out double py))
            return Colors.Transparent;

        return ApplyOpacity(ImageSampler.Sample(_image, px, py, Interpolation), _opacity);
    }

    // Map a bounds-local cell to a source-pixel coordinate per Stretch. Returns false for cells that sample
    // nothing (Stretch.None past the image edge, Stretch.Uniform letterbox), which read transparent.
    private bool TryMapToSource(int localCol, int localRow, int cols, int rows, out double px, out double py)
    {
        int w = _image.Width, h = _image.Height;

        switch (Stretch)
        {
            case Stretch.None:
                px = localCol + 0.5;
                py = localRow + 0.5;
                return localCol < w && localRow < h;   // local coords are already ≥ 0

            case Stretch.Fill:
                px = (localCol + 0.5) / cols * w;
                py = (localRow + 0.5) / rows * h;
                return true;

            default:   // Uniform / UniformToFill
            {
                double car = CellAspectRatio > 0.0 ? CellAspectRatio : 1.0;
                double ratio = w / (double) h / car;   // displayed columns ÷ displayed rows to keep true aspect

                // Width-bound when scaling to the full width keeps the height within bounds.
                bool widthBound = cols / ratio <= rows;
                bool useWidth = Stretch == Stretch.Uniform ? widthBound : !widthBound;

                double dispCols = useWidth ? cols : rows * ratio;
                double dispRows = useWidth ? cols / ratio : rows;
                double offCol = (cols - dispCols) / 2.0;
                double offRow = (rows - dispRows) / 2.0;

                double fc = localCol + 0.5 - offCol;
                double fr = localRow + 0.5 - offRow;

                if (Stretch == Stretch.Uniform && (fc < 0 || fc >= dispCols || fr < 0 || fr >= dispRows))
                {
                    px = py = 0;
                    return false;   // letterbox / pillarbox
                }

                px = Math.Clamp(fc / dispCols, 0.0, 1.0) * w;
                py = Math.Clamp(fr / dispRows, 0.0, 1.0) * h;
                return true;
            }
        }
    }

    internal static void ValidateImage(in DecodedImage image)
    {
        if (image.Rgba is null)
            throw new ArgumentException("The image has no pixel data (Rgba is null).", nameof(image));
        if (image.Width < 0 || image.Height < 0 || image.Rgba.Length != image.Width * image.Height * 4)
            throw new ArgumentException(
                $"The image's pixel buffer ({image.Rgba.Length} bytes) does not match {image.Width}×{image.Height}×4.",
                nameof(image));
    }

    internal static Color ApplyOpacity(Color color, double opacity)
    {
        if (opacity >= 1.0 || color.Kind != ColorKind.Rgb) return color;
        double scaled = opacity <= 0.0 ? 0.0 : color.Alpha * opacity;
        return color.WithAlpha((byte) Math.Clamp(Math.Round(scaled), 0, 255));
    }
}
