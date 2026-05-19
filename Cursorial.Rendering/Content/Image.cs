using System.ComponentModel.DataAnnotations;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Fragments;
using Cursorial.Text;

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
/// and on WezTerm. Sixel will join the chain once a sixel encoder is in place; today there's
/// no upstream PNG → sixel converter in this repo so Sixel is silently skipped even on
/// terminals that report support.
/// </para>
/// <para>
/// <b>Format compatibility.</b> Kitty only accepts PNG via the encoded-bytes transmission
/// path; if the supplied <see cref="ImageData.Format"/> is JPEG or GIF and Kitty is the only
/// supported protocol, this content treats Kitty as unsupported and falls through. Callers
/// w`ho need cross-protocol compatibility should either re-encode upstream or supply PNG.
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
        PlaceholderText = placeholderText ?? "[image]";
        Loader = ResourceLoader.Default;
        ResourceUri = null;
        RenderSize = _data?.CellSize ?? Size.Empty;
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
    protected override Size MeasureOverride(Size availableSpace, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        // The image's natural footprint is _data.CellSize. We don't downscale here — the
        // protocol fragments carry that cell footprint on the wire, and an arbitrary smaller
        // size would require re-encoding the image. Callers wanting an actual fit-to-bounds
        // should re-construct the ImageData with the desired CellSize.
        var natural = _data?.CellSize;
        int cols = natural is { Columns: var c } ? Math.Min(c, availableSpace.Columns) : availableSpace.Columns;
        int rows = natural is { Rows: var r } ? Math.Min(r, availableSpace.Rows) : availableSpace.Rows;
        return new Size(cols, rows);
    }

    /// <inheritdoc/>
    protected override IBufferFragment? CreateFragment(CellBuffer buffer, Rect bounds, in Style style, OutputCapabilities capabilities)
    {
        // No bytes → no transmittable payload, regardless of capability. This is the case
        // Icon hits when its resource URI didn't resolve: it constructs an Image with empty
        // bytes plus the configured fallback glyph, expecting the placeholder path. Without
        // this guard, a graphics-capable terminal would receive an empty fragment.
        if (_data?.Bytes.IsEmpty is not false) return null;

        // Kitty first — supports PNG natively, has the most predictable cell-footprint semantics.
        if (capabilities.Graphics.KittyGraphics && _data.Format == ImageFormat.Png)
            return new KittyImageFragment(_data);

        // iTerm2 second — accepts PNG / JPEG / GIF; format hint passes through unchanged.
        if (capabilities.Graphics.ITerm2InlineImages)
            return new ITerm2ImageFragment(_data);

        return null;
    }

    /// <inheritdoc/>
    protected override Rect PaintPlaceholder(CellBuffer buffer, Rect bounds, in Style style, OutputCapabilities capabilities)
    {
        // Effective placeholder style: the content's PlaceholderStyle wins, falling back to the
        // caller-supplied style when no placeholder was configured. The caller's style is what
        // would have been the SGR backdrop for a real image fragment, so reusing it produces a
        // visually coherent "where the image would have been" affordance.
        var fillStyle = PlaceholderStyle == Style.Default ? style : PlaceholderStyle;
        var cellSize = _data?.CellSize ?? bounds.Size;
        var rowSpan = cellSize.Rows;

        if (rowSpan is 0)
            rowSpan = Math.Max(1, cellSize.Columns / 2);

        // Clip the placeholder to the smaller of the natural footprint, the allocated bounds,
        // and the buffer extent.
        int colWidth = Math.Min(cellSize.Columns, bounds.Columns);
        int rowHeight = Math.Min(rowSpan, bounds.Rows);
        int colEnd = Math.Min(buffer.Columns, bounds.Column + colWidth);
        int rowEnd = Math.Min(buffer.Rows, bounds.Row + rowHeight);

        for (var r = rowEnd - 1; r >= bounds.Row; r--)
        {
            for (var c = bounds.Column; c < colEnd; c++)
                buffer.Set(c, r, " ", fillStyle);
        }

        // Center some custom text in the placeholder when there's room. Bog-standard ASCII, so
        // it always fits — callers wanting localization should supply their own placeholder
        // content via a custom IContent.
        var label = PlaceholderText;
        var labelLength = GraphemeWidth.StringWidth(label);

        if (colWidth >= labelLength && rowHeight >= 1)
        {
            var labelRow = bounds.Row + rowHeight / 2;
            var labelCol = bounds.Column + (colWidth - labelLength) / 2;

            if (labelRow >= 0 && labelRow < buffer.Rows)
            {
                var advance = 0;
                var enumerator = label.GetGraphemeEnumerator();

                while (enumerator.MoveNext())
                {
                    var cluster = enumerator.Current;
                    var width = GraphemeWidth.ClusterWidth(cluster);

                    if (labelCol + advance + width > buffer.Columns) break;

                    buffer.Set(labelCol + advance, labelRow, enumerator.Current.ToString(), fillStyle);

                    advance += width;
                }
            }
        }

        var paintedCols = Math.Min(colWidth, Math.Max(0, buffer.Columns - bounds.Column));
        var paintedRows = Math.Min(rowHeight, Math.Max(0, buffer.Rows - bounds.Row));

        return bounds.WithSize(new Size(paintedCols, paintedRows));
    }
    
    protected static ImageData? LoadImage(Uri resourceUri, Size renderSize, IResourceLoader? loader = null)
    {
        loader ??= ResourceLoader.Default;

        var bytes = loader.TryLoadBytes(resourceUri);
        if (bytes is not null)
            return new ImageData(bytes, InferFormat(resourceUri), renderSize);

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
