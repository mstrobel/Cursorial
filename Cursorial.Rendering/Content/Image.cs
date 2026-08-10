using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Imaging;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;

namespace Cursorial.Rendering.Content;

/// <summary>
/// An image drawn at the configured cell footprint. Picks a rendering protocol at paint time
/// based on the terminal's negotiated capabilities — Kitty graphics → iTerm2 inline images
/// → cell-rectangle placeholder when neither is supported.
/// </summary>
/// <remarks>
/// <para>
/// <b>Protocol selection.</b> Kitty's graphics protocol is preferred when both Kitty and iTerm2
/// are available because it supports both PNG and arbitrary placement IDs (useful for layered
/// images in a future iteration). The iTerm2 path is the practical fallback on iTerm2 itself
/// and on WezTerm. Sixel is the fallback of last resort for whichever terminal support it.
/// </para>
/// <para>
/// <b>Format compatibility.</b> Kitty only accepts PNG via the encoded-bytes transmission
/// path; if the supplied <see cref="ImageData.Format"/> is JPEG or GIF and Kitty is the only
/// supported protocol, this content treats Kitty as unsupported and falls through. Callers
/// who need cross-protocol compatibility should either re-encode upstream or supply PNG.
/// </para>
/// <para>
/// <b>Fallback rendering.</b> When no graphics protocol is available, the content paints a
/// cell-rectangle placeholder filled with the configured <see cref="PlaceholderStyle"/>'s
/// background and a centered "[image]" label so the layout's reserved region remains visually
/// obvious. Callers wanting a richer fallback (Braille downscale, ASCII art, etc.) can build
/// their own <see cref="IContent"/> on top of <see cref="KittyImageFragment"/> /
/// <see cref="ITerm2ImageFragment"/> directly.
/// </para>
/// </remarks>
public class Image : FragmentContent
{
    protected internal const int DefaultCellPixelHeight = 20;
    protected internal const int DefaultCellPixelWidth = 10;

    private readonly ImageData? _data;
    private (int Width, int Height)? _resolvedSourcePixelSize;
    private DecodedImage? _resampledImage;

    /// <summary>Construct an image content from the supplied data.</summary>
    public Image(ImageData? data, in CellStyle placeholderStyle = default, string? placeholderText = null)
    {
        _data = data;
        PlaceholderStyle = placeholderStyle;
        PlaceholderText = placeholderText ?? "[p align=center]\\[image\\][/p]";
        Loader = ResourceLoader.Default;
        ResourceUri = null;
        RenderSize = _data?.RequestedSize ?? Size.Empty;
    }

    /// <summary>Construct an image content from the supplied data.</summary>
    public Image(Uri resourceUri, Size renderSize = default, in CellStyle placeholderStyle = default, string? placeholderText = null, IResourceLoader? loader = null)
        : this(LoadImage(resourceUri, renderSize, loader), placeholderStyle, placeholderText)
    {
        ResourceUri = resourceUri;
        RenderSize = renderSize;
        Loader = loader ?? ResourceLoader.Default;
    }

    /// <summary>The URI from which the image bytes were loaded, provided it was loaded by URI.</summary>
    public Uri? ResourceUri { get; }

    /// <summary>The cell size the image paints into.</summary>
    public Size RenderSize { get; }

    /// <summary>The image payload and cell footprint.</summary>
    public ImageData? Data => _data;

    /// <summary>Style applied to the placeholder rectangle when no graphics protocol is supported.</summary>
    public CellStyle PlaceholderStyle { get; init; }

    /// <summary>Text to display when no graphics protocol is supported. For icons, could be an emoji.</summary>
    public string PlaceholderText { get; init; }

    /// <summary>The resource loader that was used to fetch the image bytes.</summary>
    public IResourceLoader Loader { get; private set; }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSpace, OutputCapabilities capabilities, out bool canCreateFragment)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var desiredSize = MeasureImage(availableSpace, capabilities, out _);
        if (desiredSize.IsEmpty)
        {
            canCreateFragment = false;
            return Size.Empty;
        }

        canCreateFragment = true;
        return desiredSize;
    }

    protected Size MeasureImage(Size availableSpace, OutputCapabilities capabilities, out (int Width, int Height) pixelSize)
    {
        // Every protocol — including Sixel — funnels through the same measurement path now that
        // we own a resampler. Previously Sixel had its own branch that computed a "natural"
        // footprint from source pixels ÷ cell pixels because we couldn't honor a requested
        // cell footprint without scaling; CreateFragment now resamples the decoded RGBA to the
        // target pixel dimensions, so the measurement can match the requested size like the
        // Kitty / iTerm2 paths do.
        pixelSize = _resolvedSourcePixelSize ?? ImageSizeHelper.DecodeSize(_data!.Bytes.Span, _data.Format);
        _resolvedSourcePixelSize ??= pixelSize;

        var baseSize = ResolveBaseSize(_data?.RequestedSize, RenderSize);
        return ResolveRenderSize(baseSize, new Rect(0, 0, availableSpace), pixelSize, capabilities);
    }

    /// <inheritdoc/>
    protected override IBufferFragment? CreateFragment(in CellBufferView buffer, in Rect bounds, in CellStyle style, OutputCapabilities capabilities)
    {
        if (capabilities.Graphics is {KittyGraphics: false, ITerm2InlineImages: false, Sixel: false})
            return null;

        var data = Data;
        var baseSize = ResolveBaseSize(data?.RequestedSize, RenderSize);

        // No bytes → no transmittable payload, regardless of capability. This is the case
        // Icon hits when its resource URI didn't resolve: it constructs an Image with empty
        // bytes plus the configured fallback glyph, expecting the placeholder path. Without
        // this guard, a graphics-capable terminal would receive an empty fragment.
        if (data?.Bytes.IsEmpty is not false) return null;

        var effectiveSize = MeasureImage(bounds.Size, capabilities, out var pixelSize);

        // When the caller sized exactly one dimension, mark the OTHER (aspect-derived) dimension as
        // aspect-free so the graphics protocol scales the image to its native aspect in that axis instead
        // of stretching into the rounded whole-cell box. effectiveSize still reserves a whole-cell
        // footprint for layout. The present dimension selects which one is omitted — a cols-only request
        // leaves Rows free, and vice versa. Kitty (omit c=/r=) and iTerm2 (width/height=auto) both honor it.
        var aspectFree = baseSize switch
        {
            { Columns: > 0, Rows: 0 } => AspectFreeDimension.Rows,
            { Columns: 0, Rows: > 0 } => AspectFreeDimension.Columns,
            _ => AspectFreeDimension.None,
        };

        // // Kitty first — supports PNG natively, has the most predictable cell-footprint semantics.
        // Pass the native pixel size so a clip can crop via a source rectangle (Clip → x,y,w,h).
        if (capabilities.Graphics.KittyGraphics && data.Format == ImageFormat.Png)
            return new KittyImageFragment(data, effectiveSize, pixelSize, aspectFree: aspectFree);

        // iTerm2 second — accepts PNG / JPEG / GIF; format hint passes through unchanged.
        if (capabilities.Graphics.ITerm2InlineImages)
            return new ITerm2ImageFragment(data, effectiveSize, pixelSize, aspectFree: aspectFree);

        // Sixel third — PNG only (we don't decode JPEG / GIF). PNG path: decode to RGBA, scale
        // to the target pixel dimensions, quantize to a 256-color palette, encode the Sixel
        // envelope. Heavier than Kitty / iTerm2 because we own the rasterizer AND the scaler,
        // but it's the broadest fallback across legacy terminal emulators.
        if (capabilities.Graphics.Sixel && data.Format == ImageFormat.Png && !effectiveSize.IsEmpty)
        {
            try
            {
                var decoded = _resampledImage ?? PngDecoder.Decode(data.Bytes.Span);
                var (targetPxW, targetPxH) = ResolveTargetPixelSize(effectiveSize, capabilities);

                // Resample only when dimensions differ — source-matching targets get a direct
                // pass-through. Lanczos-3 is the high-quality default; for icon / UI content
                // the slight ringing it introduces is invisible at terminal scales, and detail
                // preservation beats Mitchell or Triangle at 2× / 3× downscales (common when
                // HiDPI source assets land on a SD terminal).
                if (decoded.Width != targetPxW || decoded.Height != targetPxH)
                {
                    decoded = ImageResampler.Resample(decoded, targetPxW, targetPxH);
                    _resampledImage = decoded;
                }

                return new SixelFragment(decoded.Rgba, decoded.Width, decoded.Height, effectiveSize, imageData: data);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                // Decode failure → fall through to the placeholder rather than crashing the render.
            }
        }

        return null;
    }

    /// <summary>
    /// Resolve the pixel dimensions a Sixel rendition should occupy from the resolved cell
    /// footprint. When the terminal's negotiated cell-pixel size is unknown (older terminals or
    /// transports that don't expose it), falls back to a typical-modern-terminal 10×20 cell —
    /// the resampled image still has sane dimensions; the worst case is a slight aspect-ratio
    /// mismatch versus the actual cell shape, which is recoverable by a re-render once the
    /// cell-pixel info becomes available.
    /// </summary>
    private static (int Width, int Height) ResolveTargetPixelSize(Size cellSize, OutputCapabilities capabilities)
    {
        int cellPxW = capabilities.Window.CellPixelWidth ?? 10;
        int cellPxH = capabilities.Window.CellPixelHeight ?? 20;
        int w = Math.Max(1, cellSize.Columns * cellPxW);
        int h = Math.Max(1, cellSize.Rows * cellPxH);
        return (w, h);
    }

    private static Size ResolveBaseSize(Size? requestedSize, Size renderSize)
    {
        if (requestedSize is not {} dataSize)
            return renderSize;

        return dataSize.IsEmpty
                   ? renderSize
                   : renderSize.IsEmpty
                       ? dataSize
                       : dataSize.ClampTo(renderSize);
    }

    internal static Size ResolveRenderSize(Size requestedSize, in Rect bounds, (int Width, int Height) pixelSize, OutputCapabilities capabilities)
    {
        if (requestedSize.IsEmpty)
            requestedSize = requestedSize with { Columns = bounds.Columns };

        var cellPixelSize = (Width: capabilities.Window.CellPixelWidth, Height: capabilities.Window.CellPixelHeight);
        var cellAspectRatio = (double?)cellPixelSize.Width / cellPixelSize.Height ?? 0.5;
        var aspectRatio = (double) pixelSize.Width / pixelSize.Height;
        
        // Adjust aspect ratio for cell aspect ratio if known
        var effectiveAspectRatio = aspectRatio / cellAspectRatio;

        Size preliminarySize;

        if (requestedSize is { Columns: 0, Rows: 0 })
        {
            if (cellPixelSize is (null, null))
                cellPixelSize = (DefaultCellPixelWidth, DefaultCellPixelHeight);

            if (cellPixelSize is ({} cpWidth, _))
            {
                var columns = (int) Math.Round((double) pixelSize.Width / cpWidth);
                preliminarySize = new Size(columns, (int) Math.Round(columns / effectiveAspectRatio));
            }
            else
            {
                // The requested size is empty: use decoded size clamped to bounds, preserving aspect ratio.
                int maxCols = bounds.Columns;
                int maxRows = bounds.Rows;

                int cols = Math.Min(requestedSize.Columns, maxCols);
                int rows = Math.Min(requestedSize.Rows, maxRows);

                // Clamp while preserving aspect ratio
                if (cols > maxCols)
                {
                    cols = maxCols;
                    rows = Math.Max(1, (int) (cols / effectiveAspectRatio));
                }

                if (rows > maxRows)
                {
                    rows = maxRows;
                    cols = Math.Max(1, (int) (rows * effectiveAspectRatio));
                }

                preliminarySize = new Size(cols, rows);
            }
        }
        else if (requestedSize is { Columns: > 0, Rows: 0 })
        {
            // Only columns specified: compute rows based on aspect ratio
            int cols = requestedSize.Columns;
            int rows = Math.Max(1, (int)Math.Round(cols / effectiveAspectRatio));
            preliminarySize = new Size(cols, rows);
        }
        else  if (requestedSize is { Columns: 0, Rows: > 0 })
        {
            // Only rows specified: compute columns based on aspect ratio
            int rows = requestedSize.Rows;
            int cols = Math.Max(1, (int) Math.Round(rows * effectiveAspectRatio));
            preliminarySize = new Size(cols, rows).ClampTo(bounds.Size);
        }
        else
        {
            preliminarySize = requestedSize;
        }

        if (preliminarySize.Columns > bounds.Columns)
            return new(bounds.Columns, (int) Math.Round(bounds.Columns / effectiveAspectRatio));

        if (preliminarySize.Rows > bounds.Rows)
            return new((int) Math.Round(bounds.Rows * effectiveAspectRatio), bounds.Rows);

        return preliminarySize;
    }

    protected override IContent BuildPlaceholder(Size size, OutputCapabilities capabilities, in CellStyle style)
    {
        var richText = TextMarkup.Parse(PlaceholderText, new TextMarkupOptions { DefaultStyle = BrushedStyle.FromStated(PlaceholderStyle) });
        var formatter = new TextFormatter { Wrap = WrapMode.WordWrap, Alignment = TextAlignment.Center };
        var formattedText = formatter.Format(richText, Math.Max(1, size.Columns), Math.Max(1, size.Rows), capabilities, fillEntireBounds: true);
        
        return formattedText;
    }
    
    protected static ImageData? LoadImage(Uri resourceUri, Size renderSize, IResourceLoader? loader = null)
    {
        loader ??= ResourceLoader.Default;

        var bytes = loader.TryLoadBytes(resourceUri);
        if (bytes is not null)
            return new ImageData(bytes, InferFormat(resourceUri), renderSize, Path.GetFileName(resourceUri.LocalPath));

        return null;
    }

    protected static ImageFormat InferFormat(Uri uri)
    {
        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
        var ext = Path.GetExtension(path).ToLowerInvariant();

        return ext switch
               {
                   ".png"            => ImageFormat.Png,
                   ".jpg" or ".jpeg" => ImageFormat.Jpeg,
                   ".gif"            => ImageFormat.Gif,
                   _                 => ImageFormat.Png
               };
    }
}
