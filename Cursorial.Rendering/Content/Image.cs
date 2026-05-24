using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Fragments;
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
    private readonly ImageData? _data;

    /// <summary>Construct an image content from the supplied data.</summary>
    public Image(ImageData? data, in Style placeholderStyle = default, string? placeholderText = null)
    {
        _data = data;
        PlaceholderStyle = placeholderStyle;
        PlaceholderText = placeholderText ?? "[p align=center]\\[image\\][/p]";
        Loader = ResourceLoader.Default;
        ResourceUri = null;
        RenderSize = _data?.RequestedSize ?? Size.Empty;
    }

    /// <summary>Construct an image content from the supplied data.</summary>
    public Image(Uri resourceUri, Size renderSize = default, in Style placeholderStyle = default, string? placeholderText = null, IResourceLoader? loader = null)
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

    /// <summary>The image payload + cell footprint.</summary>
    public ImageData? Data => _data;

    /// <summary>Style applied to the placeholder rectangle when no graphics protocol is supported.</summary>
    public Style PlaceholderStyle { get; init; }

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
        var cellPixels = (Width: capabilities.Window.CellPixelWidth, Height: capabilities.Window.CellPixelHeight);
        var cellRatio = (double?) capabilities.Window.CellPixelWidth / capabilities.Window.CellPixelHeight ?? 2.0;

        pixelSize = ImageSizeHelper.DecodeSize(_data!.Bytes.Span, _data.Format);
        
        //
        // HACK: Special code path for Sixel graphics, for which we don't support scaling at the moment.
        //
        if (capabilities is { Graphics: { KittyGraphics: false, ITerm2InlineImages: false, Sixel: true } })
        {
            if (cellPixels is ({} cpWidth, _))
            {
                var columns = (int) Math.Round((double) pixelSize.Width / cpWidth);
                var naturalSize = new Size(columns, (int) Math.Round(columns * cellRatio));

                //
                // TODO: We need to decide on a default behavior when an image doesn't fit in the provided bounds.
                //       Do we scale it down, overpaint, or attempt to clip it?
                //
                
                // if (naturalSize.Columns <= availableSpace.Columns && naturalSize.Rows <= availableSpace.Rows)
                    return naturalSize;
            }

            // Sixel doesn't support scaling at the moment.
            return Size.Empty;
        }
 
        var baseSize = ResolveBaseSize(_data?.RequestedSize, RenderSize);

        return ResolveRenderSize(baseSize, new Rect(0, 0, availableSpace), pixelSize, capabilities);
    }

    /// <inheritdoc/>
    protected override IBufferFragment? CreateFragment(in CellBufferView buffer, in Rect bounds, in Style style, OutputCapabilities capabilities)
    {
        if (capabilities.Graphics is {KittyGraphics: false, ITerm2InlineImages: false, Sixel: false})
            return null;

        var baseSize = ResolveBaseSize(_data?.RequestedSize, RenderSize);

        // No bytes → no transmittable payload, regardless of capability. This is the case
        // Icon hits when its resource URI didn't resolve: it constructs an Image with empty
        // bytes plus the configured fallback glyph, expecting the placeholder path. Without
        // this guard, a graphics-capable terminal would receive an empty fragment.
        if (_data?.Bytes.IsEmpty is not false) return null;

        var pixelSize = ImageSizeHelper.DecodeSize(_data.Bytes.Span, _data.Format);
        var effectiveSize = ResolveRenderSize(baseSize, bounds, pixelSize, capabilities);
        
        // // Kitty first — supports PNG natively, has the most predictable cell-footprint semantics.
        if (capabilities.Graphics.KittyGraphics && _data.Format == ImageFormat.Png)
            return new KittyImageFragment(_data, effectiveSize);

        // iTerm2 second — accepts PNG / JPEG / GIF; format hint passes through unchanged.
        if (capabilities.Graphics.ITerm2InlineImages)
            return new ITerm2ImageFragment(_data, effectiveSize, pixelSize);

        // Sixel third — PNG only (we don't decode JPEG / GIF). PNG path: decode to RGBA, quantize
        // to a 256-color palette, encode the Sixel envelope. Heavier than Kitty / iTerm2 because
        // we own the rasterizer, but it's the broadest fallback across legacy terminal emulators.
        if (capabilities.Graphics.Sixel && _data.Format == ImageFormat.Png)
        {
            try
            {
                // TODO: Fix this shite once we implement scaling for Sixel.
                effectiveSize = DesiredSize ?? /* HACK! Should not calling Measure() be an error? */ MeasureOverride(bounds.Size, capabilities, out _);

                if (effectiveSize.IsEmpty is false)
                {
                    var decoded = PngDecoder.Decode(_data.Bytes.Span);
                    return new SixelFragment(decoded.Rgba, pixelSize.Width, pixelSize.Height, effectiveSize, imageData: _data);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                // Decode failure → fall through to the placeholder rather than crashing the render.
            }
        }

        return null;
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
        var cellPixelSize = (Width: capabilities.Window.CellPixelWidth, Height: capabilities.Window.CellPixelHeight);
        var cellAspectRatio = (double?)cellPixelSize.Width / cellPixelSize.Height ?? 0.5;
        var aspectRatio = (double) pixelSize.Width / pixelSize.Height;
        
        // Adjust aspect ratio for cell aspect ratio if known
        var effectiveAspectRatio = aspectRatio / cellAspectRatio;

        Size preliminarySize;

        if (requestedSize is { Columns: 0, Rows: 0 })
        {
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

    protected override IContent BuildPlaceholder(Size size, OutputCapabilities capabilities)
    {
        var richText = TextMarkup.Parse(PlaceholderText, new TextMarkupOptions { DefaultStyle = PlaceholderStyle });
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
